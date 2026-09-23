using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 引擎 wire 层（定稿）：JSON-RPC 2.0 端点，与内存层同一语义。
///
/// <para><b>方法路由</b>：<c>engine.execute</c>（params = { command, args?, options? }）、
/// <c>engine.query</c>（params = { command, args? }）、<c>engine.describe</c>（params = { category? }），
/// 其余 method 一律视为直接命令名（params = args；按 Descriptor 的 Query/Mutation 标志路由）。</para>
///
/// <para><b>响应</b>：成功 = <c>{ jsonrpc, id, result }</c>；execute/变更命令的 result =
/// <c>{ ok, data, changes, audit_ref }</c>（snake_case），查询的 result = 数据本体。</para>
///
/// <para><b>错误</b>：JSON-RPC error.code 数值映射（-32600 请求体非法 / -32601 未知命令 /
/// -32602 校验类 / -32000 其余引擎错误），完整 <see cref="EngineError"/>（code/message/details/
/// retryable/correlation_id）放 error.data——兼容规范又保留结构化信息。</para>
///
/// <para>事件：提交成功后的领域事件由 <see cref="IEventBus"/> 推送（宿主订阅后自行转 wire 通知），
/// 不在响应内联——与旧协议的事件通道形态一致（前端防抖行为不变）。</para>
/// </summary>
public sealed class EngineWire(IEngine engine)
{
    /// <summary>命令目录缓存（构造期解析一次；registry 注册完成后再构造 wire）。</summary>
    private readonly Dictionary<string, CommandDescriptor> _commands =
        engine.Describe(null).Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<string> HandleAsync(string jsonRequest, CancellationToken ct = default)
    {
        // JSON-RPC：空/非法请求体 → -32600；Deserialize(null) 会抛 ArgumentNullException
        // 落到通用 catch 变成 -32000，分类错误。
        if (jsonRequest is null)
            return Error(hasId: false, default, -32600, "request body must not be empty");
        JsonElement request;
        var hasId = false;
        JsonElement idValue = default;

        try
        {
            request = JsonSerializer.Deserialize<JsonElement>(jsonRequest);

            // 先取 id：即使 method 缺失/非法，错误响应也必须回传请求的 id（JSON-RPC 关联语义，
            // 否则 host 无法把错误响应对齐到对应请求——曾因先校验 method 后取 id，非法 method
            // 的错误帧 id 恒为 null）。
            if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var idEl)
                && idEl.ValueKind is not JsonValueKind.Undefined)
            {
                hasId = true;
                idValue = idEl.Clone();
            }

            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("method", out var methodEl)
                || methodEl.ValueKind != JsonValueKind.String)
            {
                return Error(hasId, idValue, -32600, "request body is not a valid JSON-RPC 2.0 object");
            }

            var method = methodEl.GetString()!;
            request.TryGetProperty("params", out var paramsEl);
            var result = await DispatchAsync(method, paramsEl, ct);
            return Response(hasId, idValue, result);
        }
        catch (JsonException ex)
        {
            return Error(hasId, idValue, -32600, $"request body JSON parse failed: {ex.Message}");
        }
        catch (EngineException ex)
        {
            var (code, _) = MapError(ex.Error.Code);
            return Error(hasId, idValue, code, ex.Error.Message, ex.Error);
        }
        catch (OperationCanceledException)
        {
            return Error(hasId, idValue, -32000, "Call cancelled");
        }
        catch (Exception ex)
        {
            return Error(hasId, idValue, -32000, $"internal error: {ex.Message}");
        }
    }

    /// <summary>方法分发（engine.* 三标准方法 + 直接命令名）。</summary>
    private async Task<object?> DispatchAsync(string method, JsonElement args, CancellationToken ct)
    {
        switch (method)
        {
            case "engine.execute":
            {
                var command = Require(args, "command");
                var wireArgs = args.TryGetProperty("args", out var a) ? a.Clone() : (JsonElement?)null;
                var options = args.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Object
                    ? DeserializeOptions(oEl)
                    : null;
                var r = await engine.ExecuteAsync<object>(command, wireArgs, options, ct);
                return ToWireResult(r);
            }
            case "engine.query":
            {
                var command = Require(args, "command");
                var wireArgs = args.TryGetProperty("args", out var a) ? a.Clone() : (JsonElement?)null;
                return ToWireData(await engine.QueryAsync<object>(command, wireArgs, null, ct));
            }
            case "engine.describe":
            {
                var category = args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("category", out var c)
                    && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                return engine.Describe(category);
            }
            case "batch.run" or "batch.dry_run" or "batch.status":
            {
                // 编排命令（wire 方法 = engine.* 三标准方法 + <编排命令>）：
                // 批引擎自身即管道父调用，直路由 IBatchEngine，不经标准命令管道。
                var batch = engine.Batch
                    ?? throw new EngineException(EngineErrors.Of(EngineErrors.Internal, "batch engine not wired (OrchestrationHost)"));
                return method switch
                {
                    "batch.run" => await batch.RunAsync(
                        ParseScript(RequireElement(args, "script")),
                        args.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Object
                            ? DeserializeOptions(oEl) : null, ct),
                    "batch.dry_run" => await batch.DryRunAsync(ParseScript(RequireElement(args, "script")), ct),
                    _ => batch.GetStatus(Require(args, "batch_id"))
                        ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound,
                            $"batch not found: {Require(args, "batch_id")}")),
                };
            }
            default:
            {
                // 直接命令名：params = args；按目录里的 Query/Mutation 标志路由
                if (!_commands.TryGetValue(method, out var descriptor))
                    throw new EngineException(EngineErrors.Of(EngineErrors.UnknownCommand,
                        $"unknown command '{method}'"));

                if (descriptor.IsQuery)
                    return ToWireData(await engine.QueryAsync<object>(method, args, null, ct));

                var r = await engine.ExecuteAsync<object>(method, args, null, ct);
                return ToWireResult(r);
            }
        }
    }

    /// <summary>查询结果 = 数据本体（JSON 可序列化：DTO / record / JsonElement 均直接落形）。</summary>
    private static object? ToWireData(object? data) => data;

    private static BatchScript ParseScript(JsonElement scriptEl) => BatchDispatch.ParseScript(scriptEl);

    /// <summary>必填 JSON 元素参数（复杂入参，如批脚本）；缺失即抛 REQUIRED_PARAM。</summary>
    private static JsonElement RequireElement(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el)
            && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return el;

        throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
            $"required parameter '{name}' is missing", details: JsonSerializer.SerializeToElement(new { param = name })));
    }

    private static CallOptions DeserializeOptions(JsonElement el)
        => JsonSerializer.Deserialize<CallOptions>(el.GetRawText(), WireOptions) ?? new CallOptions();

    /// <summary>写流响应结果：<c>{ ok, data, changes, audit_ref }</c>。changes 走载荷唯一投影
    /// （<see cref="ChangeSetPayload"/>：与审计 changes_json / 事件负载同形状，diff 上限 2000 条 + 截断标记）。</summary>
    private static object ToWireResult<T>(CommandResult<T> r) => new
    {
        ok = r.Ok,
        data = r.Data,
        audit_ref = r.AuditRef,
        changes = r.Changes == null ? (JsonElement?)null : ChangeSetPayload.From(r.Changes),
    };

    private static string Require(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
            return el.GetString()!;

        throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
            $"required parameter '{name}' is missing", details: JsonSerializer.SerializeToElement(new { param = name })));
    }

    /// <summary>错误码 → JSON-RPC 数值码（约定②）。</summary>
    private static (int Code, bool) MapError(string engineCode) => engineCode switch
    {
        EngineErrors.UnknownCommand => (-32601, true),
        EngineErrors.ProtocolMalformed => (-32600, true),
        _ when engineCode.StartsWith("LP.VAL", StringComparison.Ordinal) => (-32602, true),
        _ => (-32000, false),
    };

    private static string Response(bool hasId, JsonElement id, object? result)
        => JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = hasId ? id.Clone() : null,
                ["result"] = result,
            }, WireOptions);

    private static string Error(bool hasId, JsonElement id, int code, string message, EngineError? error = null)
        => JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = hasId ? id.Clone() : null,
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["data"] = error ?? EngineErrors.Of(EngineErrors.Internal, message),
                },
            }, WireOptions);
}

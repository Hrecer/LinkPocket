using System.Text.Encodings.Web;
using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Mcp;

/// <summary>
/// MCP（Model Context Protocol）stdio 网关：把外部 Agent 的工具调用翻译成 **LinkPocket 命令行的同一次执行**。
///
/// <para><b>协议形态</b>：stdin 逐行读 JSON-RPC 2.0 消息、stdout 逐行写响应；stdout 只承载协议帧，
/// 引擎日志走 stderr（<c>EngineHost(logToStderr: true)</c>）。</para>
///
/// <para><b>能力等价</b>：工具清单 = 契约层 <see cref="AiToolCatalog.BuildAll"/>（与内置助手同一份工具定义，
/// 逐字同源）；工具调用 = 进程内复用 <c>LinkPocket.Cli.CliRunner</c>（与人在终端敲命令同一条路径）。
/// 因此外部 Agent 与界面、内置助手在能力上同层——看得到数据、改得动数据、可空跑，无阉割、无第二套实现。</para>
///
/// <para><b>确认口径</b>：网关对每次调用自动带上 CLI 的 <c>--yes</c>（用户把网关挂到自己的 Agent 上即代表授权），
/// 破坏性命令由引擎的两阶段令牌在同一次调用内自动完成；需要先看影响面时用工具参数 <c>_dry_run</c>。</para>
/// </summary>
public sealed class McpServer
{
    /// <summary>本实现面向的 MCP 协议版本。</summary>
    public const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly McpSurface _surface;

    private McpServer(EngineHost host) => _surface = new McpSurface(host);

    /// <summary>进程入口：解析 <c>--db</c>、装配宿主、跑 stdio 循环。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var host = new EngineHost(Value(args, "--db"), logToStderr: true);
        LpLog.Info($"mcp gateway ready (db={host.DatabasePath}, commands={host.Commands.Count})", category: "mcp");
        return await Create(host).LoopAsync(Console.In, Console.Out).ConfigureAwait(false);
    }

    /// <summary>在已装配的宿主上建一个网关实例（进程入口与进程内验证共用；不启动 IO 循环）。</summary>
    public static McpServer Create(EngineHost host) => new(host);

    /// <summary>逐行读请求、逐行写响应；EOF（客户端关闭 stdin）即结束。</summary>
    public async Task<int> LoopAsync(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = await input.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await HandleAsync(line).ConfigureAwait(false);
            if (response is null) continue;   // notification：按协议不回响应
            await output.WriteLineAsync(response).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        LpLog.Info("mcp gateway exiting (stdin closed)", category: "mcp");
        return 0;
    }

    /// <summary>处理一条 JSON-RPC 消息；返回 null = notification（无响应）。</summary>
    public async Task<string?> HandleAsync(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ErrorResponse(null, -32700, $"parse error: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ErrorResponse(null, -32600, "request must be a JSON object");

            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
                return hasId ? ErrorResponse(id, -32600, "request must carry a string 'method'") : null;

            var method = methodEl.GetString()!;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;

            // 通知（notifications/*）：处理但不回响应。
            if (!hasId && method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                if (method == "notifications/initialized") LpLog.Debug("client initialized", category: "mcp");
                return null;
            }

            try
            {
                var result = await DispatchAsync(method, parameters).ConfigureAwait(false);
                return SuccessResponse(hasId ? id : default, hasId, result);
            }
            catch (McpException ex)
            {
                return hasId ? ErrorResponse(id, ex.Code, ex.Message) : null;
            }
            catch (Exception ex)
            {
                return hasId ? ErrorResponse(id, -32603, ex.Message) : null;
            }
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement parameters) => method switch
    {
        "initialize" => Initialize(),
        "notifications/initialized" => null,
        "notifications/cancelled" => null,
        "ping" => new Dictionary<string, object?>(),
        "tools/list" => _surface.ToolsList(),
        "tools/call" => await _surface.ToolsCallAsync(parameters).ConfigureAwait(false),
        "resources/list" => _surface.ResourcesList(),
        "resources/templates/list" => _surface.ResourceTemplates(),
        "resources/read" => await _surface.ResourcesReadAsync(parameters).ConfigureAwait(false),
        _ => throw new McpException(-32601, $"method not found: {method}"),
    };

    private object Initialize() => new Dictionary<string, object?>
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?>(),
            ["resources"] = new Dictionary<string, object?> { ["subscribe"] = false, ["listChanged"] = false },
        },
        ["serverInfo"] = new Dictionary<string, object?>
        {
            ["name"] = "linkpocket",
            ["version"] = Cli.CliRunner.ProductVersion,
        },
        ["instructions"] =
            "Drive the user's LinkPocket bookmark library. Every engine command is exposed as a tool "
            + "(names, descriptions and argument schemas are identical to the ones the in-app assistant uses). "
            + "Read data with query tools (folders.*, links.*, search.*, trash.*); change it with mutation tools. "
            + "Every tool accepts an extra boolean argument '_dry_run' that previews the effect without committing. "
            + "The resources below expose the whole library (docs, tree, links, trash, stats, snapshot).",
    };

    private static string SuccessResponse(JsonElement id, bool hasId, object? result)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = hasId ? id.Clone() : null,
            ["result"] = result,
        }, Json);

    private static string ErrorResponse(JsonElement? id, int code, string message)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.Clone(),
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        }, Json);

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}

/// <summary>网关把协议层的错误（方法缺失、参数不合法）以 JSON-RPC 错误码回给客户端。</summary>
public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

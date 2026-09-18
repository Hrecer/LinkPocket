using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Engine;

/// <summary>
/// 批引擎（方案 4.3 IBatchEngine / 2.3 批流 / 3.5 并发事务语义）。
/// 事务批（Transactional，默认）：持有写闸 + 单一工作单元，每步经嵌套派发复用父 UoW；
/// 步骤全部成功才提交（abort / 抛异常 = 不提交即回滚）；dry_run 回滚事务。
/// 独立批（Independent）：每步走完整顶层管道（各自隐式事务）；abort 只停后续步骤，已执行步骤保持生效。
/// 步骤间结果经 <c>{ref.path}</c> 模板传递；报告含每步结果 + 总变更集 + 审计关联。
/// batch.run / batch.dry_run / batch.status 三个命令不进命令注册表——由 wire 层直接路由到本引擎
/// （批 = 编排层，自身即管道父调用，不走标准命令管道）。
/// </summary>
public sealed class BatchEngine : IBatchEngine
{
    /// <summary>批三命令的描述符（目录自描述：EngineCatalog.Manifest 追加，registry 不注册）。</summary>
    public static readonly IReadOnlyList<CommandDescriptor> Descriptors =
    [
        new("batch.run", "batch", "按脚本顺序执行一批命令（事务批共享一个工作单元，abort 整批回滚；独立批每步各自提交）",
            [ParamSpec.Req<JsonElement>("script", "批脚本 { name, steps: [{ ref, command, args, on_error }], scope }")],
            CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.SupportsCancellation),
        new("batch.dry_run", "batch", "预演批脚本：全步骤执行但不提交，返回每步结果与影响面，零副作用",
            [ParamSpec.Req<JsonElement>("script", "批脚本")],
            CommandCaps.Query | CommandCaps.LongRunning | CommandCaps.SupportsCancellation),
        new("batch.status", "batch", "查询批运行状态（batch_id 由 batch.run 的报告/错误详情下发）",
            [ParamSpec.Req<string>("batch_id", "批 ID")],
            CommandCaps.Query),
    ];

    /// <summary>批状态字典容量上限：宿主长跑（AI 轮询/宏）会持续新增 batch_id，
    /// 无上限会无限膨胀（每个 BatchStatus 还挂着步骤结果）。超限清理已完成/中止的旧条目。</summary>
    private const int MaxStatusEntries = 256;

    private readonly EngineCore _engine;
    private readonly ConcurrentDictionary<string, BatchStatus> _status = new(StringComparer.Ordinal);

    public BatchEngine(EngineCore engine)
        => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    private void TrackStatus(string batchId, BatchStatus status)
    {
        _status[batchId] = status;
        if (_status.Count <= MaxStatusEntries) return;
        // 只清终态（running 的进行中批保留）：移除任意已完成条目，凑回容量之内
        var stale = _status
            .Where(kv => kv.Value.State is "completed" or "failed" or "aborted")
            .Select(kv => kv.Key)
            .Take(64)
            .ToList();
        foreach (var key in stale) _status.TryRemove(key, out _);
    }

    public Task<BatchReport> RunAsync(BatchScript script, CallOptions? options = null, CancellationToken ct = default)
        => RunCoreAsync(script, dryRun: options?.DryRun == true, options, ct);

    public Task<BatchReport> DryRunAsync(BatchScript script, CancellationToken ct = default)
        => RunCoreAsync(script, dryRun: true, options: null, ct);

    public BatchStatus? GetStatus(string batchId)
        => _status.TryGetValue(batchId, out var status) ? status : null;

    // ===== 核心执行 =====

    private async Task<BatchReport> RunCoreAsync(BatchScript script, bool dryRun, CallOptions? options, CancellationToken ct)
    {
        ValidateScript(script);
        var batchId = Guid.NewGuid().ToString("N");
        var correlationId = options?.CorrelationId ?? $"batch:{batchId}";
        var caller = options?.Caller ?? new CallerRef(CallerKind.Batch, batchId);
        var sw = Stopwatch.StartNew();

        TrackStatus(batchId, new BatchStatus(batchId, script.Name, "running", 0, script.Steps.Count));

        List<BatchStepResult> results;
        var touched = new List<EntityRef>();
        var events = new List<string>();
        try
        {
            if (script.Scope == BatchScope.Transactional)
            {
                (results, touched, events) = await RunTransactionalAsync(script, dryRun, correlationId, caller, ct);
            }
            else
            {
                results = await RunIndependentAsync(script, dryRun, correlationId, caller, batchId, ct);
            }
        }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.BatchAborted)
        {
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", _status[batchId].CompletedSteps, script.Steps.Count));
            throw;   // 嵌套步骤循环已带报告详情（batch_id + step）
        }
        catch (EngineException ex) when (ex.Error.Code is EngineErrors.TypeMismatch or EngineErrors.RequiredParam or EngineErrors.ProtocolMalformed)
        {
            // 校验类错误（含模板坏引用）零副作用，按原错误码透传，不包装成批失败
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", _status[batchId].CompletedSteps, script.Steps.Count));
            throw;
        }
        catch (EngineException ex)
        {
            // 事务批中途异常：工作单元未提交已回滚
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", _status[batchId].CompletedSteps, script.Steps.Count));
            throw new EngineException(EngineErrors.Of(
                EngineErrors.BatchAborted,
                $"批「{script.Name}」执行失败（{ex.Error.Code}）：事务批已整体回滚",
                details: JsonSerializer.SerializeToElement(new { batch_id = batchId, error = ex.Error }),
                correlationId: correlationId));
        }
        catch (OperationCanceledException)
        {
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", _status[batchId].CompletedSteps, script.Steps.Count));
            throw;
        }

        var report = BuildReport(batchId, script.Name, results, touched, events, sw.ElapsedMilliseconds, correlationId);
        TrackStatus(batchId, new BatchStatus(batchId, script.Name, report.Ok ? "completed" : "failed",
            results.Count, script.Steps.Count));

        // 父级审计条目（batch_id 列关联；每步已有 IsNested 子记录）
        _engine.Audit.Write(new AuditEntry(
            DateTimeOffset.Now, "batch.run", correlationId, caller, sw.ElapsedMilliseconds,
            Success: report.Ok, ErrorCode: report.Ok ? null : EngineErrors.BatchAborted,
            Changes: report.Changes, DryRun: dryRun, IsNested: false, StackTrace: null, BatchId: batchId));

        return report;
    }

    /// <summary>事务批：写闸 + 单 UoW + 嵌套派发（abort/异常 = 不提交即回滚）。</summary>
    private async Task<(List<BatchStepResult> Results, List<EntityRef> Touched, List<string> Events)> RunTransactionalAsync(
        BatchScript script, bool dryRun, string correlationId, CallerRef caller, CancellationToken ct)
    {
        await _engine.WriteGate.WaitAsync(ct);
        try
        {
            await using var uow = _engine.UowFactory();
            ITransactionScope? tx = dryRun ? uow.BeginTransaction() : null;
            CommandContextImpl ctx;
            List<BatchStepResult> results;
            List<EntityRef> touched;
            List<string> events;
            try
            {
                ctx = new CommandContextImpl(uow, isNested: false, dryRun, correlationId, caller, ct, _engine);
                (results, touched, events) = await RunStepsNestedAsync(ctx, script, ct);

                if (dryRun)
                {
                    if (tx != null) await tx.RollbackAsync(ct);
                }
                else
                {
                    await uow.CommitAsync(ct);
                }
            }
            finally
            {
                if (tx != null) await tx.DisposeAsync();
            }

            if (!dryRun)
            {
                // 事件发布：嵌套事件已随执行入父缓冲，提交成功后统一发布一次
                // （不变量：订阅方不得同步回派命令；发布路径统一走 EngineCore.PublishAsync → 缓存世代戳同步推进）
                var merged = ctx.TakeNestedChanges();
                if (merged.Events.Count > 0)
                {
                    var payload = JsonSerializer.SerializeToElement(merged, EngineJson.Options);
                    foreach (var name in merged.Events)
                        await _engine.PublishAsync(new DomainEvent(name, DateTimeOffset.Now, payload, correlationId, caller));
                }
            }
            return (results, touched, events);
        }
        finally
        {
            _engine.WriteGate.Release();
        }
    }

    /// <summary>独立批：每步走完整顶层管道（各自隐式事务；abort 只停后续步骤，已执行步骤保持生效）。
    /// 中止（OnError=Abort 失败）通过抛 <see cref="EngineErrors.BatchAborted"/> 表达，由调用方 catch 转状态——
    /// 本方法只返回逐步骤结果，不再有「aborted 标志」这一恒 false 的死字段。</summary>
    private async Task<List<BatchStepResult>> RunIndependentAsync(
        BatchScript script, bool dryRun, string correlationId, CallerRef caller, string batchId, CancellationToken ct)
    {
        var results = new List<BatchStepResult>();
        var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in script.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var stepSw = Stopwatch.StartNew();
            var args = BatchTemplate.Resolve(step.Args, refs);
            try
            {
                var r = await _engine.ExecuteAsync<object>(step.Command, args, new CallOptions(
                    DryRun: dryRun, CorrelationId: $"{correlationId}:{step.Ref}", Caller: caller), ct);
                TrackStepData(refs, step.Ref, r.Data);
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: true, Skipped: false,
                    ToElement(r.Data), null, null, stepSw.ElapsedMilliseconds));
            }
            catch (EngineException ex)
            {
                if (step.OnError == ErrorPolicy.Abort)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.BatchAborted,
                        $"批「{script.Name}」步骤「{step.Ref}」（{step.Command}）失败：{ex.Error.Message}" +
                        "（独立批：失败前的已执行步骤保持生效）",
                        details: JsonSerializer.SerializeToElement(new { batch_id = batchId, step = step.Ref }),
                        correlationId: correlationId));
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: false,
                    Skipped: step.OnError == ErrorPolicy.SkipAndLog, null, ex.Error.Code, ex.Error.Message,
                    stepSw.ElapsedMilliseconds));
            }
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "running", results.Count, script.Steps.Count));
        }
        return results;
    }

    /// <summary>
    /// 嵌套步骤循环（事务批与宏运行共用）：在既有管道上下文内逐步嵌套派发。
    /// 策略 Abort 的步骤失败直接抛出（由调用方的管道回滚）；Continue/SkipAndLog 记录后继续。
    /// 返回步骤结果 + 聚合变更集（Touched/Events，供批报告）。
    /// </summary>
    internal static async Task<(List<BatchStepResult> Results, List<EntityRef> Touched, List<string> Events)> RunStepsNestedAsync(
        CommandContextImpl ctx, BatchScript script, CancellationToken ct)
    {
        var results = new List<BatchStepResult>();
        var touched = new List<EntityRef>();
        var events = new List<string>();
        var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in script.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var stepSw = Stopwatch.StartNew();
            var args = BatchTemplate.Resolve(step.Args, refs);
            try
            {
                var r = await ctx.Engine.ExecuteNestedAsync(ctx, step.Command, args, ct);
                TrackStepData(refs, step.Ref, r.Data);
                if (r.Changes is { } changes)
                {
                    touched.AddRange(changes.Touched);
                    events.AddRange(changes.Events);
                }
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: true, Skipped: false,
                    ToElement(r.Data), null, null, stepSw.ElapsedMilliseconds));
            }
            catch (EngineException ex)
            {
                if (step.OnError == ErrorPolicy.Abort)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.BatchAborted,
                        $"批「{script.Name}」步骤「{step.Ref}」（{step.Command}）失败：{ex.Error.Message}",
                        details: JsonSerializer.SerializeToElement(new { batch_id = ctx.CorrelationId, step = step.Ref }),
                        correlationId: ctx.CorrelationId));
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: false,
                    Skipped: step.OnError == ErrorPolicy.SkipAndLog, null, ex.Error.Code, ex.Error.Message,
                    stepSw.ElapsedMilliseconds));
            }
        }
        return (results, touched, events);
    }

    // ===== 结果聚合 / 校验 / 工具 =====

    private static void TrackStepData(Dictionary<string, JsonElement> refs, string stepRef, object? data)
    {
        if (string.IsNullOrWhiteSpace(stepRef)) return;
        refs[stepRef] = ToElement(data) ?? JsonSerializer.Deserialize<JsonElement>("null");
    }

    internal static JsonElement? ToElement(object? data)
        => data switch
        {
            null => null,
            JsonElement e => e,
            _ => JsonSerializer.SerializeToElement(data, EngineJson.Options),
        };

    private static void ValidateScript(BatchScript script)
    {
        if (script.Steps.Count == 0)
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "批脚本至少需要一个步骤", details: JsonSerializer.SerializeToElement(new { @param = "steps" })));
        if (script.Steps.Any(s => string.IsNullOrWhiteSpace(s.Command)))
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "批脚本每步必须指定 command", details: JsonSerializer.SerializeToElement(new { @param = "steps[].command" })));
        var dup = script.Steps.Select(s => s.Ref).Where(r => !string.IsNullOrWhiteSpace(r))
            .GroupBy(r => r, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                $"批步骤引用名「{dup.Key}」重复", details: JsonSerializer.SerializeToElement(new { @param = "steps[].ref" })));
    }

    private static BatchReport BuildReport(string batchId, string name, List<BatchStepResult> results,
        List<EntityRef> touched, List<string> events, long elapsedMs, string correlationId)
    {
        var ok = results.All(r => r.Ok);
        var summary = ok
            ? $"批「{name}」完成：{results.Count} 步全部成功"
            : $"批「{name}」完成（有失败）：{results.Count(r => r.Ok)}/{results.Count} 步成功";

        return new BatchReport(batchId, name, ok, results,
            new ChangeSet(touched, events, summary), summary, elapsedMs, correlationId);
    }
}

/// <summary>
/// 批步骤模板解析：<c>{ref}</c> / <c>{ref.path.sub}</c> 引用更早步骤的结果数据。
/// 整串精确匹配 = 直接替换为结果元素（任意类型）；串内嵌引用 = 以字符串形式替换（标量取值、复合取原始 JSON）。
/// </summary>
internal static class BatchTemplate
{
    public static JsonElement Resolve(JsonElement args, IReadOnlyDictionary<string, JsonElement> refs)
        => args.ValueKind switch
        {
            JsonValueKind.Object => ResolveObject(args, refs),
            JsonValueKind.Array => ResolveArray(args, refs),
            JsonValueKind.String => ResolveString(args.GetString()!, refs),
            _ => args.Clone(),
        };

    private static JsonElement ResolveObject(JsonElement obj, IReadOnlyDictionary<string, JsonElement> refs)
    {
        var target = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in obj.EnumerateObject())
            target[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(Resolve(prop.Value, refs).GetRawText());
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonElement ResolveArray(JsonElement arr, IReadOnlyDictionary<string, JsonElement> refs)
    {
        var target = new System.Text.Json.Nodes.JsonArray();
        foreach (var item in arr.EnumerateArray())
            target.Add(System.Text.Json.Nodes.JsonNode.Parse(Resolve(item, refs).GetRawText()));
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonElement ResolveString(string text, IReadOnlyDictionary<string, JsonElement> refs)
    {
        // 整串精确引用：{ref} 或 {ref.a.b} → 替换为结果元素本体
        if (text.StartsWith('{') && text.EndsWith('}') && IsRefToken(text.AsSpan(1, text.Length - 2), out var token))
        {
            if (TryLookup(token, refs, out var value)) return value.Clone();
            throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                $"模板引用「{text}」找不到对应的步骤结果（引用必须在被引用步骤之后）"));
        }
        return JsonSerializer.SerializeToElement(ReplaceInline(text, refs));
    }

    private static string ReplaceInline(string text, IReadOnlyDictionary<string, JsonElement> refs)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\{(?<token>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)\}", match =>
        {
            var token = match.Groups["token"].Value;
            if (!TryLookup(token, refs, out var value)) return match.Value;   // 非引用令牌原样保留
            return value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? value.GetRawText()
                : value.ToString();
        });

    private static bool IsRefToken(ReadOnlySpan<char> span, out string token)
    {
        token = span.ToString();
        if (token.Length == 0) return false;
        foreach (var part in token.Split('.'))
        {
            if (part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    private static bool TryLookup(string token, IReadOnlyDictionary<string, JsonElement> refs, out JsonElement value)
    {
        value = default;
        var dot = token.IndexOf('.');
        var refName = dot < 0 ? token : token[..dot];
        if (!refs.TryGetValue(refName, out var root)) return false;

        value = root;
        if (dot < 0) return true;
        foreach (var segment in token[(dot + 1)..].Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out var child))
                return false;
            value = child;
        }
        return true;
    }
}

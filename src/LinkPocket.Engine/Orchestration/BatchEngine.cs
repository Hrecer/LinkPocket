using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Engine;

/// <summary>
/// 批引擎（IBatchEngine / 批流 / 并发事务语义）。
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
        new("batch.run", "batch", "Execute a batch of commands in script order (a transactional batch shares one unit of work and rolls the whole batch back on abort; a standalone batch commits each step)",
            [ParamSpec.Req<JsonElement>("script", "Batch script { name, steps: [{ ref, command, args, on_error }], scope }; step args reference earlier results: {ref}, {ref.path}, {ref.path[n]} (array index), {ref.path[*].field} (map over array), {ref.path.length}", schema: ParamSchemas.BatchScript)],
            CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.SupportsCancellation),
        new("batch.dry_run", "batch", "Dry-run a batch script: every step executes without commit, returning per-step results and impact with zero side effects",
            [ParamSpec.Req<JsonElement>("script", "Batch script", schema: ParamSchemas.BatchScript)],
            CommandCaps.Query | CommandCaps.LongRunning | CommandCaps.SupportsCancellation),
        new("batch.status", "batch", "Query batch run status: pass batch_id (issued by batch.run reports / error details), or omit it to read the in-flight batch (null when idle)",
            [ParamSpec.Opt<string>("batch_id", "Batch ID; omit to read the current in-flight batch")],
            CommandCaps.Query),
    ];

    /// <summary>批状态字典容量上限：宿主长跑（AI 轮询/宏）会持续新增 batch_id，
    /// 无上限会无限膨胀（每个 BatchStatus 还挂着步骤结果）。超限清理已完成/中止的旧条目。</summary>
    private const int MaxStatusEntries = 256;

    private readonly EngineCore _engine;
    private readonly EngineLimits _limits;
    private readonly ConcurrentDictionary<string, BatchStatus> _status = new(StringComparer.Ordinal);

    public BatchEngine(EngineCore engine, EngineLimits? limits = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _limits = limits ?? EngineLimits.Default;
    }

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

    /// <summary>取当前已完成的步数；状态缺失时退化为 0（拒绝 KeyNotFoundException 覆盖原始异常）。</summary>
    private int CompletedStepsOf(string batchId)
        => _status.TryGetValue(batchId, out var status) ? status.CompletedSteps : 0;

    public Task<BatchReport> RunAsync(BatchScript script, CallOptions? options = null, CancellationToken ct = default)
        => RunCoreAsync(script, dryRun: options?.DryRun == true, options, ct);

    public Task<BatchReport> DryRunAsync(BatchScript script, CancellationToken ct = default)
        => RunCoreAsync(script, dryRun: true, options: null, ct);

    public BatchStatus? GetStatus(string batchId)
        => _status.TryGetValue(batchId, out var status) ? status : null;

    /// <summary>当前在飞的批（状态表里唯一 state = running 的一条；写闸保证同一时刻至多一个批在跑，
    /// 批结束即转终态 → 本读数**自清零**，不存在"忘了清标记"的泄漏面）。</summary>
    public BatchStatus? CurrentStatus
        => _status.Values.FirstOrDefault(status => status.State == "running");

    // ===== 核心执行 =====

    private async Task<BatchReport> RunCoreAsync(BatchScript script, bool dryRun, CallOptions? options, CancellationToken ct)
    {
        ValidateScript(script, _limits);
        var batchId = Guid.NewGuid().ToString("N");
        var correlationId = options?.CorrelationId ?? $"batch:{batchId}";
        var caller = options?.Caller ?? new CallerRef(CallerKind.Batch, batchId);
        var sw = Stopwatch.StartNew();

        // 能力门：与单命令同一条 —— 只读会话拒批；写入冻结时只有持锁会话能批（否则界面/宿主可借批绕过冻结）
        _engine.Sessions?.Enforce(caller, isMutation: true, correlationId);

        TrackStatus(batchId, new BatchStatus(batchId, script.Name, "running", 0, script.Steps.Count));

        // 里程碑（Debug）：批开始——批是"一条用户动作"的容器，与各步嵌套审计同 correlation
        if (LpLog.IsEnabled(LogLevel.Debug))
            LpLog.Write(LogLevel.Debug, "engine.batch", $"Batch start: {script.Name}", props: new Dictionary<string, object?>
            {
                ["batch"] = batchId,
                ["steps"] = script.Steps.Count,
                ["scope"] = script.Scope.ToString(),
                ["dry_run"] = dryRun,
            });

        List<BatchStepResult> results;
        var touched = new List<EntityRef>();
        var events = new List<string>();
        var diff = new List<FieldChange>();
        try
        {
            if (script.Scope == BatchScope.Transactional)
            {
                (results, touched, events, diff) = await RunTransactionalAsync(script, dryRun, correlationId, caller,
                    options?.UndoGroupId ?? batchId, batchId, ct);
            }
            else
            {
                (results, touched, events, diff) = await RunIndependentAsync(script, dryRun, correlationId, caller,
                    batchId, options?.UndoGroupId ?? batchId, ct);
            }
        }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.BatchAborted)
        {
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", CompletedStepsOf(batchId), script.Steps.Count));
            throw;   // 嵌套步骤循环已带报告详情（batch_id + step）
        }
        catch (EngineException ex) when (ex.Error.Code is EngineErrors.TypeMismatch or EngineErrors.RequiredParam or EngineErrors.ProtocolMalformed)
        {
            // 校验类错误（含模板坏引用）零副作用，按原错误码透传，不包装成批失败
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", CompletedStepsOf(batchId), script.Steps.Count));
            throw;
        }
        catch (EngineException ex)
        {
            // 事务批中途异常：工作单元未提交已回滚
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", CompletedStepsOf(batchId), script.Steps.Count));
            LpLog.Warn($"Batch aborted: {script.Name} ({ex.Error.Code})", ex, category: "engine.batch");
            throw new EngineException(EngineErrors.Of(
                EngineErrors.BatchAborted,
                $"Batch '{script.Name}' failed ({ex.Error.Code}): the transactional batch rolled back entirely",
                details: JsonSerializer.SerializeToElement(new { batch_id = batchId, error = ex.Error }),
                correlationId: correlationId));
        }
        catch (OperationCanceledException)
        {
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "aborted", CompletedStepsOf(batchId), script.Steps.Count));
            throw;
        }

        var report = BuildReport(batchId, script.Name, results, touched, events, diff, sw.ElapsedMilliseconds, correlationId);
        TrackStatus(batchId, new BatchStatus(batchId, script.Name, report.Ok ? "completed" : "failed",
            results.Count, script.Steps.Count));

        // 父级审计条目（batch_id 列关联；每步已有 IsNested 子记录）。
        // 入参快照 = 批脚本本身（与单命令同口径：先脱敏后截断；G4——否则 AI/排障无法自证"发过哪些参数"）。
        // 观测面纪律：父审计失败**不否定已完成的事实**（报告照常返回，失败计数 + 记日志）。
        var scriptArgs = EngineCore.SnapshotArgs(JsonSerializer.SerializeToElement(
            new { script }, EngineJson.ScriptOptions));
        try
        {
            _engine.Audit.Write(new AuditEntry(
                DateTimeOffset.Now, "batch.run", correlationId, caller, sw.ElapsedMilliseconds,
                Success: report.Ok, ErrorCode: report.Ok ? null : EngineErrors.BatchAborted,
                Changes: report.Changes, DryRun: dryRun, IsNested: false, StackTrace: null,
                ArgsJson: scriptArgs.Json, BatchId: batchId, ArgsTruncated: scriptArgs.Truncated));
        }
        catch (Exception auditEx)
        {
            _engine.RegisterObservationFailure("batch parent audit write failed", auditEx);
        }

        if (LpLog.IsEnabled(LogLevel.Debug))
            LpLog.Write(LogLevel.Debug, "engine.batch", $"Batch end: {script.Name}", props: new Dictionary<string, object?>
            {
                ["batch"] = batchId,
                ["ok"] = report.Ok,
                ["steps"] = results.Count,
            }, elapsedMs: sw.ElapsedMilliseconds);

        return report;
    }

    /// <summary>事务批：写闸 + 单 UoW + 嵌套派发（abort/异常 = 不提交即回滚）。</summary>
    private async Task<(List<BatchStepResult> Results, List<EntityRef> Touched, List<string> Events, List<FieldChange> Diff)> RunTransactionalAsync(
        BatchScript script, bool dryRun, string correlationId, CallerRef caller, string undoGroupId,
        string batchId, CancellationToken ct)
    {
        await _engine.WriteGate.WaitAsync(ct);
        try
        {
            await using var uow = _engine.UowFactory();
            // **总是**开显式事务：步骤之间的 flush（见 RunStepsNestedAsync）只写进事务、不提交，
            // 整批的原子性由这里收口——成功提交；异常与干跑回滚。
            // 干跑必须**立即** BeginAsync：绕过变更跟踪的批量语句（ExecuteDelete / ExecuteUpdate 等）
            // 只在本连接已有事务时才可回滚，否则"执行但不提交"会被绕开（改动直接落库）。
            var tx = uow.BeginTransaction();
            // 持写事务期间的嵌套审计先缓冲（SqlAuditWriter 是独立短连接，撞写锁会 busy 超时/步；
            // 见 CommandContextImpl.NestedAuditBuffer）——收口后统一写出，成功与回滚路径都留痕。
            var auditBuffer = new List<AuditEntry>();
            CommandContextImpl ctx;
            List<BatchStepResult> results;
            List<EntityRef> touched;
            List<string> events;
            List<FieldChange> diff;
            try
            {
                await tx.BeginAsync(ct);
                // BatchId：嵌套步骤审计的关联键（audit.query {batch_id} 取齐每一步）
                ctx = new CommandContextImpl(uow, isNested: false, dryRun, correlationId, caller, ct, _engine,
                    undoGroupId, batchId) { NestedAuditBuffer = auditBuffer };
                (results, touched, events, diff) = await RunStepsNestedAsync(ctx, script, undoGroupId, ct);

                if (dryRun) await tx.RollbackAsync(ct);
                else await tx.CommitAsync(ct);
            }
            finally
            {
                await tx.DisposeAsync();
                _engine.FlushNestedAudit(auditBuffer);
            }

            if (!dryRun)
            {
                // 事件发布：嵌套事件已随执行入父缓冲，提交成功后统一发布一次
                // （不变量：订阅方不得同步回派命令；发布路径统一走 EngineCore.PublishAsync → 缓存世代戳同步推进）
                var merged = ctx.TakeNestedChanges();
                if (merged.Events.Count > 0)
                {
                    var payload = ChangeSetPayload.From(merged);
                    foreach (var name in merged.Events)
                        await _engine.PublishAsync(new DomainEvent(name, DateTimeOffset.Now, payload, correlationId, caller));
                }

                // 撤销登记（E5）：提交成功后统一入栈（一次批 = 一条记录 N 逆向步，归属键 = 批 ID / 调用方归属键）；
                // 干跑与回滚路径的暂存自然作废，绝不入栈
                _engine.FlushPendingUndo(ctx, caller);
            }
            return (results, touched, events, diff);
        }
        finally
        {
            _engine.WriteGate.Release();
        }
    }

    /// <summary>独立批：每步走完整顶层管道（各自隐式事务；abort 只停后续步骤，已执行步骤保持生效）。
    /// 中止（OnError=Abort 失败）通过抛 <see cref="EngineErrors.BatchAborted"/> 表达，由调用方 catch 转状态——
    /// 本方法只返回逐步骤结果，不再有「aborted 标志」这一恒 false 的死字段。
    /// 每步的变更同样聚合（touched/events/diff）供批报告使用——独立批的步骤各走顶层管道，
    /// 不经父缓冲，聚合必须在本层做。</summary>
    private async Task<(List<BatchStepResult> Results, List<EntityRef> Touched, List<string> Events, List<FieldChange> Diff)> RunIndependentAsync(
        BatchScript script, bool dryRun, string correlationId, CallerRef caller, string batchId, string undoGroupId, CancellationToken ct)
    {
        var results = new List<BatchStepResult>();
        var touched = new List<EntityRef>();
        var events = new List<string>();
        var diff = new List<FieldChange>();
        var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in script.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var stepSw = Stopwatch.StartNew();
            var args = BatchTemplate.Resolve(step.Args, refs);
            try
            {
                // UndoGroupId = 批归属键：独立批逐步提交，撤销栈按同组合并为一条记录（一次批 = 一次用户动作）；
                // BatchId = 批运行键：步骤走完整顶层管道，审计 batch_id 列经选项携带（与事务批嵌套行同口径）
                var r = await _engine.ExecuteAsync<object>(step.Command, args, new CallOptions(
                    DryRun: dryRun, CorrelationId: $"{correlationId}:{step.Ref}", Caller: caller,
                    UndoGroupId: undoGroupId, BatchId: batchId), ct);
                TrackStepData(refs, step.Ref, r.Data);
                if (r.Changes is { } changes)
                {
                    touched.AddRange(changes.Touched);
                    events.AddRange(changes.Events);
                    if (changes.Diff is { Count: > 0 } stepDiff) diff.AddRange(stepDiff);
                }
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: true, Skipped: false,
                    ToElement(r.Data), null, null, stepSw.ElapsedMilliseconds));
            }
            catch (EngineException ex)
            {
                if (step.OnError == ErrorPolicy.Abort)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.BatchAborted,
                        $"Batch '{script.Name}' step '{step.Ref}' ({step.Command}) failed: {ex.Error.Message}" +
                        "(standalone batch: steps executed before the failure stay in effect)",
                        details: JsonSerializer.SerializeToElement(new { batch_id = batchId, step = step.Ref }),
                        correlationId: correlationId));
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: false,
                    Skipped: step.OnError == ErrorPolicy.SkipAndLog, null, ex.Error.Code, ex.Error.Message,
                    stepSw.ElapsedMilliseconds));
            }
            TrackStatus(batchId, new BatchStatus(batchId, script.Name, "running", results.Count, script.Steps.Count));
        }
        return (results, touched, events, diff);
    }

    /// <summary>
    /// 嵌套步骤循环（事务批与宏运行共用）：在既有管道上下文内逐步嵌套派发。
    /// 策略 Abort 的步骤失败直接抛出（由调用方的管道回滚）；Continue/SkipAndLog 记录后继续。
    /// 返回步骤结果 + 聚合变更集（Touched/Events/Diff，供批报告与事件负载）。
    /// 撤销（E5）：每个成功步骤的逆向信息暂存进 <paramref name="ctx"/>（归属键 = <paramref name="undoGroupId"/>），
    /// 由提交成功后的登记点统一入栈——批/宏共用本处，干跑不暂存。
    /// </summary>
    internal static async Task<(List<BatchStepResult> Results, List<EntityRef> Touched, List<string> Events, List<FieldChange> Diff)> RunStepsNestedAsync(
        CommandContextImpl ctx, BatchScript script, string undoGroupId, CancellationToken ct)
    {
        var results = new List<BatchStepResult>();
        var touched = new List<EntityRef>();
        var events = new List<string>();
        var diff = new List<FieldChange>();
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
                    if (changes.Diff is { Count: > 0 } stepDiff) diff.AddRange(stepDiff);
                }

                // 撤销暂存（E5）：dry_run 零副作用 → 不暂存；无可逆信息的步骤由登记点自然跳过
                if (!ctx.DryRun && ctx.Engine.Registry.Resolve(step.Command) is { } undoHandler)
                    ctx.AddPendingUndo(undoHandler.Descriptor, args, r.Undo, undoGroupId);

                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: true, Skipped: false,
                    ToElement(r.Data), null, null, stepSw.ElapsedMilliseconds));

                // **步骤间 flush**：把本步改动写进当前事务（不提交），让**后续步骤**读得到。
                // 没有它："先移动、再删除"这类脚本里，删除步骤按**数据库现状**做筛选
                //（folders.delete 的 cascade 读 links 表）⇒ 看到移动前的旧状态：实测事故（2026-09-26）
                // move_batch 报 moved 1630、紧接着 cascade 把这 1630 条整批扫进回收站（根目录空空，
                // 数据躺在回收站里）。干跑同样 flush——干跑必须与真跑同语义，最后统一回滚；
                // 两侧的原子性都有外层显式事务兜底（事务批见 RunTransactionalAsync、宏见 MacroRunHandler）。
                // **步骤间 flush**：把本步改动写进当前事务（不提交），让**后续步骤**读得到。
                // 没有它："先移动、再删除"这类脚本里，删除步骤按**数据库现状**做筛选
                //（folders.delete 的 cascade 读 links 表）⇒ 看到移动前的旧状态：实测事故（2026-09-26）
                // move_batch 报 moved 1630、紧接着 cascade 把这 1630 条整批扫进回收站（根目录空空，
                // 数据躺在回收站里）。干跑同样 flush——干跑必须与真跑同语义，最后统一回滚；
                // 两侧的原子性都有外层显式事务兜底（事务批见 RunTransactionalAsync、宏见 MacroRunHandler）。
                await ctx.Uow.CommitAsync(ct);
            }
            catch (EngineException ex)
            {
                if (step.OnError == ErrorPolicy.Abort)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.BatchAborted,
                        $"Batch '{script.Name}' step '{step.Ref}' ({step.Command}) failed: {ex.Error.Message}",
                        details: JsonSerializer.SerializeToElement(new { batch_id = ctx.CorrelationId, step = step.Ref }),
                        correlationId: ctx.CorrelationId));
                results.Add(new BatchStepResult(step.Ref, step.Command, Ok: false,
                    Skipped: step.OnError == ErrorPolicy.SkipAndLog, null, ex.Error.Code, ex.Error.Message,
                    stepSw.ElapsedMilliseconds));
            }
        }
        return (results, touched, events, diff);
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

    internal static void ValidateScript(BatchScript script, EngineLimits limits)
    {
        if (script.Steps.Count == 0)
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "a batch script needs at least one step", details: JsonSerializer.SerializeToElement(new { @param = "steps" })));
        if (script.Steps.Count > limits.MaxBatchSteps)
            throw new EngineException(EngineErrors.Of(EngineErrors.EnumOutOfRange,
                $"a batch script has {script.Steps.Count} steps (limit {limits.MaxBatchSteps})",
                details: JsonSerializer.SerializeToElement(new { @param = "steps", limit = limits.MaxBatchSteps })));
        if (script.Steps.Any(s => string.IsNullOrWhiteSpace(s.Command)))
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "every batch step must specify command", details: JsonSerializer.SerializeToElement(new { @param = "steps[].command" })));
        var dup = script.Steps.Select(s => s.Ref).Where(r => !string.IsNullOrWhiteSpace(r))
            .GroupBy(r => r, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
            throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                $"duplicate batch step reference name '{dup.Key}'", details: JsonSerializer.SerializeToElement(new { @param = "steps[].ref" })));
    }

    private static BatchReport BuildReport(string batchId, string name, List<BatchStepResult> results,
        List<EntityRef> touched, List<string> events, List<FieldChange> diff, long elapsedMs, string correlationId)
    {
        var ok = results.All(r => r.Ok);
        var summary = ok
            ? $"Batch '{name}' finished: all {results.Count} steps succeeded"
            : $"Batch '{name}' finished with failures: {results.Count(r => r.Ok)}/{results.Count} steps succeeded";

        return new BatchReport(batchId, name, ok, results,
            new ChangeSet(touched, events, summary, Warnings: null, Diff: diff.Count > 0 ? diff : null), summary, elapsedMs, correlationId);
    }
}

/// <summary>
/// 批步骤模板解析：<c>{ref}</c> / <c>{ref.path.sub}</c> 引用更早步骤的结果数据。
/// 路径段支持三种谓词（G8 收口）：
/// <c>[n]</c> 数组下标（越界 / 非数组 <b>如实报错</b>，绝不静默留空）；
/// <c>[*]</c> 展开 / 逐元映射（<c>{ref.items[*].id}</c> → 同形数组，可直接喂 <c>link_ids</c> 这类数组参数；
/// 出现在数组字面量里时整体摊平拼接）；
/// <c>length</c> 数组 / 字符串长度（对象上有同名属性时<b>属性优先</b>，零歧义）。
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
        {
            // 数组字面量里的整串 [*] 引用 = 展开摊平：["pre", "{q.items[*].id}"] → ["pre", "A", "B", ...]
            if (item.ValueKind == JsonValueKind.String && IsSpreadToken(item.GetString()!, out var spreadToken))
            {
                if (TryLookup(spreadToken, refs, out var expanded) && expanded.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in expanded.EnumerateArray())
                        target.Add(System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText()));
                    continue;
                }
            }
            target.Add(System.Text.Json.Nodes.JsonNode.Parse(Resolve(item, refs).GetRawText()));
        }
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonElement ResolveString(string text, IReadOnlyDictionary<string, JsonElement> refs)
    {
        // 整串精确引用：{ref} / {ref.a.b} / {ref.a[0]} / {ref.a[*].id} / {ref.a.length} → 替换为结果元素本体
        if (text.StartsWith('{') && text.EndsWith('}') && IsRefToken(text.AsSpan(1, text.Length - 2), out var token))
        {
            if (TryLookup(token, refs, out var value)) return value.Clone();
            throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                $"template reference '{text}' has no matching step result (a reference must come after the step it refers to)"));
        }
        return JsonSerializer.SerializeToElement(ReplaceInline(text, refs));
    }

    private static string ReplaceInline(string text, IReadOnlyDictionary<string, JsonElement> refs)
        => System.Text.RegularExpressions.Regex.Replace(text, TokenPattern, match =>
        {
            var token = match.Groups["token"].Value;
            if (!TryLookup(token, refs, out var value)) return match.Value;   // 非引用令牌原样保留
            return value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? value.GetRawText()
                : value.ToString();
        });

    /// <summary>引用令牌形态：<c>段(段)*</c>，每段 = 标识符 + 可选 <c>[n]</c> / <c>[*]</c>。</summary>
    private const string TokenPattern = @"\{(?<token>[A-Za-z0-9_]+(?:\[\*\]|\[\d+\])?(?:\.(?:[A-Za-z0-9_]+(?:\[\*\]|\[\d+\])?))*)\}";

    private static bool IsRefToken(ReadOnlySpan<char> span, out string token)
    {
        token = span.ToString();
        if (token.Length == 0) return false;
        foreach (var part in token.Split('.'))
        {
            if (!TryParseSegment(part, out _)) return false;
        }
        return true;
    }

    private static bool IsSpreadToken(string text, out string token)
    {
        token = "";
        if (!(text.StartsWith('{') && text.EndsWith('}'))) return false;
        if (!IsRefToken(text.AsSpan(1, text.Length - 2), out token)) return false;
        return token.Contains("[*]", StringComparison.Ordinal);
    }

    /// <summary>路径段 = 标识符 + 可选下标谓词（<c>[n]</c> 下标 / <c>[*]</c> 展开）。</summary>
    private readonly record struct Segment(string Name, int? Index, bool Star);

    private static bool TryParseSegment(string part, out Segment segment)
    {
        segment = default;
        if (part.Length == 0) return false;
        var bracket = part.IndexOf('[');
        var name = bracket < 0 ? part : part[..bracket];
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        if (bracket < 0)
        {
            segment = new Segment(name, null, false);
            return true;
        }

        if (!part.EndsWith(']')) return false;
        var inner = part[(bracket + 1)..^1];
        if (inner == "*")
        {
            segment = new Segment(name, null, true);
            return true;
        }
        if (!int.TryParse(inner, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 0)
            return false;
        segment = new Segment(name, index, false);
        return true;
    }

    /// <summary>
    /// 引用求值：未知步骤 / 缺属性 = 未命中（整串报错、内嵌保留字面量，维持既有口径）；
    /// <b>结构性错误（下标越界、非数组展开、双展开）一律如实抛 <see cref="EngineErrors.TypeMismatch"/></b>——
    /// 这是脚本作者的错误，绝不静默留空。
    /// </summary>
    private static bool TryLookup(string token, IReadOnlyDictionary<string, JsonElement> refs, out JsonElement value)
    {
        value = default;
        var parts = token.Split('.');
        if (!TryParseSegment(parts[0], out var head)) return false;
        if (!refs.TryGetValue(head.Name, out var current)) return false;

        for (var i = 0; i < parts.Length; i++)
        {
            var seg = head;
            if (i > 0)
            {
                if (!TryParseSegment(parts[i], out seg)) return false;
                if (!Step(ref current, seg.Name)) return false;
            }

            if (seg.Star)
            {
                // 展开 / 逐元映射：剩余路径逐元素求值，产出同形数组
                if (current.ValueKind != JsonValueKind.Array)
                    throw Mismatch($"template reference '{{{token}}}' cannot expand: value at '{seg.Name}' is not an array");
                var mapped = new List<JsonElement>();
                var at = 0;
                foreach (var element in current.EnumerateArray())
                {
                    var item = element;
                    for (var j = i + 1; j < parts.Length; j++)
                    {
                        if (!TryParseSegment(parts[j], out var rest)) return false;
                        if (rest.Star)
                            throw Mismatch($"template reference '{{{token}}}' has more than one [*] expansion");
                        if (!Step(ref item, rest.Name))
                            throw Mismatch($"template reference '{{{token}}}' element [{at}] has no '{rest.Name}'");
                        ApplyIndex(ref item, rest.Index, token);
                    }
                    mapped.Add(item.Clone());
                    at++;
                }
                value = JsonSerializer.SerializeToElement(mapped);
                return true;
            }

            ApplyIndex(ref current, seg.Index, token);
        }

        value = current;
        return true;
    }

    /// <summary>路径段求值：对象属性优先；数组 / 字符串上的 <c>length</c> 取长度（对象同名属性优先）。</summary>
    private static bool Step(ref JsonElement current, string name)
    {
        if (name == "length" && current.ValueKind != JsonValueKind.Object)
        {
            var length = current.ValueKind switch
            {
                JsonValueKind.Array => current.GetArrayLength(),
                JsonValueKind.String => (current.GetString() ?? string.Empty).Length,
                _ => -1,
            };
            if (length < 0) return false;
            current = JsonSerializer.SerializeToElement(length);
            return true;
        }

        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var child)) return false;
        current = child;
        return true;
    }

    private static void ApplyIndex(ref JsonElement current, int? index, string token)
    {
        if (index is not { } at) return;
        if (current.ValueKind != JsonValueKind.Array)
            throw Mismatch($"template reference '{{{token}}}' cannot index with [{at}]: value is not an array");
        if (at >= current.GetArrayLength())
            throw Mismatch($"template reference '{{{token}}}' index [{at}] is out of range (length {current.GetArrayLength()})");
        current = current[at];
    }

    private static EngineException Mismatch(string message)
        => new(EngineErrors.Of(EngineErrors.TypeMismatch, message));
}

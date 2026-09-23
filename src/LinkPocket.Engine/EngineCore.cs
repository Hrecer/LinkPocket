using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 引擎核心（读流 / wire / 命令注册）。
/// 写流：解析 → 幂等查重 → 能力门（破坏性确认）→ 审计开始 → 写闸 → UoW → Handler → 提交 → 事件发布 → 审计完成。
/// 读流：解析 → 读池短 UoW → Handler → 结果（免写闸/免审计/免撤销）。
/// 嵌套：复用父 UoW 与写闸（同链串行），事件随父提交一并发布，审计合并为父条目子记录。
/// 干跑：正常执行但事务回滚丢弃、零事件、零幂等记录，审计标记 DryRun。
/// 失败：统一抛 <see cref="EngineException"/>（必带 EngineError；内部错误带堆栈进审计）。
/// </summary>
public sealed class EngineCore : IEngine
{
    private readonly CommandRegistry _registry;
    private readonly Func<IUnitOfWork> _uowFactory;
    private readonly SemaphoreSlim _writeGate;
    private readonly IdempotencyStore _idempotency;
    private readonly ConfirmTokenStore _confirmTokens;
    private readonly IAuditWriter _audit;
    private readonly IEventBus _events;
    private readonly IEventStore _eventStore;
    private readonly ISessionManager? _sessions;
    private readonly QueryCache _cache;
    private long _observationFailures;   // 观测面失败计数（审计/事件发布）：已提交成功被观测失败时 +1，不否定业务结果

    public EngineCore(
        CommandRegistry registry,
        Func<IUnitOfWork> uowFactory,
        IAuditWriter? audit = null,
        IEventBus? eventBus = null,
        ConfirmTokenStore? confirmTokens = null,
        IdempotencyStore? idempotency = null,
        IEventStore? eventStore = null,
        ISessionManager? sessions = null,
        QueryCache? cache = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _audit = audit ?? new InMemoryAuditWriter();
        _events = eventBus ?? new InMemoryEventBus();
        _confirmTokens = confirmTokens ?? new ConfirmTokenStore();
        _idempotency = idempotency ?? new IdempotencyStore();
        _eventStore = eventStore ?? new InMemoryEventStore();
        _sessions = sessions;
        _cache = cache ?? new QueryCache();    // 查询缓存缺省装配（策略由 Descriptor 声明，未声明即不走缓存）
        _writeGate = new SemaphoreSlim(1, 1);   // 全局单写闸：任意两写不重叠（现状数据闸语义保留）

        // 事件存储 = 总线的常驻订阅者：发布即写入（先于消费方订阅者登记，顺序稳定）
        _events.Subscribe(_eventStore.Append);
    }

    /// <summary>事件总线（宿主可订阅做 UI 防抖刷新等；订阅方纪律 = 不得同步回派命令）。</summary>
    public IEventBus Events => _events;

    /// <summary>事件存储（L3）：发布即写入的环形缓冲，追平/轮询入口。</summary>
    public IEventStore EventStore => _eventStore;

    /// <summary>批引擎（编排层；OrchestrationHost 装配后非空）。
    /// setter internal：装配由编排层宿主独占（组合根不可在装配后改写）。</summary>
    public IBatchEngine? Batch { get; internal set; }

    /// <summary>撤销协调器（编排层；OrchestrationHost 装配后非空）。
    /// setter internal：同上。</summary>
    public IUndoCoordinator? Undo { get; internal set; }

    /// <summary>查询结果缓存（性能加固）：声明了 <see cref="CommandDescriptor.Cache"/> 的查询才参与。</summary>
    public QueryCache Cache => _cache;

    /// <summary>运行时统计快照（诊断面；宿主/Host 接线进 diagnostics.collect）。</summary>
    public EngineRuntimeStats RuntimeStats
    {
        get
        {
            var c = _cache.Counters;
            return new EngineRuntimeStats(
                c.Entries, c.Hits, c.Misses, c.Evictions, c.Invalidations,
                _eventStore.Head.Sequence, Interlocked.Read(ref _observationFailures));
        }
    }

    // ===== 同程序集编排组件的内部访问器（BatchEngine/MacroRun 等复用写闸/UoW 工厂/审计/注册表）=====

    internal SemaphoreSlim WriteGate => _writeGate;
    internal Func<IUnitOfWork> UowFactory => _uowFactory;
    internal IAuditWriter Audit => _audit;
    internal CommandRegistry Registry => _registry;
    /// <summary>会话能力门（同程序集编排组件复用：批引擎的写入冻结 / 只读会话检查走同一条）。</summary>
    internal ISessionManager? Sessions => _sessions;

    public async Task<CommandResult<T>> ExecuteAsync<T>(string command, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
    {
        // 批三命令直路由（唯一实现 BatchDispatch；wire 的直接方法名同口径）：批引擎自身即管道父调用，
        // 不走标准命令管道——进程内消费者（EngineClient / AI）与 wire 消费者必须同口径，禁止双实现。
        if (BatchDispatch.IsBatchCommand(command))
            return await DispatchBatchAsync<T>(command, args, options, ct).ConfigureAwait(false);

        var correlationId = options?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var caller = CallOptions.CallerOf(options);
        var dryRun = options?.DryRun == true;
        var argsJson = EngineJson.ToJsonElement(args);   // 入参快照：审计 ArgsJson 与撤销登记共用
        var argsSnapshot = SnapshotArgs(argsJson);       // 审计副本（超长截断 + 如实标记）

        // 能力门：会话存在性 + 只读拒绝写 + 限流（未登记会话零约束，兼容宿主自有调用）
        _sessions?.Enforce(caller, isMutation: true, correlationId);

        var handler = ResolveOrThrow(command, correlationId);
        if (!handler.Descriptor.IsMutation)
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
                $"'{command}' is not a mutation command, use QueryAsync", correlationId: correlationId));

        // 调用上下文：corr / cmd / caller 落到记录的**首类字段**——本命令链上的每条记录（里程碑 / 处理器 /
        // 观测面）一律自动携带，与审计同 correlation。"一条用户动作的完整链路"由此可对齐，不靠各处手抄。
        using var call = LpLog.BeginCall(correlationId, command, caller.ToString());

        // 里程碑（Debug：缺省 info 级下零噪音，提级即为完整管道轨迹）
        if (LpLog.IsEnabled(LogLevel.Debug))
            LpLog.Write(LogLevel.Debug, "engine.pipeline", $"Command start: {command}", props: new Dictionary<string, object?>
            {
                ["dry_run"] = dryRun,
            });

        // 幂等查重（在写闸之前；24h 窗口内命中即返回首次结果，不重复执行）。
        // 干跑跳过查重：干跑语义 = 「无论如何执行一遍（执行但不提交）」，命中历史缓存会把它变成 no-op。
        if (!dryRun && options?.IdempotencyKey is { } key && _idempotency.TryGet(key, out var cached))
            return ToTyped<T>(cached);

        // 能力门：破坏性命令两阶段确认（干跑不消耗确认）
        if (handler.Descriptor.IsDestructive && !dryRun)
            EnsureConfirmed(handler.Descriptor, options, correlationId);

        var sw = Stopwatch.StartNew();
        bool gateOwned = false;
        CommandContextImpl? ctx = null;   // 提升到 catch 可见：失败审计要带 BatchId（宏处理器运行期间会登记）
        try
        {
            await _writeGate.WaitAsync(ct);
            gateOwned = true;

            // 幂等二次确认（写闸内）：闸外首次查重只是快路径——两并发携带同一 IdempotencyKey
            // 可能都在提交前排过（都 miss），闸内复核保证后到者直接命中首次结果，绝不重复执行；
            // 干跑同样跳过（与闸外查重一致，见上方注释）。
            if (!dryRun && options?.IdempotencyKey is { } recheckKey && _idempotency.TryGet(recheckKey, out var recheckCached))
                return ToTyped<T>(recheckCached);

            await using var uow = _uowFactory();
            // 干跑立即开启显式事务：绕过变更跟踪的批量语句（ExecuteDelete 等）只在本连接已有事务时才可回滚，
            // 否则"执行但不提交"会被绕开（改动直接落库）。
            ITransactionScope? tx = dryRun ? uow.BeginTransaction() : null;
            if (tx != null) await tx.BeginAsync(ct);
            CommandResult result;
            try
            {
                ctx = new CommandContextImpl(uow, isNested: false, dryRun, correlationId, caller, ct, this,
                    undoGroupId: options?.UndoGroupId, batchId: options?.BatchId);
                result = await handler.ExecuteAsync(ctx, argsJson);

                if (dryRun)
                {
                    if (tx != null) await tx.RollbackAsync(ct);   // 干跑：执行但不提交
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
                // 事件发布：持闸期间同步推送（不变量：订阅方不得同步回派命令）
                // 事件携带 ChangeSet 负载（订阅方可做增量处理）+ 按事件名精确失效查询缓存
                // 观测面纪律：订阅方异常已由 IEventBus 逐方隔离，此处再兜一层——
                // 任何事件发布失败都绝不允许把「已提交的成功写」报成失败（违规即 LP.SYS.003）。
                try
                {
                    await PublishChangesAsync(result.Changes, ctx, correlationId, caller);
                }
                catch (Exception pubEx)
                {
                    RegisterObservationFailure("event publish failed", pubEx);
                }

                // 整库影响面的命令（maintenance.reinit）：表已清空，全部查询缓存直接作废；
                // **撤销栈/重做栈同样作废**（Maintenance：旧条目的目标 ID 已不存在，
                // 留着只会让 undo.undo 报 EntityNotFound 且永远可重放失败）——同步清内存栈
                //（写闸内串行、ClearAsync 无内等待，GetAwaiter 安全）。
                if (handler.Descriptor.Impact == ImpactSummary.Database)
                {
                    _cache.Clear();
                    Undo?.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
                    // 幂等记录一并失效：表已清空，"上次那条成功"不再成立 ——
                    // 留着它会让同 key 的重放在 24h 内直接返回旧成功结果、**库里什么都没发生**却没提示。
                    try
                    {
                        _idempotency.Clear();
                    }
                    catch (Exception idemEx)
                    {
                        // 清理失败只计数 + 记日志，绝不否定"表已清空"这个已提交事实
                        RegisterObservationFailure("failed to clear idempotency records after full database reset", idemEx);
                    }
                }

                if (options?.IdempotencyKey is { } idemKey)
                    _idempotency.Store(idemKey, result);

                // 撤销登记：顶层可撤销命令成功后入栈。
                // 逆向步骤优先取**处理器回填**（能带旧值，重命名/移动靠它）；没有则退回描述符 + 原参数。
                // UndoGroupId：同一次用户动作拆成的多次调用（一次粘贴多选）合并为一条记录。
                // 批/宏步骤（E5）先消费暂存登记（同归属键合并为「一次批 = 一条记录 N 逆向步」），再登记顶层自身。
                FlushPendingUndo(ctx, caller);
                Undo?.Record(handler.Descriptor, argsJson, caller, result.Undo, options?.UndoGroupId);
            }

            // 成功审计（观测面）：同样不因审计失败而否定已提交的事实（ARCHITECTURE 不变量 #10「失败要暴露」的
            // 约束下，只记录并暴露 ObservationFailures，绝不抛异常、也不再补一条矛盾的「失败审计」）。
            string? auditRef;
            try
            {
                auditRef = _audit.Write(new AuditEntry(
                    DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                    Success: true, ErrorCode: null, result.Changes, DryRun: dryRun, IsNested: false, StackTrace: null,
                    ArgsJson: argsSnapshot.Json, BatchId: ctx?.BatchId, ArgsTruncated: argsSnapshot.Truncated));
            }
            catch (Exception auditEx)
            {
                RegisterObservationFailure("success audit write failed", auditEx);
                auditRef = null;
            }

            if (LpLog.IsEnabled(LogLevel.Debug))
                LpLog.Write(LogLevel.Debug, "engine.pipeline", $"Command completed: {command}", props: new Dictionary<string, object?>
                {
                    ["dry_run"] = dryRun,
                    ["touched"] = result.Changes?.Touched.Count ?? 0,
                    ["events"] = result.Changes?.Events.Count ?? 0,
                }, elapsedMs: sw.ElapsedMilliseconds);

            return new CommandResult<T>(true, (T?)result.Data, result.Changes, auditRef);
        }
        catch (EngineException ex)
        {
            WriteFailureAudit(command, correlationId, caller, sw, dryRun, ex.Error.Code, ex.StackTrace?.ToString(), argsSnapshot, ctx?.BatchId);
            LpLog.Warn($"Command failed: {command} ({ex.Error.Code})", ex, category: "engine.pipeline");
            throw;
        }
        catch (OperationCanceledException)
        {
            // 取消也落审计（观测面：所有调用可追溯，取消不例外）
            WriteFailureAudit(command, correlationId, caller, sw, dryRun, EngineErrors.Cancelled, null, argsSnapshot, ctx?.BatchId);
            LpLog.Warn($"Command cancelled: {command}", category: "engine.pipeline");
            throw new EngineException(EngineErrors.Of(EngineErrors.Cancelled, "Call cancelled", correlationId: correlationId));
        }
        catch (Exception ex)
        {
            var wrapped = new EngineException(EngineErrors.Of(
                EngineErrors.Internal, ex.Message, correlationId: correlationId));
            WriteFailureAudit(command, correlationId, caller, sw, dryRun, wrapped.Error.Code, ex.StackTrace?.ToString(), argsSnapshot, ctx?.BatchId);
            LpLog.Error($"Command internal error: {command}", ex, category: "engine.pipeline");
            throw wrapped;
        }
        finally
        {
            if (gateOwned) _writeGate.Release();
        }
    }

    public async Task<T> QueryAsync<T>(string query, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
    {
        // 批的读流形态（batch.dry_run / batch.status）与写流同一处直路由
        if (BatchDispatch.IsBatchCommand(query))
            return await BatchDispatch.QueryAsync<T>(RequireBatch(), query, args, options, ct).ConfigureAwait(false);

        var correlationId = options?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var caller = CallOptions.CallerOf(options);

        _sessions?.Enforce(caller, isMutation: false, correlationId);

        // 调用上下文（corr / cmd / caller → 记录首类字段）：读链上的记录同样自动携带
        using var call = LpLog.BeginCall(correlationId, query, caller.ToString());

        var handler = ResolveOrThrow(query, correlationId);
        if (!handler.Descriptor.IsQuery)
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
                $"'{query}' is not a query command, use ExecuteAsync", correlationId: correlationId));

        var argsJson = EngineJson.ToJsonElement(args);
        var policy = handler.Descriptor.Cache;

        // 缓存路径（读流）：先取依赖世代快照，再触库；条目按快照存回。
        // 快照必须在读取之前取 —— 读取期间发生的写会推进世代戳，使本次条目立即失配（冷启动宁多回填一次，绝不留陈旧值）。
        if (policy is not null)
        {
            var key = QueryCache.BuildKey(query, argsJson);
            var stamp = _cache.Snapshot(policy.DependsOn);
            if (_cache.TryGet(key, stamp, out var hit)) return (T)hit!;

            await using var cachedUow = _uowFactory();
            var cachedCtx = new CommandContextImpl(cachedUow, isNested: false, dryRun: false, correlationId, caller, ct, this);
            var cachedResult = await handler.ExecuteAsync(cachedCtx, argsJson);
            if (cachedResult.Data is T cachedData)
            {
                _cache.Set(key, policy.DependsOn, stamp, cachedData, TimeSpan.FromSeconds(policy.TtlSeconds));
                return cachedData;
            }
            return (T?)cachedResult.Data
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.Internal,
                    $"query '{query}' returned no result", correlationId: correlationId));
        }

        // 读池：每查询一个短 UoW，免写闸、免审计、免撤销（WAL 下与写并发）
        await using var uow = _uowFactory();
        var ctx = new CommandContextImpl(uow, isNested: false, dryRun: false, correlationId, caller, ct, this);
        var result = await handler.ExecuteAsync(ctx, argsJson);
        return (T?)result.Data
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.Internal,
                $"query '{query}' returned no result", correlationId: correlationId));
    }

    /// <summary>
    /// 自描述（<c>engine.describe</c>；文档与 **AI 工具清单**的唯一来源）。
    /// 批三命令不进注册表（wire 直路由 <see cref="IBatchEngine"/>），但**只要批引擎已装配就并入**——
    /// 否则进程内消费者（AI / <c>EngineClient</c>）的工具清单会缺掉 <c>batch.*</c>，
    /// 而它们正是"多步操作的唯一正确形态"（AI-ASSISTANT §3.3 的 79 条口径）。
    /// 与目录导出（<see cref="EngineCatalog"/>）同口径；批未装配时原样返回注册表内容。
    /// </summary>
    public EngineManifest Describe(string? category = null)
    {
        var commands = _registry.Describe(category);
        if (Batch is not null && category is null or "batch")
            commands = commands.Concat(BatchEngine.Descriptors)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        return new EngineManifest(DateTimeOffset.Now, commands);
    }

    // ===== 嵌套派发（复用父 UoW 与写闸；同链串行绝无死锁）=====

    internal async Task<CommandResult> ExecuteNestedAsync(
        CommandContextImpl parent, string command, object? args, CancellationToken ct)
    {
        // 嵌套派发按 Descriptor 形态路由：既允许 mutation（复用父 UoW/写闸），也允许 query
        // （只读预检复用父 UoW，如 staging.inspect→bookmarks.inspect）——不强制 IsMutation，
        // 因查询嵌套是既有合法用法（守卫会误伤只读预检）。
        var json = EngineJson.ToJsonElement(args);
        // 嵌套入参快照与顶层同口径：先脱敏后截断（G4——否则 AI 无法自证"发过哪些参数"）。
        var nestedArgs = SnapshotArgs(json);
        var sw = Stopwatch.StartNew();
        try
        {
            var handler = ResolveOrThrow(command, parent.CorrelationId);

            // 当调用方显式传入自己的 ct（非 default）时，用 LinkedTokenSource 联合父 ct——
            // 父取消同样会传播到子命令；传 default（缺省路径）则直接继承父 ct（零开销等价）。
            var linked = ct == default ? null : CancellationTokenSource.CreateLinkedTokenSource(parent.Ct, ct);
            try
            {
                var childCtx = new CommandContextImpl(parent.Uow, isNested: true, parent.DryRun,
                    parent.CorrelationId, parent.Caller, linked?.Token ?? parent.Ct, this,
                    undoGroupId: parent.UndoGroupId, batchId: parent.BatchId);

                var result = await handler.ExecuteAsync(childCtx, json);

                // 嵌套变更加入父缓冲：父提交成功后随父事件一并发布（提交语义唯一归属父管道）
                parent.CollectNestedChange(result.Changes);

                // 嵌套审计记录实测耗时（此前恒为 0，诊断面丢失「哪一步慢」）。
                // 观测面纪律：嵌套子审计失败**不否定父命令**（只计数 + 记日志；顶上还有父审计条目兜底）。
                try
                {
                    _audit.Write(new AuditEntry(
                        DateTimeOffset.Now, command, parent.CorrelationId, parent.Caller,
                        sw.ElapsedMilliseconds, Success: true, ErrorCode: null, result.Changes,
                        DryRun: parent.DryRun, IsNested: true, StackTrace: null,
                        ArgsJson: nestedArgs.Json, BatchId: parent.BatchId, ArgsTruncated: nestedArgs.Truncated));
                }
                catch (Exception auditEx)
                {
                    RegisterObservationFailure("nested audit write failed", auditEx);
                }

                // 里程碑（Debug）：嵌套派发轨迹——与父命令同 correlation，可还原"一条用户动作"的完整链路
                if (LpLog.IsEnabled(LogLevel.Debug))
                    LpLog.Write(LogLevel.Debug, "engine.pipeline", $"Nested dispatch completed: {command}", props: new Dictionary<string, object?>
                    {
                        // 首类字段（corr / cmd / caller）= 外层调用（调用上下文）；被派发的子命令另给 nested_cmd，避免歧义
                        ["nested_cmd"] = command,
                        ["nested"] = true,
                    }, elapsedMs: sw.ElapsedMilliseconds);

                return result;
            }
            finally
            {
                linked?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 失败的嵌套步骤同样留痕（IsNested 子记录 + 错误码 + 入参 + batch_id）：
            // 否则 Continue/SkipAndLog 策略下"跳过的那一步"在 audit.query 里查无此步，取不齐整批。
            // 观测面纪律同上：审计写失败只计数，原始异常永远优先上抛。
            var code = ex switch
            {
                EngineException e => e.Error.Code,
                OperationCanceledException => EngineErrors.Cancelled,
                _ => EngineErrors.Internal,
            };
            try
            {
                _audit.Write(new AuditEntry(
                    DateTimeOffset.Now, command, parent.CorrelationId, parent.Caller,
                    sw.ElapsedMilliseconds, Success: false, ErrorCode: code, Changes: null,
                    DryRun: parent.DryRun, IsNested: true, StackTrace: ex.StackTrace?.ToString(),
                    ArgsJson: nestedArgs.Json, BatchId: parent.BatchId, ArgsTruncated: nestedArgs.Truncated));
            }
            catch (Exception auditEx)
            {
                RegisterObservationFailure("nested failure audit write failed", auditEx);
            }
            throw;
        }
    }

    /// <summary>
    /// 发布一次调用的全部事件（自身 + 嵌套聚合），并按事件名精确失效查询缓存。
    /// 事件负载 Data = 本次调用的合并变更集（去重后的受影响实体 + 事件名 + 人类摘要），
    /// 使订阅方（事件存储追平 / AI 轮询 / 未来的界面增量刷新）能按「变了哪些实体」增量处理，
    /// 而不是只知道「有变更」。
    /// </summary>
    private async Task PublishChangesAsync(ChangeSet? own, CommandContextImpl ctx, string correlationId, CallerRef caller)
    {
        var nested = ctx.TakeNestedChanges();

        var events = new List<string>();
        if (own?.Events is { } ownEvents) events.AddRange(ownEvents);
        events.AddRange(nested.Events);
        var distinctEvents = events.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctEvents.Length == 0) return;

        var touched = new List<EntityRef>();
        if (own?.Touched is { } ownTouched) touched.AddRange(ownTouched);
        touched.AddRange(nested.Touched);

        // 非致命问题（Warnings）一并进事件负载：订阅方/AI 能看到"部分可选项没做成"，与调用的结果面一致
        var warnings = new List<string>();
        if (own?.Warnings is { Count: > 0 } ownWarnings) warnings.AddRange(ownWarnings);
        if (nested.Warnings is { Count: > 0 } nestedWarnings) warnings.AddRange(nestedWarnings);

        // 字段级 diff 同样聚合（自身 + 嵌套，按发生顺序）：订阅方/AI 台账能看到每一步改了哪个字段。
        // 事件负载是**粗粒度广播**：diff 按**值**去重——嵌套步骤的变更在"经父缓冲发布"的命令
        // （macro.run 等：处理器把子步骤变更同时写进自身结果与父缓冲）里会出现两份，
        // 广播只留一份；命令结果与批报告保留完整序列（不去重，时间线保真）。
        var diff = new List<FieldChange>();
        if (own?.Diff is { Count: > 0 } ownDiff) diff.AddRange(ownDiff);
        if (nested.Diff is { Count: > 0 } nestedDiff) diff.AddRange(nestedDiff);

        var payload = ChangeSetPayload.From(new ChangeSet(
            touched.DistinctBy(r => (r.Type, r.Id)).ToArray(),
            distinctEvents,
            own?.HumanSummary,
            warnings.Count > 0 ? warnings.Distinct(StringComparer.Ordinal).ToArray() : null,
            diff.Count > 0 ? diff.Distinct().ToArray() : null));

        foreach (var name in distinctEvents)
            await PublishAsync(new DomainEvent(name, DateTimeOffset.Now, payload, correlationId, caller));
    }

    /// <summary>
    /// 发布单个领域事件（批引擎的事务批发布同样走这里，保证失效路径唯一）：
    /// 先推进缓存世代戳（状态变更对读者立即可见），再推订阅方——顺序固定，
    /// 任一订阅方异常都不会留下「已发布但缓存未失效」的窗口。
    /// </summary>
    internal async Task PublishAsync(DomainEvent e)
    {
        _cache.Invalidate([e.Name]);
        await _events.PublishAsync(e);
    }

    private ICommandHandler ResolveOrThrow(string command, string correlationId)
        => _registry.Resolve(command)
           ?? throw new EngineException(EngineErrors.Of(EngineErrors.UnknownCommand,
               $"unknown command '{command}'", correlationId: correlationId));

    /// <summary>
    /// 批/宏步骤的撤销统一入栈（E5）：提交成功后按归属键合并为**一条记录 N 逆向步**（撤销时逆序回绕、
    /// 重做时正序重放，与"一条记录 = 一次用户动作"同口径）。登记失败 = 观测面失败（已提交事实不否定）。
    /// </summary>
    internal void FlushPendingUndo(CommandContextImpl ctx, CallerRef caller)
    {
        if (Undo is null) return;
        foreach (var pending in ctx.TakePendingUndo())
        {
            try
            {
                Undo.Record(pending.Descriptor, pending.Args, caller, pending.Inverse, pending.GroupId);
            }
            catch (Exception undoEx)
            {
                RegisterObservationFailure("batch step undo registration failed", undoEx);
            }
        }
    }

    private Task<CommandResult<T>> DispatchBatchAsync<T>(string command, object? args,
        CallOptions? options, CancellationToken ct)
        => BatchDispatch.ExecuteAsync<T>(RequireBatch(), command, args, options, ct);

    private IBatchEngine RequireBatch()
        => Batch ?? throw new EngineException(EngineErrors.Of(EngineErrors.Internal,
            "batch engine not wired (OrchestrationHost)"));

    private void EnsureConfirmed(CommandDescriptor descriptor, CallOptions? options, string correlationId)
    {
        if (options?.ConfirmToken is { } token)
        {
            if (!_confirmTokens.ValidateAndConsume(token, descriptor.Name))
                throw new EngineException(EngineErrors.Of(EngineErrors.ConfirmExpired,
                    $"confirmation token for '{descriptor.Name}' is invalid or expired, start again",
                    correlationId: correlationId));
            return;
        }

        var issued = _confirmTokens.Issue(descriptor.Name);
        var details = JsonSerializer.SerializeToElement(new
        {
            impact = descriptor.Impact?.Text ?? descriptor.Category,
            confirm_token = issued,
            ttl_seconds = 60,
        });
        throw new EngineException(EngineErrors.Of(EngineErrors.ConfirmRequired,
            $"'{descriptor.Name}' is a destructive command and needs confirmation", details, correlationId: correlationId));
    }

    private static CommandResult<T> ToTyped<T>(CommandResult result)
    {
        // 幂等落表（SqlIdempotencyStore 跨实例还原）的 Data 是 JsonElement：按调用方类型反序列化，
        // 否则 (T?)JsonElement 强转会抛 InvalidCastException（同进程活对象命中路径不走这里）。
        var data = result.Data is JsonElement je && typeof(T) != typeof(JsonElement) && typeof(T) != typeof(object)
            ? je.Deserialize<T>(EngineJson.Options)
            : (T?)result.Data;
        return new(true, data, result.Changes, result.AuditRef);   // 非泛型 CommandResult 只承载成功结果（失败走异常）
    }

    /// <summary>入参快照（审计 ArgsJson 列）：空对象不记；**先脱敏**（敏感键的字符串值 + URL 查询串掩码，
    /// 走契约 <see cref="LogRedactor.RedactJson"/>）**再截断** 4000 字符并如实标记截断（v6 args_truncated）。
    /// 顺序不可颠倒：掩码只会让文本变短，先截后脱敏会白截一段、还会把半个敏感值留在末尾。
    /// 审计 args 的脱敏**无开关**——它是持久化的对外读面（<c>audit.query</c>），不给"忘记开"留口子。
    /// internal：批引擎的父审计条目复用同一实现（批脚本入参快照与单命令同口径）。</summary>
    internal static (string? Json, bool Truncated) SnapshotArgs(JsonElement argsJson)
    {
        if (argsJson.ValueKind != JsonValueKind.Object || argsJson.EnumerateObject().MoveNext() == false)
            return (null, false);
        const int max = 4000;
        var raw = LogRedactor.RedactJson(argsJson.GetRawText());
        return raw.Length <= max ? (raw, false) : (raw[..max], true);
    }

    /// <summary>
    /// 失败路径审计（观测面）：**审计失败绝不顶替原始异常**——只计数 + 记日志，原异常照常上抛。
    /// 历史缺陷：三处 catch 里裸调 <c>_audit.Write</c>，审计抛异常时错误码/栈被顶替且该异常自身无审计。
    /// </summary>
    private void WriteFailureAudit(string command, string correlationId, CallerRef caller, Stopwatch sw,
        bool dryRun, string errorCode, string? stackTrace, (string? Json, bool Truncated) argsSnapshot,
        string? batchId = null)
    {
        try
        {
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, errorCode, null, DryRun: dryRun, IsNested: false, stackTrace,
                ArgsJson: argsSnapshot.Json, BatchId: batchId, ArgsTruncated: argsSnapshot.Truncated));
        }
        catch (Exception auditEx)
        {
            RegisterObservationFailure("failure audit write failed", auditEx);
        }
    }

    /// <summary>
    /// 观测面失败登记：已提交的成功写遇到观测面（审计/事件发布）异常时调用——
    /// 不否定业务结果（保持成功返回），仅计数暴露 + 记日志（ARCHITECTURE 不变量 #10「失败要暴露」）。
    /// 日志统一走 <see cref="LpLog"/> 管道（未装配管道的宿主仍有计数与一次性 Trace 提示，不静默）。
    /// internal：同程序集编排层（BatchEngine 的父审计）复用同一计数口径。
    /// </summary>
    internal void RegisterObservationFailure(string what, Exception ex)
    {
        Interlocked.Increment(ref _observationFailures);
        LpLog.Warn($"observation surface failed (write already committed, call still returns success): {what}", ex, category: "engine.observe");
    }
}

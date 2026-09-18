using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 引擎核心（方案 2.3/3.1/4.2）。
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

    /// <summary>事件存储（方案 4.4 L3）：发布即写入的环形缓冲，追平/轮询入口。</summary>
    public IEventStore EventStore => _eventStore;

    /// <summary>批引擎（阶段 11 编排层；OrchestrationHost 装配后非空）。</summary>
    public IBatchEngine? Batch { get; set; }

    /// <summary>撤销协调器（阶段 11 编排层；OrchestrationHost 装配后非空）。</summary>
    public IUndoCoordinator? Undo { get; set; }

    /// <summary>查询结果缓存（阶段 12 性能加固）：声明了 <see cref="CommandDescriptor.Cache"/> 的查询才参与。</summary>
    public QueryCache Cache => _cache;

    /// <summary>运行时统计快照（诊断面；宿主/Host 接线进 diagnostics.collect）。</summary>
    public EngineRuntimeStats RuntimeStats
    {
        get
        {
            var c = _cache.Counters;
            return new EngineRuntimeStats(
                c.Entries, c.Hits, c.Misses, c.Evictions, c.Invalidations,
                _eventStore.Head.Sequence);
        }
    }

    // ===== 同程序集编排组件的内部访问器（BatchEngine/MacroRun 等复用写闸/UoW 工厂/审计/注册表）=====

    internal SemaphoreSlim WriteGate => _writeGate;
    internal Func<IUnitOfWork> UowFactory => _uowFactory;
    internal IAuditWriter Audit => _audit;
    internal CommandRegistry Registry => _registry;

    public async Task<CommandResult<T>> ExecuteAsync<T>(string command, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
    {
        var correlationId = options?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var caller = options?.Caller ?? CallerRef.Ui;
        var dryRun = options?.DryRun == true;
        var argsJson = EngineJson.ToJsonElement(args);   // 入参快照：审计 ArgsJson 与撤销登记共用

        // 能力门（方案 4.5）：会话存在性 + 只读拒绝写 + 限流（未登记会话零约束，兼容宿主自有调用）
        _sessions?.Enforce(caller, isMutation: true, correlationId);

        var handler = ResolveOrThrow(command, correlationId);
        if (!handler.Descriptor.IsMutation)
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
                $"「{command}」不是变更命令，请走 QueryAsync", correlationId: correlationId));

        // 幂等查重（在写闸之前；24h 窗口内命中即返回首次结果，不重复执行）
        if (options?.IdempotencyKey is { } key && _idempotency.TryGet(key, out var cached))
            return ToTyped<T>(cached);

        // 能力门：破坏性命令两阶段确认（干跑不消耗确认）
        if (handler.Descriptor.IsDestructive && !dryRun)
            EnsureConfirmed(handler.Descriptor, options, correlationId);

        var sw = Stopwatch.StartNew();
        bool gateOwned = false;
        try
        {
            await _writeGate.WaitAsync(ct);
            gateOwned = true;

            // 幂等二次确认（写闸内）：闸外首次查重只是快路径——两并发携带同一 IdempotencyKey
            // 可能都在提交前排过（都 miss），闸内复核保证后到者直接命中首次结果，绝不重复执行。
            if (options?.IdempotencyKey is { } recheckKey && _idempotency.TryGet(recheckKey, out var recheckCached))
                return ToTyped<T>(recheckCached);

            await using var uow = _uowFactory();
            // 干跑立即开启显式事务：绕过变更跟踪的批量语句（ExecuteDelete 等）只在本连接已有事务时才可回滚，
            // 否则"执行但不提交"会被绕开（改动直接落库）。
            ITransactionScope? tx = dryRun ? uow.BeginTransaction() : null;
            if (tx != null) await tx.BeginAsync(ct);
            CommandResult result;
            CommandContextImpl ctx;
            try
            {
                ctx = new CommandContextImpl(uow, isNested: false, dryRun, correlationId, caller, ct, this);
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
                // 阶段 12：事件携带 ChangeSet 负载（订阅方可做增量处理）+ 按事件名精确失效查询缓存
                await PublishChangesAsync(result.Changes, ctx, correlationId, caller);

                // 整库影响面的命令（maintenance.reinit）：表已清空，全部条目直接作废
                if (handler.Descriptor.Impact == ImpactSummary.Database) _cache.Clear();

                if (options?.IdempotencyKey is { } idemKey)
                    _idempotency.Store(idemKey, result);

                // 撤销登记（阶段 11）：顶层可撤销命令（Reversible + UndoInverse）成功后入栈
                Undo?.Record(handler.Descriptor, argsJson, caller);
            }

            var auditRef = _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: true, ErrorCode: null, result.Changes, DryRun: dryRun, IsNested: false, StackTrace: null,
                ArgsJson: TruncateArgs(argsJson)));

            return new CommandResult<T>(true, (T?)result.Data, result.Changes, auditRef);
        }
        catch (EngineException ex)
        {
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, ex.Error.Code, null, DryRun: dryRun, IsNested: false, ex.StackTrace?.ToString(),
                ArgsJson: TruncateArgs(argsJson)));
            throw;
        }
        catch (OperationCanceledException)
        {
            // 取消也落审计（观测面：所有调用可追溯，取消不例外）
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, EngineErrors.Cancelled, null, DryRun: dryRun, IsNested: false,
                StackTrace: null, ArgsJson: TruncateArgs(argsJson)));
            throw new EngineException(EngineErrors.Of(EngineErrors.Cancelled, "调用已取消", correlationId: correlationId));
        }
        catch (Exception ex)
        {
            var wrapped = new EngineException(EngineErrors.Of(
                EngineErrors.Internal, ex.Message, correlationId: correlationId));
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, wrapped.Error.Code, null, DryRun: dryRun, IsNested: false, ex.StackTrace?.ToString(),
                ArgsJson: TruncateArgs(argsJson)));
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
        var correlationId = options?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var caller = options?.Caller ?? CallerRef.Ui;

        _sessions?.Enforce(caller, isMutation: false, correlationId);

        var handler = ResolveOrThrow(query, correlationId);
        if (!handler.Descriptor.IsQuery)
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
                $"「{query}」不是查询命令，请走 ExecuteAsync", correlationId: correlationId));

        var argsJson = EngineJson.ToJsonElement(args);
        var policy = handler.Descriptor.Cache;

        // 缓存路径（方案 2.3 读流）：先取依赖世代快照，再触库；条目按快照存回。
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
                    $"查询「{query}」返回空结果", correlationId: correlationId));
        }

        // 读池：每查询一个短 UoW，免写闸、免审计、免撤销（WAL 下与写并发）
        await using var uow = _uowFactory();
        var ctx = new CommandContextImpl(uow, isNested: false, dryRun: false, correlationId, caller, ct, this);
        var result = await handler.ExecuteAsync(ctx, argsJson);
        return (T?)result.Data
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.Internal,
                $"查询「{query}」返回空结果", correlationId: correlationId));
    }

    public EngineManifest Describe(string? category = null)
        => new(DateTimeOffset.Now, _registry.Describe(category));

    // ===== 嵌套派发（复用父 UoW 与写闸；同链串行绝无死锁）=====

    internal async Task<CommandResult> ExecuteNestedAsync(
        CommandContextImpl parent, string command, object? args, CancellationToken ct)
    {
        var handler = ResolveOrThrow(command, parent.CorrelationId);
        var json = EngineJson.ToJsonElement(args);

        var childCtx = new CommandContextImpl(parent.Uow, isNested: true, parent.DryRun,
            parent.CorrelationId, parent.Caller, ct == default ? parent.Ct : ct, this);
        var result = await handler.ExecuteAsync(childCtx, json);

        // 嵌套变更加入父缓冲：父提交成功后随父事件一并发布（提交语义唯一归属父管道）
        parent.CollectNestedChange(result.Changes);

        _audit.Write(new AuditEntry(
            DateTimeOffset.Now, command, parent.CorrelationId, parent.Caller,
            ElapsedMs: 0, Success: true, ErrorCode: null, result.Changes,
            DryRun: parent.DryRun, IsNested: true, StackTrace: null));

        return result;
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

        var payload = JsonSerializer.SerializeToElement(new ChangeSet(
            touched.DistinctBy(r => (r.Type, r.Id)).ToArray(),
            distinctEvents,
            own?.HumanSummary,
            warnings.Count > 0 ? warnings.Distinct(StringComparer.Ordinal).ToArray() : null), EngineJson.Options);

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
               $"未知命令「{command}」", correlationId: correlationId));

    private void EnsureConfirmed(CommandDescriptor descriptor, CallOptions? options, string correlationId)
    {
        if (options?.ConfirmToken is { } token)
        {
            if (!_confirmTokens.ValidateAndConsume(token, descriptor.Name))
                throw new EngineException(EngineErrors.Of(EngineErrors.ConfirmExpired,
                    $"「{descriptor.Name}」的确认令牌无效或已过期，请重新发起",
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
            $"「{descriptor.Name}」是破坏性命令，需要二次确认", details, correlationId: correlationId));
    }

    private static CommandResult<T> ToTyped<T>(CommandResult result)
        => new(true, (T?)result.Data, result.Changes, result.AuditRef);   // 非泛型 CommandResult 只承载成功结果（失败走异常）

    /// <summary>入参快照截断（审计 ArgsJson 列；空对象不记，超长截 4000 字符）。</summary>
    private static string? TruncateArgs(JsonElement argsJson)
    {
        if (argsJson.ValueKind != JsonValueKind.Object || argsJson.EnumerateObject().MoveNext() == false)
            return null;
        const int max = 4000;
        var raw = argsJson.GetRawText();
        return raw.Length <= max ? raw : raw[..max];
    }
}

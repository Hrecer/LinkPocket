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

    public EngineCore(
        CommandRegistry registry,
        Func<IUnitOfWork> uowFactory,
        IAuditWriter? audit = null,
        IEventBus? eventBus = null,
        ConfirmTokenStore? confirmTokens = null,
        IdempotencyStore? idempotency = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _audit = audit ?? new InMemoryAuditWriter();
        _events = eventBus ?? new InMemoryEventBus();
        _confirmTokens = confirmTokens ?? new ConfirmTokenStore();
        _idempotency = idempotency ?? new IdempotencyStore();
        _writeGate = new SemaphoreSlim(1, 1);   // 全局单写闸：任意两写不重叠（现状数据闸语义保留）
    }

    /// <summary>事件总线（宿主可订阅做 UI 防抖刷新等；订阅方纪律 = 不得同步回派命令）。</summary>
    public IEventBus Events => _events;

    public async Task<CommandResult<T>> ExecuteAsync<T>(string command, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
    {
        var correlationId = options?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var caller = options?.Caller ?? CallerRef.Test;
        var dryRun = options?.DryRun == true;

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

            await using var uow = _uowFactory();
            ITransactionScope? tx = dryRun ? uow.BeginTransaction() : null;
            CommandResult result;
            CommandContextImpl ctx;
            try
            {
                ctx = new CommandContextImpl(uow, isNested: false, dryRun, correlationId, caller, ct, this);
                result = await handler.ExecuteAsync(ctx, EngineJson.ToJsonElement(args));

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
                foreach (var name in CollectEvents(result, ctx))
                    await _events.PublishAsync(new DomainEvent(name, DateTimeOffset.Now, null, correlationId, caller));

                if (options?.IdempotencyKey is { } idemKey)
                    _idempotency.Store(idemKey, result);
            }

            var auditRef = _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: true, ErrorCode: null, result.Changes, DryRun: dryRun, IsNested: false, StackTrace: null));

            return new CommandResult<T>(true, (T?)result.Data, result.Changes, auditRef);
        }
        catch (EngineException ex)
        {
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, ex.Error.Code, null, DryRun: dryRun, IsNested: false, ex.StackTrace?.ToString()));
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new EngineException(EngineErrors.Of(EngineErrors.Cancelled, "调用已取消", correlationId: correlationId));
        }
        catch (Exception ex)
        {
            var wrapped = new EngineException(EngineErrors.Of(
                EngineErrors.Internal, ex.Message, correlationId: correlationId));
            _audit.Write(new AuditEntry(
                DateTimeOffset.Now, command, correlationId, caller, sw.ElapsedMilliseconds,
                Success: false, wrapped.Error.Code, null, DryRun: dryRun, IsNested: false, ex.StackTrace?.ToString()));
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
        var caller = options?.Caller ?? CallerRef.Test;

        var handler = ResolveOrThrow(query, correlationId);
        if (!handler.Descriptor.IsQuery)
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
                $"「{query}」不是查询命令，请走 ExecuteAsync", correlationId: correlationId));

        // 读池：每查询一个短 UoW，免写闸、免审计、免撤销（WAL 下与写并发）
        await using var uow = _uowFactory();
        var ctx = new CommandContextImpl(uow, isNested: false, dryRun: false, correlationId, caller, ct, this);
        var result = await handler.ExecuteAsync(ctx, EngineJson.ToJsonElement(args));
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

        // 嵌套事件入父缓冲：父提交成功后随父事件一并发布（提交语义唯一归属父管道）
        foreach (var name in result.Changes?.Events ?? []) parent.CollectNestedEvent(name);

        _audit.Write(new AuditEntry(
            DateTimeOffset.Now, command, parent.CorrelationId, parent.Caller,
            ElapsedMs: 0, Success: true, ErrorCode: null, result.Changes,
            DryRun: parent.DryRun, IsNested: true, StackTrace: null));

        return result;
    }

    private IReadOnlyList<string> CollectEvents(CommandResult result, CommandContextImpl ctx)
    {
        var events = new List<string>();
        if (result.Changes?.Events is { } own) events.AddRange(own);
        events.AddRange(ctx.TakeNestedEvents());
        return events;
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
}

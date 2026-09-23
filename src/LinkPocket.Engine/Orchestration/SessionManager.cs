using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 会话管理器（ISessionManager）：会话登记 + 能力门校验 + 写入冻结（写锁）。
/// 校验规则（EngineCore / BatchEngine 在每次写前调用 <see cref="Enforce"/>）：
/// ① **写入冻结**：有写锁时，非持锁会话（含不带 SessionId 的界面/宿主调用）的写一律拒绝（WRITE_FROZEN_BY_AGENT）；
/// ② Caller 未带 SessionId 或会话未登记 = 宿主自有会话，不做其余约束（向后兼容既有调用方）；
/// ③ 只读会话（agent_readonly）拒绝一切变更命令（READONLY_SESSION）；
/// ④ 限流：滑动 60s 窗口计数（agent 默认 30 cmd/min），超限抛 RATE_LIMITED（可重试，附 retry_after_ms）。
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private sealed class SessionState
    {
        public required Session Session { get; init; }
        public readonly Queue<DateTimeOffset> Calls = new();
        public readonly object Lock = new();
    }

    /// <summary>agent 类会话的缺省限流（agent 默认 30 cmd/min）。</summary>
    public const int DefaultAgentRateLimitPerMinute = 30;

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    /// <summary>已结束会话的 tombstone：End 后携带旧 SessionId 的调用一律拒绝（能力门不得绕过）。
    /// 仅登记 id，不存状态；Begin 生成全新 id，永不撞车。</summary>
    private readonly ConcurrentDictionary<string, byte> _ended = new(StringComparer.Ordinal);

    /// <summary>写锁最大存活（安全兜底：持锁方崩溃 / 未释放时由下一次校验强制解除并留痕）。</summary>
    public static readonly TimeSpan DefaultWriteHoldMaxAge = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _writeHoldMaxAge;
    private readonly object _holdLock = new();
    private WriteHold? _hold;

    /// <param name="writeHoldMaxAge">写锁最大存活（缺省 30 分钟；测试可传短值）。</param>
    public SessionManager(TimeSpan? writeHoldMaxAge = null)
        => _writeHoldMaxAge = writeHoldMaxAge ?? DefaultWriteHoldMaxAge;

    public WriteHold? CurrentWriteHold
    {
        get
        {
            lock (_holdLock) return _hold;
        }
    }

    public IDisposable BeginWriteHold(string sessionId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_holdLock) _hold = new WriteHold(sessionId, reason ?? "", DateTimeOffset.UtcNow);
        return new WriteHoldHandle(this, sessionId);
    }

    private sealed class WriteHoldHandle(SessionManager owner, string sessionId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (owner._holdLock)
            {
                if (owner._hold is { } current && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
                    owner._hold = null;
            }
        }
    }

    /// <summary>取当前有效写锁（惰性解除超龄锁；解除如实留痕，不静默）。</summary>
    private WriteHold? ActiveHold()
    {
        lock (_holdLock)
        {
            if (_hold is not { } hold) return null;
            if (DateTimeOffset.UtcNow - hold.TakenAt <= _writeHoldMaxAge) return hold;
            _hold = null;
            LpLog.Warn(
                $"write hold released by max age ({_writeHoldMaxAge.TotalMinutes:N0} min): session={hold.SessionId}",
                category: "ai.writehold");
            return null;
        }
    }

    public Task<Session> BeginAsync(SessionProfile profile, CancellationToken ct = default)
    {
        var rate = profile.RateLimitPerMinute
            ?? (profile.Kind is SessionKind.Agent or SessionKind.AgentReadonly ? DefaultAgentRateLimitPerMinute : 0);
        var session = new Session(
            SessionId: $"s-{Guid.NewGuid():N}",
            Kind: profile.Kind,
            RateLimitPerMinute: rate,
            StartedAt: DateTimeOffset.UtcNow);
        _sessions[session.SessionId] = new SessionState { Session = session };
        return Task.FromResult(session);
    }

    public Task EndAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(sessionId, out _))
            _ended[sessionId] = 0;   // tombstone：不再出现于 _sessions → 未被 End 的老流程读不到，但 Enforce 仍可识别
        return Task.CompletedTask;
    }

    public Session? Get(string sessionId)
    {
        if (_ended.ContainsKey(sessionId)) return null;
        return _sessions.TryGetValue(sessionId, out var state) ? state.Session : null;
    }

    public void Enforce(CallerRef caller, bool isMutation, string correlationId)
    {
        // 写入冻结（写锁）：**在读/写分流之前**判定 —— 界面/宿主的写不带 SessionId，
        // 若晚于"未带会话即放行"那一行，冻结就永远拦不住界面。
        if (isMutation && ActiveHold() is { } hold
            && !string.Equals(caller.SessionId, hold.SessionId, StringComparison.Ordinal))
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.WriteFrozenByAgent,
                "writes are frozen while the AI assistant is modifying data (stop the AI turn or wait for it to finish)",
                details: System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    holder_session = hold.SessionId,
                    reason = hold.Reason,
                    taken_at = hold.TakenAt,
                }),
                correlationId: correlationId));
        }

        if (caller.SessionId is not { } id)
            return;   // 未带会话 = 宿主自有调用，零约束（既有兼容口径）
        // 已显式 End 的会话：带旧 id 的后续调用一律拒绝——只读保护与限流不能被「End + 重放」绕过
        if (_ended.ContainsKey(id))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound,
                "the session has ended and no longer accepts calls (call BeginAsync to start a new one)",
                correlationId: correlationId));
        if (!_sessions.TryGetValue(id, out var state))
            return;

        var session = state.Session;
        if (isMutation && session.Kind == SessionKind.AgentReadonly)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.ReadonlySession,
                "a read-only session rejects writes (use read-only queries or upgrade to a writable session)",
                correlationId: correlationId));

        if (session.RateLimitPerMinute <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        lock (state.Lock)
        {
            while (state.Calls.Count > 0 && now - state.Calls.Peek() > TimeSpan.FromMinutes(1))
                state.Calls.Dequeue();

            if (state.Calls.Count >= session.RateLimitPerMinute)
            {
                var retryAfterMs = (int)Math.Ceiling(
                    (TimeSpan.FromMinutes(1) - (now - state.Calls.Peek())).TotalMilliseconds);
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.RateLimited,
                    $"session rate limited: at most {session.RateLimitPerMinute} calls per minute, retry later",
                    details: System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        retry_after_ms = Math.Max(retryAfterMs, 1),
                        limit_per_minute = session.RateLimitPerMinute,
                    }),
                    retryable: true,
                    correlationId: correlationId));
            }

            state.Calls.Enqueue(now);
        }
    }
}

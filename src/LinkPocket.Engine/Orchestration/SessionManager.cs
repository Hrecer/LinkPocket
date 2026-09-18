using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 会话管理器（方案 4.5 ISessionManager）：会话登记 + 能力门校验。
/// 校验规则（EngineCore 在每次 Execute/Query 前调用 <see cref="Enforce"/>）：
/// ① Caller 未带 SessionId 或会话未登记 = 宿主自有会话，不做引擎侧约束（向后兼容既有调用方）；
/// ② 只读会话（agent_readonly）拒绝一切变更命令（READONLY_SESSION）；
/// ③ 限流：滑动 60s 窗口计数（agent 默认 30 cmd/min），超限抛 RATE_LIMITED（可重试，附 retry_after_ms）。
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private sealed class SessionState
    {
        public required Session Session { get; init; }
        public readonly Queue<DateTimeOffset> Calls = new();
        public readonly object Lock = new();
    }

    /// <summary>agent 类会话的缺省限流（方案 4.5：agent 默认 30 cmd/min）。</summary>
    public const int DefaultAgentRateLimitPerMinute = 30;

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    /// <summary>已结束会话的 tombstone：End 后携带旧 SessionId 的调用一律拒绝（能力门不得绕过）。
    /// 仅登记 id，不存状态；Begin 生成全新 id，永不撞车。</summary>
    private readonly ConcurrentDictionary<string, byte> _ended = new(StringComparer.Ordinal);

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
        if (caller.SessionId is not { } id)
            return;   // 未带会话 = 宿主自有调用，零约束（既有兼容口径）
        // 已显式 End 的会话：带旧 id 的后续调用一律拒绝——只读保护与限流不能被「End + 重放」绕过（审核 1.2）
        if (_ended.ContainsKey(id))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound,
                "会话已结束，无法继续调用（请重新 BeginAsync 开启新会话）",
                correlationId: correlationId));
        if (!_sessions.TryGetValue(id, out var state))
            return;

        var session = state.Session;
        if (isMutation && session.Kind == SessionKind.AgentReadonly)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.ReadonlySession,
                "只读会话拒绝写操作（请改用只读查询或升级为可写会话）",
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
                    $"会话限流：每分钟最多 {session.RateLimitPerMinute} 次调用，请稍后重试",
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

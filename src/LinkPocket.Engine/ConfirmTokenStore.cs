using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 破坏性命令两阶段确认令牌（LP.SEC.003/004）：
/// 无令牌调用 → CONFIRM_REQUIRED（Details 附 impact 摘要 + token + ttl）；
/// 持有效令牌重调 → 消费令牌并执行；过期/不匹配 → CONFIRM_EXPIRED。默认 60s 有效。
/// </summary>
public sealed class ConfirmTokenStore
{
    private sealed record Token(string Command, DateTimeOffset IssuedAt);

    private readonly ConcurrentDictionary<string, Token> _tokens = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public ConfirmTokenStore(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromSeconds(60);

    public string Issue(string command)
    {
        SweepExpired();   // 惰性清理：每次签发顺带清掉过期条目（长期宿主内存只增不减）
        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new Token(command, DateTimeOffset.Now);
        return token;
    }

    /// <summary>
    /// 校验并消费令牌（一次性）。<b>先校验后消费</b>：
    /// 仅在「命令匹配且未过期」时消费（TryRemove 成功=true）；命令不匹配或过期时
    /// <b>不误吞 token</b>——返回 false 但不移除，调用方修正命令后仍可复用；
    /// 过期条目交给 <see cref="Issue"/> 的惰性清理回收，避免死条目滞留。
    /// </summary>
    public bool ValidateAndConsume(string token, string command)
    {
        if (!_tokens.TryGetValue(token, out var entry)) return false;
        if (entry.Command != command || DateTimeOffset.Now - entry.IssuedAt > _ttl)
            return false;   // 校验失败：不消费（不误吞），返回 false
        return _tokens.TryRemove(token, out _);   // 校验通过后才消费（一次性）
    }

    private void SweepExpired()
    {
        var now = DateTimeOffset.Now;
        foreach (var kv in _tokens)
            if (now - kv.Value.IssuedAt > _ttl)
                _tokens.TryRemove(kv.Key, out _);
    }
}

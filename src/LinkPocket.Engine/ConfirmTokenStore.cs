using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 破坏性命令两阶段确认令牌（方案 3.2 LP.SEC.003/004）：
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
        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new Token(command, DateTimeOffset.Now);
        return token;
    }

    /// <summary>校验并消费令牌（一次性）：命令匹配且未过期 = true。</summary>
    public bool ValidateAndConsume(string token, string command)
    {
        if (!_tokens.TryRemove(token, out var entry)) return false;
        return entry.Command == command && DateTimeOffset.Now - entry.IssuedAt <= _ttl;
    }
}

using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 幂等键存储：key → 首次成功结果，24h 窗口内重复调用返回首次结果副本且不重复执行。
/// 进程内实现（双击防护/批内去重）；落表持久化见 <see cref="SqlIdempotencyStore"/>（可虚化供替换）。
/// </summary>
public class IdempotencyStore
{
    private sealed record Entry(CommandResult Result, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public IdempotencyStore(TimeSpan? window = null) => _window = window ?? TimeSpan.FromHours(24);

    public virtual bool TryGet(string key, out CommandResult result)
    {
        result = null!;
        if (_entries.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.Now - entry.At <= _window)
            {
                result = entry.Result;
                return true;
            }
            _entries.TryRemove(key, out _);
        }
        return false;
    }

    public virtual void Store(string key, CommandResult result) => _entries[key] = new Entry(result, DateTimeOffset.Now);
}

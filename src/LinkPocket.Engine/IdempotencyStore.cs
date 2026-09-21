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

    /// <summary>
    /// 清空全部幂等记录（内存 + 落表副本）。
    /// </summary>
    /// <remarks>
    /// <b>为什么整库影响面的命令必须清它</b>：
    /// `maintenance.reinit` / `backup.import replace=true` 把表清空了，但幂等记录留着 —— 调用方（脚本 / AI /
    /// 重试逻辑）在 24h 窗口内用**同一个 `IdempotencyKey`** 重放那次"导入/重建"，会直接命中旧的成功结果并原样返回，
    /// **库里什么都没发生**，而调用方以为已经导入成功（空库 + 假成功 = 事实上的数据缺失且无提示）。
    /// 表已清空时，"上次那条成功"就不再成立，幂等记录必须一起失效。
    /// </remarks>
    public virtual void Clear() => _entries.Clear();
}

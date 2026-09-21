namespace LinkPocket.Contracts;

/// <summary>事件游标：事件存储内的单调递增序号（追平/轮询的定位锚点）。</summary>
public readonly record struct EventCursor(long Sequence);

/// <summary>事件存储条目：全局序号 + 领域事件。</summary>
public sealed record StoredEvent(EventCursor Cursor, DomainEvent Event);

/// <summary>轮询页：条目 + 下一游标（无更多事件时 Next = null）。</summary>
public sealed record StoredEventPage(IReadOnlyList<StoredEvent> Items, EventCursor? Next);

/// <summary>
/// 事件存储（L3）：环形容量默认 5000 条，超出淘汰最旧；发布路径由引擎自动写入。
/// 实现口径：追平/轮询的消费方（新加入的会话、AI 宿主）都存活在引擎进程内，
/// 跨重启的持久历史已由 audit_log 承载 → 本存储为进程内环形缓冲，不落库（schema v2 约定不动）。
/// </summary>
public interface IEventStore
{
    /// <summary>当前最大序号（0 = 存储为空）。</summary>
    EventCursor Head { get; }

    /// <summary>追加事件（引擎发布路径自动调用；消费方一般不直调）。</summary>
    void Append(DomainEvent e);

    /// <summary>
    /// 追平：依序回放 <paramref name="from"/> 之后的全部事件（null = 从最早存活的事件开始）。
    /// 新消费者从上次游标续读即可不重不漏（容量淘汰截断的最早事件除外）。
    /// </summary>
    IAsyncEnumerable<StoredEvent> FollowAsync(EventCursor? from = null, CancellationToken ct = default);

    /// <summary>轮询：取 <paramref name="from"/> 之后最多 <paramref name="limit"/> 条（AI 轮询入口）。</summary>
    Task<StoredEventPage> PollAsync(EventCursor? from = null, int limit = 100, CancellationToken ct = default);
}

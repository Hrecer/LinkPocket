using System.Runtime.CompilerServices;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 进程内环形事件存储（L3 实现口径）：
/// - 追平/轮询的消费方（新加入的会话、AI 宿主）都存活在引擎进程内，环形缓冲即可满足；
/// - 跨重启的持久历史由 audit_log 承载，schema v2 无事件表（定稿不动）；
/// - Append 由引擎发布路径自动调用（随总线订阅，见 EngineCore），消费方一般不直调。
/// 线程模型：发布在写闸内串行发生，但轮询方可能来自任意线程 → 全部读写走锁。
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly object _lock = new();
    private readonly Queue<StoredEvent> _ring;
    private readonly int _capacity;
    private long _sequence;

    public InMemoryEventStore(int capacity = 5000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ring = new Queue<StoredEvent>(Math.Min(capacity, 64));
    }

    public EventCursor Head
    {
        get
        {
            lock (_lock) return new EventCursor(_sequence);
        }
    }

    public void Append(DomainEvent e)
    {
        lock (_lock)
        {
            _sequence++;
            _ring.Enqueue(new StoredEvent(new EventCursor(_sequence), e));
            while (_ring.Count > _capacity)
                _ring.Dequeue();   // 环形淘汰最旧：追平方最多丢容量窗口外的前缀
        }
    }

    public async IAsyncEnumerable<StoredEvent> FollowAsync(
        EventCursor? from = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 快照后回放：不持锁迭代（发布持闸同步、消费端异步纪律互不阻塞）
        StoredEvent[] snapshot;
        lock (_lock)
        {
            snapshot = _ring
                .Where(ev => from is null || ev.Cursor.Sequence > from.Value.Sequence)
                .ToArray();
        }

        foreach (var ev in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ev;
        }
    }

    public Task<StoredEventPage> PollAsync(
        EventCursor? from = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock)
        {
            var items = _ring
                .Where(ev => from is null || ev.Cursor.Sequence > from.Value.Sequence)
                .Take(limit)
                .ToArray();
            var next = items.Length > 0 ? items[^1].Cursor : (EventCursor?)null;
            return Task.FromResult(new StoredEventPage(items, next));
        }
    }
}

using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// 内存日志环（进程内最近 N 条）：服务 <c>logs.query</c> 的"不读文件即可查最近日志"水位，
/// 也是测试的注入点（断言记录内容）。环形满即淘汰最旧（计入 <see cref="LogStats.Dropped"/>）；线程安全。
/// </summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly object _lock = new();
    private readonly Queue<LogRecord> _ring;
    private readonly int _capacity;
    private long _written;
    private long _evicted;

    public MemoryLogSink(int capacity = 2000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ring = new Queue<LogRecord>(Math.Min(capacity, 256));
    }

    /// <summary>容量（构造后不可变）。</summary>
    public int Capacity => _capacity;

    public bool IsEnabled(LogLevel level) => true;

    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _ring.Enqueue(record);
            _written++;
            while (_ring.Count > _capacity)
            {
                _ring.Dequeue();
                _evicted++;
            }
        }
    }

    public void Flush(TimeSpan timeout)
    {
        // 纯内存：无缓冲可刷
    }

    public LogStats Stats => new(Written: Interlocked.Read(ref _written), Dropped: Interlocked.Read(ref _evicted));

    /// <summary>按游标取快照（<paramref name="afterSequence"/> = 0 从头；<paramref name="limit"/> = 0 取全部）。</summary>
    public IReadOnlyList<LogRecord> Snapshot(long afterSequence = 0, int limit = 0)
    {
        lock (_lock)
        {
            IEnumerable<LogRecord> items = _ring.Where(r => r.Sequence > afterSequence);
            if (limit > 0) items = items.Take(limit);
            return items.ToArray();
        }
    }
}

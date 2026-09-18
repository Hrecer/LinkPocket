using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 审计条目（方案 2.3 写流"全程携带 correlationId 与幂等键"；阶段 11 落 audit_log 表）。
/// ArgsJson = 入参快照（超长截断）；BatchId = 批父条目关联列；两列均可空、缺省不传。
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset At,
    string Command,
    string CorrelationId,
    CallerRef Caller,
    long ElapsedMs,
    bool Success,
    string? ErrorCode,
    ChangeSet? Changes,
    bool DryRun,
    bool IsNested,
    string? StackTrace,
    string? ArgsJson = null,
    string? BatchId = null);

/// <summary>审计写入器契约（进程内环形 + 阶段 11 落 audit_log 表）。</summary>
public interface IAuditWriter
{
    /// <summary>写入并返回审计引用（correlationId 即引用锚点）。</summary>
    string Write(AuditEntry entry);
}

/// <summary>组合审计写入器：每条目写全部下游（内存快照 + 落库），返回第一个引用。</summary>
public sealed class CompositeAuditWriter : IAuditWriter
{
    private readonly IAuditWriter[] _writers;

    public CompositeAuditWriter(params IAuditWriter[] writers)
        => _writers = writers.Length > 0 ? writers : throw new ArgumentException("至少需要一个审计写入器", nameof(writers));

    public string Write(AuditEntry entry)
    {
        var reference = _writers[0].Write(entry);
        foreach (var w in _writers.Skip(1)) w.Write(entry);
        return reference;
    }
}

/// <summary>进程内审计写入器：环形上限 10000 条，仅保内存最近记录（落库由 <see cref="SqlAuditWriter"/> 承担）。</summary>
public sealed class InMemoryAuditWriter : IAuditWriter
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private const int Capacity = 10_000;
    private int _count;   // 软容量计数器：ConcurrentQueue.Count 是 O(n) 快照，审计为热路径不可每次全遍历

    public string Write(AuditEntry entry)
    {
        _entries.Enqueue(entry);
        // 双检查顺序：先进后量——并发下容量仅为软上限（原 while(Count>Cap) 同理）
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            _entries.TryDequeue(out _);
            Interlocked.Decrement(ref _count);
        }
        return entry.CorrelationId;
    }

    public IReadOnlyList<AuditEntry> Snapshot()
        => [.. _entries];
}

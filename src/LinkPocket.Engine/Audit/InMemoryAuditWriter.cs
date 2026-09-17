using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>审计条目（方案 2.3 写流"全程携带 correlationId 与幂等键"；schema v2 落库随阶段 5）。</summary>
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
    string? StackTrace);

/// <summary>审计写入器契约（Phase 3 进程内环形；阶段 5 落 audit_log 表并按月归档）。</summary>
public interface IAuditWriter
{
    /// <summary>写入并返回审计引用（correlationId 即引用锚点）。</summary>
    string Write(AuditEntry entry);
}

/// <summary>进程内审计写入器：环形上限 10000 条，仅保内存最近记录（完整审计落库在阶段 5）。</summary>
public sealed class InMemoryAuditWriter : IAuditWriter
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private const int Capacity = 10_000;

    public string Write(AuditEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity) _entries.TryDequeue(out _);
        return entry.CorrelationId;
    }

    public IReadOnlyList<AuditEntry> Snapshot()
        => [.. _entries];
}

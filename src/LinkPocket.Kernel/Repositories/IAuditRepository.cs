namespace LinkPocket.Kernel;

/// <summary>
/// 审计查询过滤（全部可选；null = 不过滤）。时间语义：<c>at</c> 列是带本地时区偏移的 ISO-8601 文本
/// （与全库时间列同一口径，可字典序比较），因此过滤边界在实现侧**归一成本地偏移**后再做字符串比较——
/// 调用方传 UTC 或任意偏移的时刻都按"同一时刻"处理。
/// </summary>
public sealed record AuditQueryFilter
{
    /// <summary>命令名（精确）。</summary>
    public string? Command { get; init; }

    /// <summary>调用方文本（精确，形如 <c>ui:-</c> / <c>agent:s1</c>）。</summary>
    public string? Caller { get; init; }

    public string? SessionId { get; init; }
    public string? CorrelationId { get; init; }
    public string? BatchId { get; init; }
    public bool? Success { get; init; }

    /// <summary>只取嵌套子记录 / 只取顶层记录（null = 不过滤）。</summary>
    public bool? IsNested { get; init; }

    public bool? DryRun { get; init; }

    /// <summary>时间下界（含）。</summary>
    public DateTime? From { get; init; }

    /// <summary>时间上界（**不含**——半开区间，分页与保留同口径）。</summary>
    public DateTime? To { get; init; }
}

/// <summary>
/// 审计行（读侧形态）。"重负载列"（args/changes/stack）默认不取（见 <see cref="IAuditRepository.QueryAsync"/>
/// 的 <c>includePayloads</c>）——10k 导入这类命令的 changes_json 可以很大；
/// <see cref="ArgsTruncated"/> 始终返回（它是"入参快照是否完整"的如实标记，成本为零）。
/// </summary>
public sealed record AuditRecord(
    long Id,
    DateTimeOffset At,
    string? SessionId,
    string Caller,
    string Command,
    long ElapsedMs,
    bool Success,
    string? ErrorCode,
    string? BatchId,
    string CorrelationId,
    bool IsNested,
    bool DryRun,
    bool ArgsTruncated,
    string? ArgsJson,
    string? ChangesJson,
    string? StackTrace);

/// <summary>审计分页结果（<see cref="Total"/> = 满足过滤条件的总行数，与分页无关）。</summary>
public sealed record AuditPage(IReadOnlyList<AuditRecord> Items, int Total);

/// <summary>
/// 审计存储（**读侧 + 保留策略**的唯一契约）：写入由引擎管道承担（<c>IAuditWriter</c>），
/// 这里只服务 <c>audit.query</c>（AI/排障读取调用史）与 <c>audit.prune</c>（按时间清理）。
/// </summary>
public interface IAuditRepository
{
    /// <summary>按过滤条件倒序（时间新→旧、同刻按 id 新→旧）分页取审计行。</summary>
    Task<AuditPage> QueryAsync(
        AuditQueryFilter filter, int skip, int take, bool includePayloads, CancellationToken ct);

    /// <summary>删除 <paramref name="before"/> 之前的审计行（半开：严格早于）。返回删除行数。</summary>
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken ct);

    /// <summary>审计行总数。</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>最旧一行的时刻（空表 = null）。保留策略的判据读数（据它提醒"该清理了"）。</summary>
    Task<DateTimeOffset?> OldestAtAsync(CancellationToken ct);
}

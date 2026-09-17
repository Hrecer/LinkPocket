namespace LinkPocket.Modules.Links;

/// <summary>链接域命令结果 DTO 组（一文件一主类规则的 DTO 组例外）。</summary>
/// <summary>links.query 的分页结果（items 在 fields 投影时为字典列表，否则为 LinkDto 列表）。</summary>
public sealed record PagedLinkResult(
    IReadOnlyList<object> Items,
    int Total,
    int Page,
    int PageCount);

/// <summary>links.trash 结果（回收站保留原 ID + 位置快照口径）。</summary>
public sealed record LinkTrashResult(string LinkId, string? OriginPath);

/// <summary>批量命令共用结果。</summary>
public sealed record LinkBatchResult(string Kind, int Affected);

/// <summary>links.export 结果。</summary>
public sealed record LinkExportResult(string FilePath, string Format, int Count, long FileBytes);

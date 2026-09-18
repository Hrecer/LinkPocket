namespace LinkPocket.Modules.Links;

/// <summary>链接域命令结果 DTO 组（一文件一主类规则的 DTO 组例外）。</summary>
/// <summary>links.query 的分页结果（items 在 fields 投影时为字典列表，否则为 LinkDto 列表）。
/// ★引擎能力命令（不接 UI）；其余链接域结果 DTO 已提升至 Contracts。</summary>
public sealed record PagedLinkResult(
    IReadOnlyList<object> Items,
    int Total,
    int Page,
    int PageCount);

namespace LinkPocket.Kernel;

/// <summary>
/// 标准参数解析（方案 3.3）：sortBy/sortOrder 字符串 → <see cref="SortSpec"/> 列表。
/// 字段白名单 = 排序引擎可下推的全部稳定字段名；白名单外字段回落 <paramref name="fallback"/>（既有口径）。
/// </summary>
public static class QueryParsing
{
    /// <summary>链接排序白名单（与 Data 排序引擎的 LinkFields 一致）。</summary>
    public static readonly IReadOnlySet<string> LinkSortFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "url", "created_at", "updated_at", "last_visited_at", "visit_count", "is_important",
    };

    /// <summary>文件夹排序白名单（与 Data 排序引擎的 FolderFields 一致）。</summary>
    public static readonly IReadOnlySet<string> FolderSortFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "created_at", "updated_at", "visit_count", "sort_order",
    };

    public static IReadOnlyList<SortSpec> ParseSort(
        string? sortBy, string? sortOrder, IReadOnlySet<string> allowed, string fallback)
    {
        var field = !string.IsNullOrEmpty(sortBy) && allowed.Contains(sortBy) ? sortBy : fallback;
        var dir = string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) ? SortDir.Asc : SortDir.Desc;
        return [new SortSpec(field, dir)];
    }
}

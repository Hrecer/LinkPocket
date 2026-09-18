namespace LinkPocket.Kernel;

/// <summary>
/// 标准参数解析：sortBy/sortOrder 字符串 → <see cref="SortSpec"/> 列表。
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

    /// <summary>
    /// 列表列头口径的排序方向归一：**只有 "desc" 是降序，其余一律升序**（含缺省与非法值）。
    /// 「先归一、再 ParseSort」把两处方向口径收敛成一条，避免同一参数在不同命令里默认值不同。
    /// </summary>
    public static string NormalizeOrder(string? sortOrder)
        => string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
}

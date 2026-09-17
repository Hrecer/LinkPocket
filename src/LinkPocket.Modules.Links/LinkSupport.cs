using LinkPocket.Contracts;
using LinkPocket.Api;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>链接域内部支撑：内存排序（含 null-last 口径）/ 分页 / LinkCount 缓存回填。</summary>
internal static class LinkSupport
{
    /// <summary>
    /// 链接内存排序（与既有 GetLinksAsync/GetRootLevelLinksAsync 逐条等价）：
    /// 「最后查看」升序时从未查看（null）恒排最后；标题 CurrentCulture；ID 次序兜底。
    /// </summary>
    public static List<Link> SortLinks(IEnumerable<Link> source, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<Link> ordered = sortBy switch
        {
            "created_at" => desc ? source.OrderByDescending(l => l.CreatedAt) : source.OrderBy(l => l.CreatedAt),
            "updated_at" => desc ? source.OrderByDescending(l => l.UpdatedAt) : source.OrderBy(l => l.UpdatedAt),
            "last_visited_at" => desc
                ? source.OrderBy(l => l.LastVisitedAt == null).ThenByDescending(l => l.LastVisitedAt)
                : source.OrderBy(l => l.LastVisitedAt == null).ThenBy(l => l.LastVisitedAt),
            "visit_count" => desc ? source.OrderByDescending(l => l.VisitCount) : source.OrderBy(l => l.VisitCount),
            "title" => desc
                ? source.OrderByDescending(l => l.Title, StringComparer.CurrentCulture)
                : source.OrderBy(l => l.Title, StringComparer.CurrentCulture),
            _ => desc ? source.OrderByDescending(l => l.CreatedAt) : source.OrderBy(l => l.CreatedAt),
        };
        return ordered.ThenBy(l => l.LinkId, StringComparer.Ordinal).ToList();
    }

    /// <summary>日期入参解析（ISO 字符串；解析失败 = LP.VAL.002）。</summary>
    public static DateTime ParseDate(string raw, string context)
    {
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var value))
            return value;
        throw new EngineException(EngineErrors.Of(
            EngineErrors.TypeMismatch, $"「{context}」不是可识别的日期：{raw}"));
    }

    /// <summary>直接子链接计数缓存回填（LinkCount 列的既有维护口径）。</summary>
    public static async Task RefreshLinkCountAsync(Kernel.IUnitOfWork uow, string folderId, CancellationToken ct)
    {
        var counts = await uow.Links.CountByFolderAsync(ct);
        var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
        if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(folderId));
    }
}

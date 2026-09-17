using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Folders;

/// <summary>文件夹域内部支撑：后代收集 / 排序 / 面包屑（与既有实现逐条等价，internal）。</summary>
internal static class FolderSupport
{
    /// <summary>递归收集后代文件夹 ID（不含自身；内存树遍历——文件夹数量有限）。</summary>
    public static void CollectDescendantIds(IReadOnlyList<Folder> allFolders, string folderId, List<string> into)
    {
        foreach (var child in allFolders.Where(f => f.ParentId == folderId))
        {
            into.Add(child.FolderId);
            CollectDescendantIds(allFolders, child.FolderId, into);
        }
    }

    /// <summary>
    /// 子文件夹排序（与既有 SortFolders 逐条等价）：
    /// 名称与各维度都遵循升/降序；「最后查看」为空（从未）恒排最后；名称做同序稳定兜底（CurrentCulture）。
    /// </summary>
    public static List<FolderDto> SortFolders(IEnumerable<FolderDto> source, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        var ordered = sortBy switch
        {
            "updated_at" => desc ? source.OrderByDescending(f => f.UpdatedAt) : source.OrderBy(f => f.UpdatedAt),
            "created_at" => desc ? source.OrderByDescending(f => f.CreatedAt) : source.OrderBy(f => f.CreatedAt),
            "visit_count" => desc ? source.OrderByDescending(f => f.VisitCount) : source.OrderBy(f => f.VisitCount),
            "last_visited_at" => desc
                ? source.OrderBy(f => f.LastVisitedAt == null).ThenByDescending(f => f.LastVisitedAt)
                : source.OrderBy(f => f.LastVisitedAt == null).ThenBy(f => f.LastVisitedAt),
            _ => desc
                ? source.OrderByDescending(f => f.Name, StringComparer.CurrentCulture)
                : source.OrderBy(f => f.Name, StringComparer.CurrentCulture),
        };
        return ordered.ThenBy(f => f.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// 链接内存排序（与既有 SortLinks/GetLinksAsync 逐条等价）：
    /// 标题按 CurrentCulture；从未查看（null）恒排最后；ID 次序兜底。
    /// 文件夹目录页的链接列表全走本实现（每页数据量有界，内存排序 = 行为完全等价的最短路径）。
    /// </summary>
    public static List<Link> SortLinks(IEnumerable<Link> source, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<Link> ordered = sortBy switch
        {
            "title" => desc
                ? source.OrderByDescending(l => l.Title, StringComparer.CurrentCulture)
                : source.OrderBy(l => l.Title, StringComparer.CurrentCulture),
            "updated_at" => desc ? source.OrderByDescending(l => l.UpdatedAt) : source.OrderBy(l => l.UpdatedAt),
            "last_visited_at" => desc
                ? source.OrderByDescending(l => l.LastVisitedAt ?? DateTime.MinValue)
                : source.OrderBy(l => l.LastVisitedAt ?? DateTime.MinValue),
            "visit_count" => desc ? source.OrderByDescending(l => l.VisitCount) : source.OrderBy(l => l.VisitCount),
            "created_at" => desc ? source.OrderByDescending(l => l.CreatedAt) : source.OrderBy(l => l.CreatedAt),
            _ => desc ? source.OrderByDescending(l => l.CreatedAt) : source.OrderBy(l => l.CreatedAt),
        };
        return ordered.ThenBy(l => l.LinkId, StringComparer.Ordinal).ToList();
    }

    /// <summary>面包屑：「全部书签 / A / B」（与既有 BuildBreadcrumb 逐条等价）。</summary>
    public static List<string> BuildBreadcrumb(Folder folder, IReadOnlyList<Folder> allFolders)
    {
        var dict = allFolders.ToDictionary(f => f.FolderId);
        var parts = new List<string>();
        var currentId = folder.FolderId;
        for (var i = 0; i < 50 && dict.TryGetValue(currentId, out var f); i++)
        {
            parts.Insert(0, f.Name);
            currentId = f.ParentId ?? string.Empty;
        }

        return new List<string> { FolderIds.RootDisplayName }.Concat(parts).ToList();
    }
}

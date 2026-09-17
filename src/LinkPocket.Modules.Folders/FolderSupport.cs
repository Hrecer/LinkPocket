using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Folders;

/// <summary>文件夹域内部支撑：后代收集 / 排序 / 面包屑（internal）。</summary>
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
    /// <c>sort_order</c> = 手动排序（<c>folders.sort</c> 写入的列），此前只有写路径没有读路径，现接通。
    /// 仅用于**一次读出的同一个目录的直接子文件夹**（数量有限），链接列表的排序一律 SQL 下推。
    /// </summary>
    public static List<FolderDto> SortFolders(IEnumerable<FolderDto> source, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        var ordered = sortBy switch
        {
            "updated_at" => desc ? source.OrderByDescending(f => f.UpdatedAt) : source.OrderBy(f => f.UpdatedAt),
            "created_at" => desc ? source.OrderByDescending(f => f.CreatedAt) : source.OrderBy(f => f.CreatedAt),
            "visit_count" => desc ? source.OrderByDescending(f => f.VisitCount) : source.OrderBy(f => f.VisitCount),
            "sort_order" => desc ? source.OrderByDescending(f => f.SortOrder) : source.OrderBy(f => f.SortOrder),
            "last_visited_at" => desc
                ? source.OrderBy(f => f.LastVisitedAt == null).ThenByDescending(f => f.LastVisitedAt)
                : source.OrderBy(f => f.LastVisitedAt == null).ThenBy(f => f.LastVisitedAt),
            _ => desc
                ? source.OrderByDescending(f => f.Name, StringComparer.CurrentCulture)
                : source.OrderBy(f => f.Name, StringComparer.CurrentCulture),
        };
        return ordered.ThenBy(f => f.Name, StringComparer.CurrentCulture).ToList();
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

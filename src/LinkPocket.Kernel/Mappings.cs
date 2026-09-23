using LinkPocket.Contracts;
using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>
/// 实体 → 契约 DTO 映射（跨模块复用的唯一合法形式 = L0）。
/// 九个业务模块共用同一份映射，保证任何命令产出的 DTO 字段口径完全一致。
/// </summary>
public static class Mappings
{
    public static Contracts.LinkDto ToDto(this Link link) => new()
    {
        LinkId = link.LinkId,
        Url = link.Url,
        Title = link.Title ?? string.Empty,
        Description = link.Description ?? string.Empty,
        FaviconUrl = link.FaviconUrl ?? string.Empty,
        ListId = link.ListId,
        LastVisitedAt = link.LastVisitedAt,
        VisitCount = link.VisitCount,
        IsImportant = link.IsImportant,
        CreatedAt = link.CreatedAt,
        UpdatedAt = link.UpdatedAt,
    };

    /// <summary>
    /// 树叶子专用的精简投影：只带走目录树真正读的四个字段。
    /// 用于"全库链接"这种大集合（<c>folders.overview</c> 的 <c>tree_links</c>）——
    /// 完整 <see cref="Contracts.LinkDto"/> 会连描述、时间戳、访问计数一起搬运，而树一个都不看。
    /// </summary>
    public static Contracts.TreeLinkDto ToTreeDto(this Link link) => new()
    {
        LinkId = link.LinkId,
        Title = link.Title ?? string.Empty,
        Url = link.Url,
        ListId = link.ListId,
    };

    /// <param name="counts">
    /// 链接计数两口径（<see cref="ITreeService.LinkCountsAsync"/> 的结果）：
    /// <c>LinkCount</c> = 递归（含子孙），<c>DirectLinkCount</c> = 直接子链接数。
    /// 传 null 时两个字段置 0（调用方明确放弃计数口径，如纯列表场景）。
    /// </param>
    public static Contracts.FolderDto ToDto(this Folder folder, FolderLinkCounts? counts)
        => new()
        {
            FolderId = folder.FolderId,
            Name = folder.Name,
            ParentId = folder.ParentId,
            LinkCount = counts != null && counts.Recursive.TryGetValue(new FolderId(folder.FolderId), out var recursive)
                ? recursive
                : 0,
            DirectLinkCount = counts != null && counts.Direct.TryGetValue(new FolderId(folder.FolderId), out var direct)
                ? direct
                : 0,
            SortOrder = folder.SortOrder,
            UpdatedAt = folder.UpdatedAt,
            CreatedAt = folder.CreatedAt,
            LastVisitedAt = folder.LastVisitedAt,
            VisitCount = folder.VisitCount,
        };
}

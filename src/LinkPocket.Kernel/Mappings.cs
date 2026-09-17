using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>
/// 实体 → 契约 DTO 映射（跨模块复用的唯一合法形式 = L0，方案 2.2/4.1）。
/// 九个业务模块共用同一份映射，保证任何命令产出的 DTO 字段口径完全一致。
/// </summary>
public static class Mappings
{
    public static Api.LinkDto ToDto(this Link link) => new()
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

    /// <param name="recursiveCounts">
    /// 递归链接计数（<see cref="ITreeService.RecursiveLinkCountsAsync"/> 的结果）；
    /// 传 null 时 LinkCount 置 0（调用方明确放弃计数口径，如纯列表场景）。
    /// </param>
    public static Api.FolderDto ToDto(this Folder folder, IReadOnlyDictionary<FolderId, int>? recursiveCounts)
        => new()
        {
            FolderId = folder.FolderId,
            Name = folder.Name,
            ParentId = folder.ParentId,
            LinkCount = recursiveCounts != null && recursiveCounts.TryGetValue(new FolderId(folder.FolderId), out var count)
                ? count
                : 0,
            UpdatedAt = folder.UpdatedAt,
            CreatedAt = folder.CreatedAt,
            LastVisitedAt = folder.LastVisitedAt,
            VisitCount = folder.VisitCount,
        };
}

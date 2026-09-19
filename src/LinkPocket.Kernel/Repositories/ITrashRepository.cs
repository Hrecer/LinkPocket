using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>回收站两表（trash_links / trash_folders）的完整读写契约。</summary>
public interface ITrashRepository
{
    // —— 链接快照 ——
    Task<TrashedLink?> FindLinkAsync(LinkId id, CancellationToken ct);

    /// <summary>单独删除的书签快照（TrashFolderId == NULL），按删除时间倒序（行为等价项：平铺口径）。</summary>
    Task<IReadOnlyList<TrashedLink>> ListStandaloneLinksAsync(CancellationToken ct);

    /// <summary>单独删除的书签计数（与 <see cref="ListStandaloneLinksAsync"/> 同口径的 SQL COUNT；
    /// diagnostics 等只计数场景使用，绝不拉全量）。</summary>
    Task<int> CountStandaloneLinksAsync(CancellationToken ct);

    /// <summary>某回收站单元的直接书签快照（TrashFolderId == unit）。</summary>
    Task<IReadOnlyList<TrashedLink>> ListLinksByUnitAsync(TrashFolderId unit, CancellationToken ct);

    /// <summary>各回收站单元的书签快照计数。</summary>
    Task<IReadOnlyDictionary<TrashFolderId, int>> CountLinksByUnitAsync(CancellationToken ct);

    Task<TrashedLink> AddLinkAsync(TrashedLink snapshot, CancellationToken ct);
    Task RemoveLinkAsync(LinkId id, CancellationToken ct);

    /// <summary>搬移链接快照到目标单元（<paramref name="unit"/> = null → 回收站根，即"单独删除"位）。
    /// 只改归属（trash_folder_id），其余快照字段（原位置/时间戳等）一律不动；提交权归工作单元。返回是否命中。</summary>
    Task<bool> MoveLinkAsync(LinkId id, TrashFolderId? unit, CancellationToken ct);

    // —— 文件夹单元 ——
    Task<TrashedFolder?> FindFolderAsync(TrashFolderId id, CancellationToken ct);

    /// <summary>全部单元（树装配在模块层组合），按删除时间倒序。</summary>
    Task<IReadOnlyList<TrashedFolder>> ListFoldersAsync(CancellationToken ct);

    /// <summary>被删文件夹总数（含子单元；SQL COUNT，diagnostics 只计数场景）。</summary>
    Task<int> CountFoldersAsync(CancellationToken ct);

    Task<TrashedFolder> AddFolderAsync(TrashedFolder unit, CancellationToken ct);
    Task RemoveFolderAsync(TrashFolderId id, CancellationToken ct);

    /// <summary>搬移单元到目标父单元（<paramref name="parent"/> = null → 回收站根）。
    /// 只改层级（parent_id），名称/原位置等快照字段一律不动；调用方负责成环校验。返回是否命中。</summary>
    Task<bool> MoveFolderAsync(TrashFolderId id, TrashFolderId? parent, CancellationToken ct);
}

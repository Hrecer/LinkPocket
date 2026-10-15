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

    /// <summary>
    /// 全部书签快照（含归属单元），按删除时间倒序。
    /// 组合读取（trash.overview）用这一条替掉"每个单元查一次"的循环 —— 单元数一多就是 N+1 次查询。
    /// </summary>
    Task<IReadOnlyList<TrashedLink>> ListAllLinksAsync(CancellationToken ct);

    /// <summary>各回收站单元的书签快照计数。</summary>
    Task<IReadOnlyDictionary<TrashFolderId, int>> CountLinksByUnitAsync(CancellationToken ct);

    Task<TrashedLink> AddLinkAsync(TrashedLink snapshot, CancellationToken ct);
    Task RemoveLinkAsync(LinkId id, CancellationToken ct);

    /// <summary>
    /// **整目录批量入站**（删除文件夹的整树级联专用）：对每个 (目录, 原路径) 执行
    /// <c>INSERT INTO trash_links SELECT … FROM links WHERE folder_id=?</c> + <c>DELETE FROM links WHERE folder_id=?</c>
    /// ——set-based，一个目录两条语句。逐行 EF 登记在万行子树上实测 2s+（快照对象 + 变更跟踪 + 提交批），
    /// 本路径把"物理搬运"压到 SQL 一层（调用方仍负责 diff / 撤销载荷 / 事件，语义与逐行路径一致）。
    /// 返回入站的链接行数；同目录重复调用 = 第二次 0 行（幂等）。
    /// </summary>
    Task<int> TrashLinksOfFoldersAsync(IReadOnlyList<(string FolderId, string OriginPath)> folders,
        DateTime deletedAt, CancellationToken ct);

    // —— 文件夹单元 ——
    Task<TrashedFolder?> FindFolderAsync(TrashFolderId id, CancellationToken ct);

    /// <summary>全部单元（树装配在模块层组合），按删除时间倒序。</summary>
    Task<IReadOnlyList<TrashedFolder>> ListFoldersAsync(CancellationToken ct);

    /// <summary>被删文件夹总数（含子单元；SQL COUNT，diagnostics 只计数场景）。</summary>
    Task<int> CountFoldersAsync(CancellationToken ct);

    Task<TrashedFolder> AddFolderAsync(TrashedFolder unit, CancellationToken ct);
    Task RemoveFolderAsync(TrashFolderId id, CancellationToken ct);
}

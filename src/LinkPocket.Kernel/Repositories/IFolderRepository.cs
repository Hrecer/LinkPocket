using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>文件夹仓储契约（与链接仓储对称：Find/List/Add/Update/Remove/ChildrenOf）。</summary>
public interface IFolderRepository
{
    Task<Folder?> FindAsync(FolderId id, CancellationToken ct);

    /// <summary>全部文件夹（树装配/递归计数在 ITreeService 组合）。</summary>
    Task<IReadOnlyList<Folder>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// 目录名搜索（SQL 侧完成）：返回「名称命中」与「命中目录的**严格后代**」两组。
    /// </summary>
    /// <remarks>
    /// 为什么不用 <see cref="ListAllAsync"/> 再在内存里筛：十万目录时全量物化 ≈410ms，
    /// 而 SQL 只返回命中行（2 000 个名称命中 ≈20ms；5 万个后代 ≈100–200ms）。
    /// 位置范围 = 两组之和；文件夹结果行 = 名称命中（标题档）∪ 后代（位置档）。
    /// <paramref name="includeDescendants"/> = false 时只查名称命中（标题档不需要后代）。
    /// 匹配口径 = SQL `LIKE`（与链接搜索一致，ASCII 大小写不敏感；中文无大小写）。
    /// </remarks>
    Task<FolderNameSearch> SearchByNameAsync(string query, bool includeDescendants, CancellationToken ct);

    /// <summary>
    /// 按 ID 集合批量取（`id in (...)`，分块下推）：批量还原/批量操作的冲突预检用——
    /// 逐 ID <see cref="FindAsync"/> 在几百条时是线性点查（大条目撤销/还原的实测瓶颈之一）。
    /// 语义与 <see cref="FindAsync"/> 对齐：本单元已删的不返回、未提交新增的会返回。
    /// </summary>
    Task<IReadOnlyList<Folder>> FindManyAsync(IReadOnlyList<FolderId> ids, CancellationToken ct);

    /// <summary>文件夹总数（SQL COUNT；diagnostics 等只计数场景，绝不拉全量）。</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>某文件夹的直接子文件夹；parent = null 时返回根层（ParentId == NULL，根不是实体）。</summary>
    Task<IReadOnlyList<Folder>> ChildrenOfAsync(FolderId? parent, CancellationToken ct);

    Task<Folder> AddAsync(Folder folder, CancellationToken ct);
    Task UpdateAsync(Folder folder, CancellationToken ct);
    Task RemoveAsync(FolderId id, CancellationToken ct);

    /// <summary>批量删除**已读出的实体**（整子树删除；语义与理由同 <see cref="ILinkRepository.RemoveRangeAsync"/>）。</summary>
    Task RemoveRangeAsync(IReadOnlyList<Folder> folders, CancellationToken ct);
}

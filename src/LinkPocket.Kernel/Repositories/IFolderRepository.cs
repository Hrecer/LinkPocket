using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>文件夹仓储契约（与链接仓储对称：Find/List/Add/Update/Remove/ChildrenOf）。</summary>
public interface IFolderRepository
{
    Task<Folder?> FindAsync(FolderId id, CancellationToken ct);

    /// <summary>全部文件夹（树装配/递归计数在 ITreeService 组合）。</summary>
    Task<IReadOnlyList<Folder>> ListAllAsync(CancellationToken ct);

    /// <summary>文件夹总数（SQL COUNT；diagnostics 等只计数场景，绝不拉全量）。</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>某文件夹的直接子文件夹；parent = null 时返回根层（ParentId == NULL，根不是实体）。</summary>
    Task<IReadOnlyList<Folder>> ChildrenOfAsync(FolderId? parent, CancellationToken ct);

    Task<Folder> AddAsync(Folder folder, CancellationToken ct);
    Task UpdateAsync(Folder folder, CancellationToken ct);
    Task RemoveAsync(FolderId id, CancellationToken ct);
}

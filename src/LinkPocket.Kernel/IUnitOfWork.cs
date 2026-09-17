namespace LinkPocket.Kernel;

/// <summary>显式事务作用域（批/导入/干跑用）：Commit 提交、Dispose 未提交即回滚。</summary>
public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>
/// 工作单元（方案 4.1）：每个调用一个短生命周期工作单元（不共享可变长命上下文），
/// CommitAsync = 单次 SaveChanges；BeginTransaction 供批/导入/干跑使用。
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    ILinkRepository Links { get; }
    IFolderRepository Folders { get; }
    ITrashRepository Trash { get; }

    /// <summary>树领域服务（父链遍历/递归计数/环检测/路径显示——唯一出处，方案 4.1）。</summary>
    ITreeService Trees { get; }

    Task CommitAsync(CancellationToken ct);

    ITransactionScope BeginTransaction();
}

namespace LinkPocket.Kernel;

/// <summary>显式事务作用域（批/导入/干跑用）：Commit 提交、Dispose 未提交即回滚。</summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>
    /// 立刻开启底层事务（而非等到 Commit/Rollback 才开）。
    /// 干跑必须先调用：批量语句（<c>ExecuteDelete</c> 等绕过变更跟踪的操作）只在本连接已有事务时才被回滚，
    /// 否则会立刻落库、绕开"执行但不提交"的干跑语义。
    /// </summary>
    Task BeginAsync(CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>
/// 工作单元：每个调用一个短生命周期工作单元（不共享可变长命上下文），
/// CommitAsync = 单次 SaveChanges；BeginTransaction 供批/导入/干跑使用。
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    ILinkRepository Links { get; }
    IFolderRepository Folders { get; }
    ITrashRepository Trash { get; }

    /// <summary>审计存储（读侧 + 保留策略；写入由引擎管道的 IAuditWriter 承担）。</summary>
    IAuditRepository Audit { get; }

    /// <summary>树领域服务（父链遍历/递归计数/环检测/路径显示——唯一出处）。</summary>
    ITreeService Trees { get; }

    /// <summary>
    /// 命名领域服务（文件夹同层唯一命名的**唯一**入口：单条查库解析 + 批量占用表——唯一出处）。
    /// 与 <see cref="Trees"/> 同一种接线：服务绑当前工作单元，同一事务内可见未提交变更，
    /// 因此不能被抓成长命单例注入模块。
    /// </summary>
    IFolderNaming Naming { get; }

    Task CommitAsync(CancellationToken ct);

    ITransactionScope BeginTransaction();

    /// <summary>
    /// 清空全部业务数据（链接 / 文件夹 / 回收站两表）——**单语句批量删除**，供整库重置使用。
    /// 10k 库下逐条 DELETE 是一万次往返；这里是常量条 SQL。
    /// folder_id 外键为 ON DELETE SET NULL、folders.parent_id 是自引用 RESTRICT，
    /// 故删除顺序与去跟踪细节由实现保证；本方法只登记/执行删除，**不提交**。
    /// </summary>
    Task ClearAllDataAsync(CancellationToken ct);

    /// <summary>
    /// 当前库 schema 版本（schema_migrations 表 MAX(version)；
    /// v2 全新建库起步，仅服务 v2 内部常规演进）。维护/诊断命令消费。
    /// </summary>
    Task<int> SchemaVersionAsync(CancellationToken ct);
}

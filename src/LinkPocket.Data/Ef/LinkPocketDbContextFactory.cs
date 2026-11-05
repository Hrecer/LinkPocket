using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>
/// 引擎侧 DbContext 工厂（读池）：每次 CreateDbContext 产出短生命周期上下文，
/// 连接串复用；建工厂时对库文件一次性启用 WAL（journal_mode 为库级持久属性），
/// 每个连接经连接串启用 foreign_keys。WAL = SQLite 官方读写并发方案，
/// 导入大事务期间读者见旧快照——与现状"导入完成后才看到结果"的感知一致（行为等价）。
/// </summary>
public sealed class LinkPocketDbContextFactory : IDbContextFactory<LinkPocketDbContext>
{
    /// <summary>
    /// 跨进程写争用的**忙等上限（秒）**：SQLite 是单写者——外部进程（CLI / 外部 Agent 经 MCP）
    /// 提交期间，本进程的写会拿到 <c>SQLITE_BUSY</c>。显式忙等 5s 重试，别依赖隐式默认
    /// （否则表现为"对方提交的那一瞬间，界面/命令行偶发报锁"）。超过上限就如实失败，不静默兜底。
    /// </summary>
    public const int BusyTimeoutSeconds = 5;

    private readonly string _connectionString;

    /// <summary>本工厂服务的库文件绝对路径（唯一来源；消费方按它推导同目录附属物，如 macros.json）。</summary>
    public string DatabasePath { get; }

    public LinkPocketDbContextFactory(string dbPath)
    {
        DatabasePath = Path.GetFullPath(dbPath);
        var builder = new SqliteConnectionStringBuilder($"Data Source={DatabasePath}")
        {
            ForeignKeys = true,
            DefaultTimeout = BusyTimeoutSeconds,
        };
        _connectionString = builder.ToString();
        EnableWal(DatabasePath);
        // 缺省库 = 空文件 → 建当前纪元基线；异纪元 / 外来库 → 明确拒绝（DatabaseIncompatibleException）
        SchemaMigrator.EnsureSchema(DatabasePath);
    }

    public LinkPocketDbContext CreateDbContext()
        => new(contextPath: null, connectionStringOverride: _connectionString);

    /// <summary>
    /// 在真实事务里走一遍文件夹写路径（命名查询、插入、父链触达），随后整体回滚。
    /// 只用于启动空闲预热：EF 模型与查询编译是进程级一次性成本，先在这里付掉，用户第一次新建文件夹不再卡顿。
    /// 不留任何行、事件或审计；失败由调用方记录，不重试。
    /// </summary>
    public async Task WarmWritePathAsync(CancellationToken ct = default)
    {
        using var db = CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var uow = new EfUnitOfWork(db);
        var parent = await db.Folders.AsNoTracking()
            .Select(f => f.FolderId)
            .FirstOrDefaultAsync(ct);
        var desired = $"__linkpocket_warmup_{Guid.NewGuid():N}";
        var resolved = await uow.Naming.ResolveAsync(parent, desired, null, ct);
        var folder = new Folder
        {
            Name = resolved,
            ParentId = parent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await uow.Folders.AddAsync(folder, ct);
        await uow.Trees.TouchModifiedAsync(parent is null ? null : new FolderId(parent), ct);
        try
        {
            await uow.CommitAsync(ct);
        }
        finally
        {
            // 回滚不接受取消：提交完成后即使窗口正在关闭，也必须把预热写入丢掉。
            await tx.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>一次性启用 WAL；已处于 WAL 时为幂等 no-op。</summary>
    private static void EnableWal(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder($"Data Source={dbPath}").ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
    }
}

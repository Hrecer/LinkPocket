using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>
/// 工作单元 EF 实现（方案 4.1）：短生命周期（每调用一个），
/// CommitAsync = 单次 SaveChanges；仓储经属性惰性创建。经工厂/组合根暴露为 IUnitOfWork。
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly LinkPocketDbContext _db;
    private EfLinkRepository? _links;
    private EfFolderRepository? _folders;
    private EfTrashRepository? _trash;
    private EfTreeService? _trees;

    public EfUnitOfWork(LinkPocketDbContext db) => _db = db;

    public ILinkRepository Links => _links ??= new EfLinkRepository(_db);
    public IFolderRepository Folders => _folders ??= new EfFolderRepository(_db);
    public ITrashRepository Trash => _trash ??= new EfTrashRepository(_db);
    public ITreeService Trees => _trees ??= new EfTreeService(_db);

    public Task CommitAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task ClearAllDataAsync(CancellationToken ct)
    {
        // defer_foreign_keys 是事务级 PRAGMA（在提交点校验）：大多数调用（非干跑 reinit / backup replace）
        // 没有外层事务，裸执行会被自动提交立即重置 → 自引用 RESTRICT 的整表 DELETE 必然失败。
        // 本方法保证「要么复用外层事务、要么自建事务」再执行清空（干跑时外层事务回滚即整体丢弃）。
        var tx = _db.Database.CurrentTransaction;
        var ownsTx = tx is null;
        if (ownsTx) tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // 回收站两表之间没有 FK 约束（schema 未声明外键；trash_links.trash_folder_id 只是逻辑关联）→
            // 直接批量删除（绕过变更跟踪）。注意：未来若补 FK 约束，必须先删 trash_links 再删 trash_folders。
            await _db.TrashedLinks.ExecuteDeleteAsync(ct);
            await _db.TrashedFolders.ExecuteDeleteAsync(ct);

            // links.folder_id → folders(id) ON DELETE SET NULL：先删链接，避免 SET NULL 的额外写放大
            await _db.Links.ExecuteDeleteAsync(ct);

            // folders 是自引用外键（ON DELETE RESTRICT），RESTRICT 按行即时校验 → 整表 DELETE 必然违规；
            // 事务内开启 defer_foreign_keys 把校验推迟到提交点（那时表已空，必然无违规）。
            await _db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);
            await _db.Folders.ExecuteDeleteAsync(ct);

            if (ownsTx) await tx!.CommitAsync(ct);
        }
        catch
        {
            if (ownsTx && tx != null)
            {
                try
                {
                    await tx.RollbackAsync(ct);
                }
                catch (Exception rollbackEx)
                {
                    // 回滚失败绝不能覆盖原始异常（审核 1.2）：保留「哪一步 DELETE 失败」的根因
                    System.Diagnostics.Trace.TraceWarning("清空数据回滚失败：{0}", rollbackEx.Message);
                }
            }
            throw;
        }

        // 自管事务场景（ownedTx）：清空是整库级物理操作——池化连接仍持有 WAL 文件句柄，
        // 显式断开 + 截断 checkpoint，避免 backup.export / maintenance.reinit 的文件操作被占住、
        // WAL 持续膨胀（审核 3.4/4.7）。外层事务（干跑）场景跳过：未提交的回滚本就把体积还原。
        if (ownsTx)
        {
            var connectionString = _db.Database.GetDbConnection().ConnectionString;
            SqliteConnection.ClearAllPools();
            await using var checkpointConn = new SqliteConnection(connectionString);
            await checkpointConn.OpenAsync(ct);
            await using var checkpoint = checkpointConn.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> SchemaVersionAsync(CancellationToken ct)
    {
        // schema_migrations 由 SchemaMigrator 建库时写入（v2 起步）；标量查询列名须为 Value
        var row = await _db.Database
            .SqlQuery<int?>($"SELECT COALESCE(MAX(version), 0) AS Value FROM schema_migrations")
            .ToListAsync(ct);
        return row.FirstOrDefault() ?? 0;
    }

    public ITransactionScope BeginTransaction()
        => new EfTransactionScope(new Lazy<Task<IDbContextTransaction>>(() => _db.Database.BeginTransactionAsync()));

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

/// <summary>显式事务作用域：Commit 提交；Dispose 时未提交/未回滚即回滚（干跑的丢弃也走这里）。</summary>
internal sealed class EfTransactionScope(Lazy<Task<IDbContextTransaction>> tx) : ITransactionScope
{
    private IDbContextTransaction? _tx;
    private bool _completed;

    private async Task<IDbContextTransaction> GetAsync() => _tx ??= await tx.Value;

    public Task BeginAsync(CancellationToken ct) => GetAsync();

    public async Task CommitAsync(CancellationToken ct)
    {
        var t = await GetAsync();
        await t.CommitAsync(ct);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        var t = await GetAsync();
        await t.RollbackAsync(ct);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                var t = await GetAsync();
                await t.RollbackAsync();
            }
            catch
            {
                // 丢弃阶段连接可能已不可用——回滚尽力而为
            }
        }

        if (_tx != null) await _tx.DisposeAsync();
    }
}

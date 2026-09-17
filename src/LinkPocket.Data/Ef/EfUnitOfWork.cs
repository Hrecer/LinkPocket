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
        // 回收站两表无外键依赖 → 直接批量删除（绕过变更跟踪；干跑时由外层事务回滚）
        await _db.TrashedLinks.ExecuteDeleteAsync(ct);
        await _db.TrashedFolders.ExecuteDeleteAsync(ct);

        // links.folder_id → folders(id) ON DELETE SET NULL：先删链接，避免 SET NULL 的额外写放大
        await _db.Links.ExecuteDeleteAsync(ct);

        // folders 是自引用外键（ON DELETE RESTRICT），RESTRICT 按行即时校验 → 整表 DELETE 必然违规；
        // 事务内开启 defer_foreign_keys 把校验推迟到提交点（那时表已空，必然无违规）。
        // 本方法在干跑下已被外层显式事务包住（EngineCore 干跑先 BeginAsync），故此处的提交点是外层事务。
        await _db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);
        await _db.Folders.ExecuteDeleteAsync(ct);
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

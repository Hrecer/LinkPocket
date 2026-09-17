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

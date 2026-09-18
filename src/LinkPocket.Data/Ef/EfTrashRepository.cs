using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>回收站两表仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfTrashRepository(LinkPocketDbContext db) : ITrashRepository
{
    public async Task<TrashedLink?> FindLinkAsync(LinkId id, CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking().FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);

    public async Task<IReadOnlyList<TrashedLink>> ListStandaloneLinksAsync(CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .Where(l => l.TrashFolderId == null)
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(ct);

    public Task<int> CountStandaloneLinksAsync(CancellationToken ct)
        => db.TrashedLinks.AsNoTracking().CountAsync(l => l.TrashFolderId == null, ct);

    public async Task<IReadOnlyList<TrashedLink>> ListLinksByUnitAsync(TrashFolderId unit, CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .Where(l => l.TrashFolderId == unit.Value)
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<TrashFolderId, int>> CountLinksByUnitAsync(CancellationToken ct)
        => (await db.TrashedLinks.AsNoTracking()
                .Where(l => l.TrashFolderId != null)
                .GroupBy(l => l.TrashFolderId!)
                .Select(g => new { UnitId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => new TrashFolderId(x.UnitId), x => x.Count);

    public Task<TrashedLink> AddLinkAsync(TrashedLink snapshot, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync
        db.TrashedLinks.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public async Task RemoveLinkAsync(LinkId id, CancellationToken ct)
    {
        var snapshot = await db.TrashedLinks.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (snapshot != null) db.TrashedLinks.Remove(snapshot);
    }

    public async Task<TrashedFolder?> FindFolderAsync(TrashFolderId id, CancellationToken ct)
        => await db.TrashedFolders.AsNoTracking().FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct);

    public async Task<IReadOnlyList<TrashedFolder>> ListFoldersAsync(CancellationToken ct)
        => await db.TrashedFolders.AsNoTracking().OrderByDescending(f => f.DeletedAt).ToListAsync(ct);

    public Task<int> CountFoldersAsync(CancellationToken ct)
        => db.TrashedFolders.AsNoTracking().CountAsync(ct);

    public Task<TrashedFolder> AddFolderAsync(TrashedFolder unit, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync
        db.TrashedFolders.Add(unit);
        return Task.FromResult(unit);
    }

    public async Task RemoveFolderAsync(TrashFolderId id, CancellationToken ct)
    {
        var unit = await db.TrashedFolders.FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct);
        if (unit != null) db.TrashedFolders.Remove(unit);
    }
}

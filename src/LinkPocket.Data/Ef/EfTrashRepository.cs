using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>回收站两表仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfTrashRepository(LinkPocketDbContext db) : ITrashRepository
{
    // —— 按 ID 点查 = 读己之写（同事务合并 change tracker，ENGINE-API §5 / TRASH §4.4）——
    // 回收站的读全是 AsNoTracking（结果不进跟踪器），所以**先看 Local**才能表达
    // "本单元待删 → 不可见 / 本单元待增 → 可见"；Local 未命中再落 SQL（Local 只含本上下文
    // 显式 Add/Remove 过的实例，集合恒小）。列表查询不合并（仍只见提交边界一致快照）。

    /// <summary>按 ID 点查快照：本单元已 Remove → null；本单元已 Add（未落库）→ 可见。</summary>
    public async Task<TrashedLink?> FindLinkAsync(LinkId id, CancellationToken ct)
    {
        var pending = db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == id.Value);
        if (pending is not null)
            return db.Entry(pending).State == EntityState.Deleted ? null : pending;
        return await db.TrashedLinks.AsNoTracking().FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
    }

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

    public async Task<IReadOnlyList<TrashedLink>> ListAllLinksAsync(CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
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
        // 只登记不保存：提交权归工作单元 CommitAsync。
        // 同批"先还原（Remove 本快照）、后又移入回收站（同 ID 重建）"：跟踪器里同 key 待删实例 =
        // 行仍在库 → 新实例接管（待删取消 + 按 UPDATE 提交；直接 Add 会撞 EF 身份冲突）。
        var tracked = db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == snapshot.LinkId);
        if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
        {
            db.Entry(tracked).State = EntityState.Detached;
            db.TrashedLinks.Attach(snapshot);
            db.Entry(snapshot).State = EntityState.Modified;
            return Task.FromResult(snapshot);
        }
        db.TrashedLinks.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public async Task RemoveLinkAsync(LinkId id, CancellationToken ct)
    {
        // SQL 优先（身份解析复用跟踪实例；已删再 Remove = no-op）；SQL 未命中 →
        // 本单元未提交的新增快照（Remove 在 Added 实体上 = 取消暂存：同批"入站又出站"净效果为零）。
        var snapshot = await db.TrashedLinks.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct)
                       ?? db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == id.Value);
        if (snapshot != null) db.TrashedLinks.Remove(snapshot);
    }

    /// <summary>按 ID 点查单元：本单元已 Remove → null；本单元已 Add（未落库）→ 可见。</summary>
    public async Task<TrashedFolder?> FindFolderAsync(TrashFolderId id, CancellationToken ct)
    {
        var pending = db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == id.Value);
        if (pending is not null)
            return db.Entry(pending).State == EntityState.Deleted ? null : pending;
        return await db.TrashedFolders.AsNoTracking().FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct);
    }

    public async Task<IReadOnlyList<TrashedFolder>> ListFoldersAsync(CancellationToken ct)
        => await db.TrashedFolders.AsNoTracking().OrderByDescending(f => f.DeletedAt).ToListAsync(ct);

    public Task<int> CountFoldersAsync(CancellationToken ct)
        => db.TrashedFolders.AsNoTracking().CountAsync(ct);

    public Task<TrashedFolder> AddFolderAsync(TrashedFolder unit, CancellationToken ct)
    {
        // 只登记不保存：同 key 待删实例由新实例接管（语义同 AddLinkAsync）。
        var tracked = db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == unit.TrashFolderId);
        if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
        {
            db.Entry(tracked).State = EntityState.Detached;
            db.TrashedFolders.Attach(unit);
            db.Entry(unit).State = EntityState.Modified;
            return Task.FromResult(unit);
        }
        db.TrashedFolders.Add(unit);
        return Task.FromResult(unit);
    }

    public async Task RemoveFolderAsync(TrashFolderId id, CancellationToken ct)
    {
        // SQL 优先 + 未提交新增回退（语义同 RemoveLinkAsync）。
        var unit = await db.TrashedFolders.FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct)
                   ?? db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == id.Value);
        if (unit != null) db.TrashedFolders.Remove(unit);
    }
}

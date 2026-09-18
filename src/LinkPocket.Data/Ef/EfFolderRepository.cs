using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>文件夹仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfFolderRepository(LinkPocketDbContext db) : IFolderRepository
{
    public async Task<Folder?> FindAsync(FolderId id, CancellationToken ct)
        => await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);

    public async Task<IReadOnlyList<Folder>> ListAllAsync(CancellationToken ct)
        => await db.Folders.AsNoTracking().ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct)
        => db.Folders.AsNoTracking().CountAsync(ct);

    public async Task<IReadOnlyList<Folder>> ChildrenOfAsync(FolderId? parent, CancellationToken ct)
    {
        var query = db.Folders.AsNoTracking();
        return parent is { } p
            ? await query.Where(f => f.ParentId == p.Value).ToListAsync(ct)
            : await query.Where(f => f.ParentId == null).ToListAsync(ct);
    }

    public Task<Folder> AddAsync(Folder folder, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）
        db.Folders.Add(folder);
        return Task.FromResult(folder);
    }

    public Task UpdateAsync(Folder folder, CancellationToken ct)
    {
        db.Folders.Update(folder);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(FolderId id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);
        if (folder != null) db.Folders.Remove(folder);
    }
}

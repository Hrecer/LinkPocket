using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>文件夹仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfFolderRepository(LinkPocketDbContext db) : IFolderRepository
{
    /// <summary>
    /// 本上下文是否走过 SQL 命中的 Remove（语义同 <c>EfLinkRepository._removed</c>，见 WARNINGS 135）：
    /// 只有走过删除才可能存在同 key 待删实例，Add 的接管扫描才需要打开；正常新增路径 O(1)。
    /// </summary>
    private bool _removed;

    /// <summary>
    /// 按 ID 点查 = **读己之写**（同事务合并 change tracker，ENGINE-API §5 / TRASH §4.4）：
    /// ① SQL 命中且跟踪态为 Deleted → null（本单元已删不可见）；② SQL 未命中 → 只认跟踪器里
    /// **Added**（本单元未提交的新增，SQL 看不到）；其余状态（行已越轨消失的幽灵）如实 null。
    /// SQL 优先 = 命中走 PK 索引 + 身份解析 O(1)，Local 扫描只发生在 SQL 未命中时；
    /// 列表/范围查询与读池**不合并**（仍只见提交边界一致快照）——批内集合级判定由各流水线自记账。
    /// </summary>
    public async Task<Folder?> FindAsync(FolderId id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);
        if (folder is not null)
            return db.Entry(folder).State == EntityState.Deleted ? null : folder;   // 身份解析复用跟踪实例 → 状态判定 O(1)
        var pending = db.Folders.Local.FirstOrDefault(f => f.FolderId == id.Value);  // SQL 未命中 → 只可能是未提交的新增
        return pending is not null && db.Entry(pending).State == EntityState.Added ? pending : null;
    }

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
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）。
        // 同批"先软删、后按原 ID 还原/重建"：跟踪器里同 key 的待删实例 = 行仍在库 → 交给新实例接管
        //（待删取消 + 新值按 UPDATE 提交；直接 Add 会撞 EF 身份冲突"already being tracked"）。
        // 接管扫描只在本上下文走过 Remove 后才做（_removed）：每加必扫 = O(n²)（见 WARNINGS 135）。
        if (_removed)
        {
            var tracked = db.Folders.Local.FirstOrDefault(f => f.FolderId == folder.FolderId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.Folders.Attach(folder);
                db.Entry(folder).State = EntityState.Modified;
                return Task.FromResult(folder);
            }
        }
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
        // SQL 优先（身份解析复用跟踪实例；已删实例再 Remove = no-op）；
        // SQL 未命中且跟踪器里是 Added → Remove = 取消暂存（同批"建了又删"净效果为零）；
        // 其余状态不动（行已越轨消失时不制造 DELETE 0 行的并发异常）。
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);
        if (folder is null)
        {
            var pending = db.Folders.Local.FirstOrDefault(f => f.FolderId == id.Value);
            if (pending is not null && db.Entry(pending).State == EntityState.Added) db.Folders.Remove(pending);
            return;
        }
        db.Folders.Remove(folder);
        _removed = true;   // 出现同 key 待删实例 → 后续 Add 才需要接管扫描（AddAsync）
    }
}

using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>链接仓储 EF 实现（internal——黑盒封装，仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfLinkRepository(LinkPocketDbContext db) : ILinkRepository
{
    public async Task<Link?> FindAsync(LinkId id, CancellationToken ct)
        => await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);

    public async Task<IReadOnlyList<Link>> ListAsync(LinkQuerySpec spec, CancellationToken ct)
    {
        IQueryable<Link> q = db.Links.AsNoTracking();
        q = ApplyFilter(q, spec.Filter);

        // 排序经 ISortEngine 白名单下推；缺省 = 名称升序 + ID 兜底
        q = EfSortEngine.Instance.Apply(q, spec.Sort, EfSortEngine.LinkFields);
        if (spec.Sort.Count == 0)
            q = ((IOrderedQueryable<Link>)q).ThenBy(l => l.LinkId);

        if (spec.Page.Size > 0)
            q = q.Skip(spec.Page.Skip).Take(spec.Page.Size);

        return await q.ToListAsync(ct);
    }

    public async Task<int> CountAsync(LinkFilter filter, CancellationToken ct)
        => await ApplyFilter(db.Links.AsNoTracking(), filter).CountAsync(ct);

    public async Task<IReadOnlyDictionary<FolderId, int>> CountByFolderAsync(CancellationToken ct)
        => (await db.Links.AsNoTracking()
                .Where(l => l.ListId != null)
                .GroupBy(l => l.ListId!)
                .Select(g => new { FolderId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => new FolderId(x.FolderId), x => x.Count);

    public Task<Link> AddAsync(Link link, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）
        db.Links.Add(link);
        return Task.FromResult(link);
    }

    public Task UpdateAsync(Link link, CancellationToken ct)
    {
        db.Links.Update(link);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(LinkId id, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (link != null) db.Links.Remove(link);
    }

    public async Task<IReadOnlyList<Link>> FindByUrlAsync(string url, CancellationToken ct)
        => await db.Links.AsNoTracking().Where(l => l.Url == url).ToListAsync(ct);

    private static IQueryable<Link> ApplyFilter(IQueryable<Link> q, LinkFilter f)
    {
        if (!string.IsNullOrEmpty(f.Search))
        {
            var like = $"%{f.Search}%";
            q = q.Where(l => EF.Functions.Like(l.Title, like)
                          || EF.Functions.Like(l.Url, like)
                          || (l.Description != null && EF.Functions.Like(l.Description, like)));
        }
        if (f.FolderId is { } folder) q = q.Where(l => l.ListId == folder.Value);
        if (f.IsImportant is { } imp) q = q.Where(l => l.IsImportant == imp);
        if (f.CreatedFrom is { } from) q = q.Where(l => l.CreatedAt >= from);
        if (f.CreatedTo is { } to) q = q.Where(l => l.CreatedAt <= to);
        return q;
    }
}

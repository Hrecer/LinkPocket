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
        // ID 兜底恒追加：任意非唯一排序字段下 Skip/Take 分页仍稳定（翻页不重不漏）
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

    public async Task<IReadOnlyList<Link>> ListDuplicatedAsync(CancellationToken ct)
        // 相关子查询（EXISTS 语义）= "同址还有别的链接"；比先查重复 URL 再 IN 更稳：
        // IN 列表在超大库上会撞参数个数上限。返回顺序与去重页既有口径一致（URL → 创建时间 → ID）。
        => await db.Links.AsNoTracking()
            .Where(l => db.Links.Any(other => other.Url == l.Url && other.LinkId != l.LinkId))
            .OrderBy(l => l.Url).ThenBy(l => l.CreatedAt).ThenBy(l => l.LinkId)
            .ToListAsync(ct);

    private static IQueryable<Link> ApplyFilter(IQueryable<Link> q, LinkFilter f)
    {
        if (!string.IsNullOrEmpty(f.Search))
        {
            var like = $"%{EscapeLike(f.Search)}%";
            q = q.Where(l => EF.Functions.Like(l.Title, like, LikeEscape)
                          || EF.Functions.Like(l.Url, like, LikeEscape)
                          || (l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape)));
        }
        if (f.SearchScope is { } scope) q = ApplySearchScope(q, scope);
        if (f.FolderId is { } folder) q = q.Where(l => l.ListId == folder.Value);
        if (f.IsImportant is { } imp) q = q.Where(l => l.IsImportant == imp);
        if (f.CreatedFrom is { } from) q = q.Where(l => l.CreatedAt >= from);
        if (f.CreatedTo is { } to) q = q.Where(l => l.CreatedAt <= to);

        // —— links.query 结构化字段（每条独立下推；字段互斥由模块层保证）——
        if (f.Unfiled is { } unfiled) q = unfiled ? q.Where(l => l.ListId == null) : q.Where(l => l.ListId != null);
        if (!string.IsNullOrEmpty(f.TitleContains))
        {
            var like = $"%{EscapeLike(f.TitleContains)}%";
            q = q.Where(l => l.Title != null && EF.Functions.Like(l.Title, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.UrlContains))
        {
            var like = $"%{EscapeLike(f.UrlContains)}%";
            q = q.Where(l => EF.Functions.Like(l.Url, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.UrlStarts))
        {
            var like = $"{EscapeLike(f.UrlStarts)}%";
            q = q.Where(l => EF.Functions.Like(l.Url, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.DescriptionContains))
        {
            var like = $"%{EscapeLike(f.DescriptionContains)}%";
            q = q.Where(l => l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape));
        }
        if (f.UpdatedFrom is { } uFrom) q = q.Where(l => l.UpdatedAt >= uFrom);
        if (f.UpdatedTo is { } uTo) q = q.Where(l => l.UpdatedAt <= uTo);
        if (f.LastVisitedFrom is { } lvFrom) q = q.Where(l => l.LastVisitedAt >= lvFrom);
        if (f.LastVisitedTo is { } lvTo) q = q.Where(l => l.LastVisitedAt <= lvTo);
        if (f.NeverVisited is { } never) q = never ? q.Where(l => l.LastVisitedAt == null) : q.Where(l => l.LastVisitedAt != null);
        if (f.VisitCountMin is { } vcMin) q = q.Where(l => l.VisitCount >= vcMin);
        if (f.VisitCountMax is { } vcMax) q = q.Where(l => l.VisitCount <= vcMax);
        return q;
    }

    /// <summary>
    /// search.links 多范围搜索的 SQL 下推：三字段 OR 包含 ∪ 目录集合，两段之间亦为 OR。
    /// 中缀 LIKE 注定全表扫描（已知且接受，见 IndexPlanTests 负向断言）——但过滤发生在 SQL 端，
    /// 不再把全库读进内存逐条比对。
    /// </summary>
    private static IQueryable<Link> ApplySearchScope(IQueryable<Link> q, LinkSearchScope scope)
    {
        var like = $"%{EscapeLike(scope.Query)}%";
        var folderIds = scope.Folders.Select(x => x.Value).ToArray();
        var hasFolderRange = folderIds.Length > 0;

        if (!scope.Title && !scope.Url && !scope.Description && !hasFolderRange)
            return q.Where(_ => false);   // 全范围未启用 = 无命中

        return q.Where(l =>
            (scope.Title && EF.Functions.Like(l.Title, like, LikeEscape))
            || (scope.Url && EF.Functions.Like(l.Url, like, LikeEscape))
            || (scope.Description && l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape))
            || (hasFolderRange && l.ListId != null && folderIds.Contains(l.ListId)));
    }

    private const string LikeEscape = "\\";

    /// <summary>LIKE 通配符转义：关键词里的 % / _ / \ 按字面匹配，不做通配（用户输入不得改变匹配语义）。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

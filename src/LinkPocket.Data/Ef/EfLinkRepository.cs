using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>链接仓储 EF 实现（internal——黑盒封装，仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfLinkRepository(LinkPocketDbContext db) : ILinkRepository
{
    /// <summary>
    /// 本上下文是否走过 SQL 命中的 Remove（走过才可能存在同 key 的**待删**实例，Add 才需要接管扫描）。
    /// 无条件扫描 = 每次 Add 都 O(本上下文实体数) → 10k 导入 O(n²)（实测 34s 顶穿 5s 门槛，见 WARNINGS 135）；
    /// 正常新增热路径必须保持 O(1)。恢复/还原本就先走 Remove → 扫描照做，语义不变。
    /// </summary>
    private bool _removed;

    /// <summary>
    /// 按 ID 点查 = **读己之写**（语义同 <c>EfFolderRepository.FindAsync</c>，ENGINE-API §5）：
    /// SQL 命中且跟踪态 Deleted → null；SQL 未命中只认跟踪器 Added（未提交的新增）。
    /// </summary>
    public async Task<Link?> FindAsync(LinkId id, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (link is not null)
            return db.Entry(link).State == EntityState.Deleted ? null : link;
        var pending = db.Links.Local.FirstOrDefault(l => l.LinkId == id.Value);
        return pending is not null && db.Entry(pending).State == EntityState.Added ? pending : null;
    }

    /// <summary>过滤 + 排序（含 ID 兜底）+ 分页 的统一构建（ListAsync / ListDtosAsync 共用，规则只此一份）。</summary>
    private IQueryable<Link> BuildPageQuery(LinkQuerySpec spec)
    {
        IQueryable<Link> q = db.Links.AsNoTracking();
        q = ApplyFilter(q, spec.Filter);

        // 排序经 ISortEngine 白名单下推；缺省 = 名称升序 + ID 兜底
        q = EfSortEngine.Instance.Apply(q, spec.Sort, EfSortEngine.LinkFields);
        // ID 兜底恒追加、**方向随最后一条排序**：任意非唯一排序字段下 Skip/Take 分页仍稳定（翻页不重不漏），
        // 同时让 `(folder_id, 列, id)` 复合索引**倒序扫**也整段命中排序——
        // 混合方向（列 DESC, id ASC）没有索引可依，会退化成"取回整个目录再排序"（100K 实测 43ms/页）。
        var lastDir = spec.Sort.Count > 0 ? spec.Sort[^1].Dir : SortDir.Asc;
        q = lastDir == SortDir.Desc
            ? ((IOrderedQueryable<Link>)q).ThenByDescending(l => l.LinkId)
            : ((IOrderedQueryable<Link>)q).ThenBy(l => l.LinkId);

        if (spec.Page.Size > 0)
            q = q.Skip(spec.Page.Skip).Take(spec.Page.Size);
        return q;
    }

    /// <summary>分页**直出 DTO**（服务端投影）：与 <c>ToDto()</c> 逐字段同口径，但不物化实体——
    /// 目录页每帧 2000 行的取数形态；实体物化 + 再映射那份开销在这里省掉（100K 实测省 ~35ms/页）。</summary>
    public async Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDtosAsync(LinkQuerySpec spec, CancellationToken ct)
        => await BuildPageQuery(spec)
            .Select(l => new LinkPocket.Contracts.LinkDto
            {
                LinkId = l.LinkId,
                Url = l.Url,
                Title = l.Title ?? string.Empty,
                Description = l.Description ?? string.Empty,
                FaviconUrl = l.FaviconUrl ?? string.Empty,
                ListId = l.ListId,
                LastVisitedAt = l.LastVisitedAt,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt,
            })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListFaviconUrlsAsync(CancellationToken ct)
        => await db.Links.AsNoTracking()
            .Where(l => l.FaviconUrl != null && l.FaviconUrl != "")
            .Select(l => l.FaviconUrl!)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Link>> ListAsync(LinkQuerySpec spec, CancellationToken ct)
    {
        var q = BuildPageQuery(spec);

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
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）。
        // 同批"先移入回收站、后按原 ID 还原/重建"：跟踪器里同 key 的待删实例 = 行仍在库 →
        // 交给新实例接管（待删取消 + 新值按 UPDATE 提交；直接 Add 会撞 EF 身份冲突）。
        // 接管扫描只在本上下文走过 Remove 后才做（_removed）：同批删除→重建的场景才需要它，
        // 而每加必扫会把 O(1) 的新增变成 O(n²)（10k 导入实测 34s，见 _removed 注释）。
        if (_removed)
        {
            var tracked = db.Links.Local.FirstOrDefault(l => l.LinkId == link.LinkId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.Links.Attach(link);
                db.Entry(link).State = EntityState.Modified;
                return Task.FromResult(link);
            }
        }
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
        // 语义同 EfFolderRepository.RemoveAsync：SQL 优先；未命中且跟踪器 Added = 取消暂存。
        var link = await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (link is null)
        {
            var pending = db.Links.Local.FirstOrDefault(l => l.LinkId == id.Value);
            if (pending is not null && db.Entry(pending).State == EntityState.Added) db.Links.Remove(pending);
            return;
        }
        db.Links.Remove(link);
        _removed = true;   // 出现同 key 待删实例 → 后续 Add 才需要接管扫描（AddAsync）
    }

    public Task RemoveRangeAsync(IReadOnlyList<Link> links, CancellationToken ct)
    {
        // 已读出的实体按实体登记删除（Entry() 走身份映射，O(1)/行；不发 SELECT）——
        // 逐条 RemoveAsync 会为每行发一条点查（万行子树 = 万条 SELECT + 万次身份解析），
        // 那是"删一个大文件夹"实测卡顿的主因之一（子树级联统一走本方法）。
        // ⚠️ 同 EfFolderRepository.RemoveRangeAsync：批/宏内同一实体可能已被跟踪（先移动再删除），
        // 必须拿被跟踪的实例登记删除；跟踪表惰性建，分离热路径零额外开销。
        Dictionary<string, Link>? tracked = null;
        foreach (var link in links)
        {
            if (db.Entry(link).State != EntityState.Detached)
            {
                db.Entry(link).State = EntityState.Deleted;
                continue;
            }
            tracked ??= db.ChangeTracker.Entries<Link>()
                .ToDictionary(e => e.Entity.LinkId, e => e.Entity, StringComparer.Ordinal);
            if (tracked.TryGetValue(link.LinkId, out var existing))
                db.Entry(existing).State = EntityState.Deleted;  // 同 key 已跟踪（含已删除 → 空操作）
            else
                db.Entry(link).State = EntityState.Deleted;
        }
        if (links.Count > 0) _removed = true;   // 接管扫描的开启条件与 RemoveAsync 一致（见该字段说明）
        return Task.CompletedTask;
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
        if (f.IdIn is { } ids) q = q.Where(l => ids.Contains(l.LinkId));
        if (f.FolderIdsIn is { } folderIds) q = q.Where(l => l.ListId != null && folderIds.Contains(l.ListId));
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

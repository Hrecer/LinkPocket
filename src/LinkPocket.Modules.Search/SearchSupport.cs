using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Search;

/// <summary>搜索域内部支撑：多范围谓词（SQL 下推）与命中字段标注。</summary>
internal static class SearchSupport
{
    /// <summary>快照会话（界面内部）一页的缺省大小；外部命令默认全量，不使用这个值。</summary>
    public const int DefaultPageSize = 500;

    /// <summary>
    /// 快照会话分页归一：0/负数 = 引擎单页上限（会话每页必须有界）。
    /// 外部 search.links / search.explain 不走这里——它们默认全量，见 <see cref="ResolveExternalPage"/>。
    /// </summary>
    public static (int Page, int PerPage) ResolvePage(int page, int perPage, EngineLimits limits)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePerPage = perPage <= 0 ? limits.MaxPageSize : Math.Min(perPage, limits.MaxPageSize);
        return (effectivePage, effectivePerPage);
    }

    /// <summary>
    /// 外部搜索分页归一：不传 / 0 / 负数 = **全量**（size 0 → SQL 无 LIMIT），
    /// 显式正数 = 调用方主动分页。外部命令以准确为先，不再用引擎上限静默截断。
    /// </summary>
    public static (int Page, int PerPage) ResolveExternalPage(int page, int perPage)
        => (Math.Max(1, page), perPage <= 0 ? 0 : perPage);

    /// <summary>
    /// 四范围搜索：title / url / description（ASCII 大小写不敏感包含）+ path（文件夹名命中 → 子树展开）。
    /// **过滤与排序全部 SQL 下推**——不再把全库链接读进内存逐条比对（10k/100k 库的关键路径）。
    /// 只有「目录名匹配 + 子树展开」在内存完成（目录数量有限，且树结构本就常驻）。
    /// 空查询不是引擎职责（由 Handler 报 LP.VAL.001）；范围全不选 = 无命中（引导空态属界面）。
    /// </summary>
    public static async Task<IReadOnlyList<(LinkDto Link, List<string> Matched)>> SearchAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        string sortBy, string sortOrder,
        CancellationToken ct,
        int page = 1, int perPage = 0)
    {
        var scope = await BuildScopeAsync(uow, query, searchTitle, searchUrl, searchDescription, searchPath, ct);
        return await SearchAsync(uow, scope, sortBy, sortOrder, ct, page, perPage);
    }

    /// <summary>已构建 scope 的搜索（快照会话一次展开目录，后续页不再重复 BFS）。</summary>
    public static async Task<IReadOnlyList<(LinkDto Link, List<string> Matched)>> SearchAsync(
        Kernel.IUnitOfWork uow, LinkSearchScope scope,
        string sortBy, string sortOrder,
        CancellationToken ct,
        int page = 1, int perPage = 0)
    {
        if (!scope.Any) return [];   // 全范围未启用 = 无命中

        // 直接 DTO 投影：界面/后台补全只要 DTO；先物化 Link 实体再 ToDto 是纯浪费
        //（100K 后台补全时省下十万个实体的分配与跟踪开销）。
        var links = await uow.Links.ListDtosAsync(
            new LinkQuerySpec
            {
                Filter = new LinkFilter { SearchScope = scope },
                // 缺省/空串/非法 sort_by 统一落回「title 升序」（与 search.links 文档口径一致；
                // 此前 fallback=created_at，`sort_by:""` 会静默变成按创建时间排序）
                Sort = QueryParsing.ParseSort(sortBy, sortOrder, QueryParsing.LinkSortFields, "title"),
                // SQL 级 LIMIT：调用方经 ResolvePage 归一后这里恒为有界页。
                Page = new PageSpec(page, perPage),
            }, ct);

        // 命中字段：对已筛出的候选集重算谓词（SQL 匹配 ⊇ 内存 OrdinalIgnoreCase 匹配，不会漏标）
        var pathSet = scope.Folders.Select(f => f.Value).ToHashSet(StringComparer.Ordinal);
        return links.Select(l => (l, MatchedFields(l, scope, pathSet, scope.Query))).ToList();
    }

    /// <summary>
    /// 与 <see cref="SearchAsync"/> 同谓词的命中总数（COUNT 下推）：不物化实体、不算命中字段——
    /// 专供 <c>search.count</c>，与分页后的主查询并行取"命中 N 条"。
    /// </summary>
    public static async Task<int> CountAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        CancellationToken ct)
    {
        var scope = await BuildScopeAsync(uow, query, searchTitle, searchUrl, searchDescription, searchPath, ct);
        return await CountAsync(uow, scope, ct);
    }

    /// <summary>已构建 scope 的计数（快照会话与首页查询共用同一份目录展开结果）。</summary>
    public static async Task<int> CountAsync(
        Kernel.IUnitOfWork uow, LinkSearchScope scope, CancellationToken ct)
    {
        if (!scope.Any) return 0;   // 全范围未启用 = 无命中（与 SearchAsync 口径一致）
        return await uow.Links.CountAsync(new LinkFilter { SearchScope = scope }, ct);
    }

    // ————————————————————————————————————————————————————————————————
    // 文件夹作为搜索结果（与链接同处一条有序流，见 SearchSessionStore 的说明）
    // ————————————————————————————————————————————————————————————————

    /// <summary>结果集里的文件夹行类型值。</summary>
    public const string FolderKind = "folder";

    /// <summary>
    /// 选进结果流的文件夹命中行：标题档取「名称命中」，位置档取「严格后代」。
    /// **只收集轻量命中行**（id/name/排序键），完整 DTO 推迟到 <see cref="BuildPageAsync"/> 按页补水。
    /// </summary>
    public static List<FolderSearchHit> BuildFolderRows(FolderNameSearch hits, bool searchTitle, bool searchPath)
    {
        var rows = new List<FolderSearchHit>(hits.Matches.Count + hits.Descendants.Count);
        if (searchTitle)
            foreach (var hit in hits.Matches)
                if (!string.IsNullOrEmpty(hit.Id) && !string.IsNullOrWhiteSpace(hit.Name)) rows.Add(hit);
        if (searchPath)
            foreach (var hit in hits.Descendants)
                if (!string.IsNullOrEmpty(hit.Id) && !string.IsNullOrWhiteSpace(hit.Name)) rows.Add(hit);
        return rows;
    }

    /// <summary>
    /// 文件夹块排序：名称走 OrdinalIgnoreCase（与链接 SQL 的 `COLLATE NOCASE` 同口径）；
    /// 日期是 ISO 字符串（字典序即时间序），「最后查看」为空恒排最后（与排序引擎一致，**不随升降序翻转**）。
    /// </summary>
    public static void SortFolderHits(List<FolderSearchHit> folders, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        Comparison<FolderSearchHit> key = sortBy switch
        {
            "created_at" or "updated_at" => (a, b) =>
                string.CompareOrdinal(a.SortText ?? string.Empty, b.SortText ?? string.Empty),
            "last_visited_at" => (a, b) => CompareNullsLast(a.SortText, b.SortText),
            "visit_count" => (a, b) => a.SortNumber.CompareTo(b.SortNumber),
            // url / is_important 对文件夹恒等 → 退化为按名称
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        folders.Sort((a, b) =>
        {
            var result = key(a, b);
            // 「最后查看」为空的行恒在末尾：只在两侧都有值时应用方向。
            if (sortBy == "last_visited_at" && (a.SortText is null || b.SortText is null))
                result = CompareNullsLast(a.SortText, b.SortText);
            else if (desc) result = -result;
            if (result == 0) result = desc
                ? -string.CompareOrdinal(a.Id, b.Id)    // 稳定 tie-breaker：翻页不重不漏
                : string.CompareOrdinal(a.Id, b.Id);
            return result;
        });
    }

    private static int CompareNullsLast(string? a, string? b)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        return string.CompareOrdinal(a, b);
    }

    /// <summary>按页补水目录完整字段（描述 / 父目录 / 日期 / 次数）：命中行只带 id+name+排序键。</summary>
    private static async Task<List<LinkDto>> FolderHitsToDtosAsync(
        Kernel.IUnitOfWork uow, IReadOnlyList<FolderSearchHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return [];
        var folders = await uow.Folders.FindManyAsync(
            hits.Select(h => new FolderId(h.Id)).ToList(), ct);
        var byId = folders.ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var rows = new List<LinkDto>(hits.Count);
        foreach (var hit in hits)
        {
            // 会话是同一读事务快照：正常不可能缺行。真缺了也保留结果行（计数与翻页不漂移），
            // 只是展示字段退化为名称（脏数据如实暴露，不静默吞掉一行）。
            byId.TryGetValue(hit.Id, out var folder);
            rows.Add(new LinkDto
            {
                Kind = FolderKind,
                LinkId = hit.Id,
                Title = folder?.Name ?? hit.Name,
                Url = string.Empty,
                Description = folder?.Description ?? string.Empty,
                ListId = folder?.ParentId,          // 位置列 = 父路径（与链接行的"所在目录"同义）
                VisitCount = folder?.VisitCount ?? 0,
                LastVisitedAt = folder?.LastVisitedAt,
                CreatedAt = folder?.CreatedAt ?? default,
                UpdatedAt = folder?.UpdatedAt ?? default,
            });
        }
        return rows;
    }

    /// <summary>
    /// 取一页（文件夹块 + 链接块拼成全局有序窗口）：
    /// 全局偏移按页首算；文件夹块占 <c>[0, F)</c>，链接块从 <c>F</c> 起。
    /// 链接段用**精确偏移**取（<see cref="PageSpec.SkipOverride"/>），不会为了对齐页边界多取整页；
    /// 且**一次有序扫描同时计数 + 收窗口**（<paramref name="needLinkTotal"/> = true 时），
    /// 不再为"总数"和"首页链接"各扫一遍（见 WARNINGS 247）。
    /// 返回值 = 链接命中总数（不含文件夹；<paramref name="needLinkTotal"/> = false 时为 0）+ 本页行。
    /// </summary>
    public static async Task<(int LinkTotal, List<LinkDto> Rows)> BuildPageAsync(
        Kernel.IUnitOfWork uow, LinkSearchScope scope, IReadOnlyList<FolderSearchHit> folders,
        string sortBy, string sortOrder, int page, int perPage, bool needLinkTotal, CancellationToken ct)
    {
        var offset = Math.Max(0, (Math.Max(1, page) - 1) * perPage);
        var rows = new List<LinkDto>(perPage);

        var folderCount = folders.Count;
        if (offset < folderCount)
        {
            var end = Math.Min(folderCount, offset + perPage);
            var pageHits = new List<FolderSearchHit>(end - offset);
            for (var i = offset; i < end; i++) pageHits.Add(folders[i]);
            rows.AddRange(await FolderHitsToDtosAsync(uow, pageHits, ct));
        }

        var remaining = perPage - rows.Count;
        if (remaining <= 0)
        {
            // 文件夹块已经填满这一页：仍可能要为"总数"数一次链接（首页），后续页不需要。
            var total = needLinkTotal
                ? (await uow.Links.CountAndFirstPageAsync(scope, sortBy, sortOrder, 0, 0, ct)).Total
                : 0;
            return (total, rows);
        }

        var linkSkip = Math.Max(0, offset - folderCount);
        var links = await uow.Links.CountAndFirstPageAsync(
            scope, sortBy, sortOrder, remaining, linkSkip, ct);
        rows.AddRange(links.FirstPage);
        return (links.Total, rows);
    }

    /// <summary>由目录命中构造作用域（同步：与文件夹结果行共用同一次 SQL 查询）。</summary>
    public static LinkSearchScope BuildScope(
        string query, bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        FolderNameSearch? hits)
        => new()
        {
            Query = query,
            Title = searchTitle,
            Url = searchUrl,
            Description = searchDescription,
            // 位置范围 = 名称命中 ∪ 全部后代（同一份命中结果，不再二次展开）
            Folders = searchPath && hits is not null
                ? hits.All.Select(f => new FolderId(f.Id)).ToList()
                : [],
        };

    /// <summary>构建一次多范围谓词（外部命令入口：自己查目录命中；会话走 <see cref="BuildScope"/> 复用一次查询）。</summary>
    public static async Task<LinkSearchScope> BuildScopeAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        CancellationToken ct)
    {
        var hits = searchPath && !string.IsNullOrWhiteSpace(query)
            ? await uow.Folders.SearchByNameAsync(query, includeDescendants: true, sortBy: null, ct)
            : null;
        return BuildScope(query, searchTitle, searchUrl, searchDescription, searchPath, hits);
    }

    /// <summary>逐字段标注命中来源（与 SQL 谓词同义；供 search.explain 与结果高亮）。
    /// 匹配口径走 <see cref="TextMatch"/>——与界面侧高亮<b>同一份实现</b>（两套规则必然分叉）。</summary>
    private static List<string> MatchedFields(
        LinkDto link, LinkSearchScope scope, IReadOnlySet<string> pathSet, string query)
    {
        var matched = new List<string>();
        if (scope.Title && TextMatch.Contains(link.Title, query))
            matched.Add("title");
        if (scope.Url && TextMatch.Contains(link.Url, query))
            matched.Add("url");
        if (scope.Description && TextMatch.Contains(link.Description, query))
            matched.Add("description");
        if (link.ListId != null && pathSet.Contains(link.ListId))
            matched.Add("path");
        return matched;
    }
}
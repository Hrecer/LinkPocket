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
    /// 命中的文件夹行（`Kind=folder`）：**只认「标题（名称）」与「位置（祖先名）」两档**——
    /// URL / 描述对文件夹没有意义，那两档单独选中时文件夹不命中。
    /// 虚根「全部书签」不落库、没有 ID，天然不会出现在这里；空 id / 空名再挡一次。
    /// </summary>
    public static async Task<List<LinkDto>> FindMatchingFoldersAsync(
        Kernel.IUnitOfWork uow, string query, bool searchTitle, bool searchPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || (!searchTitle && !searchPath)) return [];

        var all = await uow.Folders.ListAllAsync(ct);
        if (all.Count == 0) return [];

        var byId = new Dictionary<string, Folder>(all.Count, StringComparer.Ordinal);
        foreach (var folder in all) byId[folder.FolderId] = folder;

        // 祖先名命中：沿父链走一次并记忆化。文件夹数量级远小于链接（本机 107 vs 100 006），
        // 深树（101 层）也只走一遍；不物化 path 列、不做 SQL 递归 CTE。
        var ancestorHit = new Dictionary<string, bool>(all.Count, StringComparer.Ordinal);
        bool AncestorMatches(string folderId)
        {
            if (ancestorHit.TryGetValue(folderId, out var known)) return known;
            var hit = false;
            var cursor = byId.TryGetValue(folderId, out var self) ? self.ParentId : null;
            for (var guard = 0; !string.IsNullOrEmpty(cursor) && guard < 512; guard++)
            {
                if (!byId.TryGetValue(cursor, out var parent)) break;
                if (parent.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                cursor = parent.ParentId;
            }
            ancestorHit[folderId] = hit;
            return hit;
        }

        var rows = new List<LinkDto>();
        foreach (var folder in all)
        {
            if (string.IsNullOrEmpty(folder.FolderId) || string.IsNullOrWhiteSpace(folder.Name)) continue;
            var titleHit = searchTitle && folder.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!titleHit && !(searchPath && AncestorMatches(folder.FolderId))) continue;

            rows.Add(new LinkDto
            {
                Kind = FolderKind,
                LinkId = folder.FolderId,
                Title = folder.Name,
                Url = string.Empty,
                Description = folder.Description ?? string.Empty,
                ListId = folder.ParentId,          // 位置列 = 父路径（与链接行的"所在目录"同义）
                VisitCount = folder.VisitCount,
                LastVisitedAt = folder.LastVisitedAt,
                CreatedAt = folder.CreatedAt,
                UpdatedAt = folder.UpdatedAt,
            });
        }
        return rows;
    }

    /// <summary>文件夹块排序：与链接同名的排序键各取自己的字段；`url`/`is_important` 对文件夹恒等 → 退化为按名称。</summary>
    public static void SortFolders(List<LinkDto> folders, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        Comparison<LinkDto> key = sortBy switch
        {
            "created_at" => (a, b) => a.CreatedAt.CompareTo(b.CreatedAt),
            "updated_at" => (a, b) => a.UpdatedAt.CompareTo(b.UpdatedAt),
            "last_visited_at" => (a, b) => Nullable.Compare(a.LastVisitedAt, b.LastVisitedAt),
            "visit_count" => (a, b) => a.VisitCount.CompareTo(b.VisitCount),
            "url" or "is_important" => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
        };

        folders.Sort((a, b) =>
        {
            var result = key(a, b);
            if (result == 0) result = string.CompareOrdinal(a.LinkId, b.LinkId);   // 稳定 tie-breaker：翻页不重不漏
            return desc ? -result : result;
        });
    }

    /// <summary>
    /// 取一页（文件夹块 + 链接块拼成全局有序窗口）：
    /// 全局偏移按页首算；文件夹块占 <c>[0, F)</c>，链接块从 <c>F</c> 起。
    /// 链接段用**精确偏移**取（<see cref="PageSpec.SkipOverride"/>），不会为了对齐页边界多取整页。
    /// </summary>
    public static async Task<IReadOnlyList<LinkDto>> BuildPageAsync(
        Kernel.IUnitOfWork uow, LinkSearchScope scope, IReadOnlyList<LinkDto> folders,
        string sortBy, string sortOrder, int page, int perPage, CancellationToken ct)
    {
        var offset = Math.Max(0, (Math.Max(1, page) - 1) * perPage);
        var rows = new List<LinkDto>(perPage);

        var folderCount = folders.Count;
        if (offset < folderCount)
        {
            var end = Math.Min(folderCount, offset + perPage);
            for (var i = offset; i < end; i++) rows.Add(folders[i]);
        }

        var remaining = perPage - rows.Count;
        if (remaining <= 0) return rows;

        var linkSkip = Math.Max(0, offset - folderCount);
        var links = await uow.Links.ListDtosAsync(new LinkQuerySpec
        {
            Filter = new LinkFilter { SearchScope = scope },
            Sort = QueryParsing.ParseSort(sortBy, sortOrder, QueryParsing.LinkSortFields, "title"),
            Page = new PageSpec(Size: remaining, SkipOverride: linkSkip),
        }, ct);
        rows.AddRange(links);
        return rows;
    }

    /// <summary>构建一次多范围谓词；目录名命中时**只在这里展开子树**。</summary>
    public static async Task<LinkSearchScope> BuildScopeAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        CancellationToken ct)
        => new()
        {
            Query = query,
            Title = searchTitle,
            Url = searchUrl,
            Description = searchDescription,
            Folders = searchPath ? await ExpandMatchingFoldersAsync(uow, query, ct) : [],
        };

    /// <summary>目录名命中的文件夹 + 其整棵子树（命中目录本身也算命中）。</summary>
    private static async Task<List<FolderId>> ExpandMatchingFoldersAsync(
        Kernel.IUnitOfWork uow, string query, CancellationToken ct)
    {
        var folders = await uow.Folders.ListAllAsync(ct);
        var childrenOf = folders
            .Where(f => f.ParentId != null)
            .ToLookup(f => f.ParentId!, StringComparer.Ordinal);

        var expanded = new List<FolderId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(folders
            .Where(f => TextMatch.Contains(f.Name, query))
            .Select(f => f.FolderId));

        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!seen.Add(id)) continue;
            expanded.Add(new FolderId(id));
            foreach (var child in childrenOf[id]) pending.Enqueue(child.FolderId);
        }

        return expanded;
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
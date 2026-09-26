using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Search;

/// <summary>搜索域内部支撑：多范围谓词（SQL 下推）与命中字段标注。</summary>
internal static class SearchSupport
{
    /// <summary>
    /// 四范围搜索：title / url / description（ASCII 大小写不敏感包含）+ path（文件夹名命中 → 子树展开）。
    /// **过滤与排序全部 SQL 下推**——不再把全库链接读进内存逐条比对（10k/100k 库的关键路径）。
    /// 只有「目录名匹配 + 子树展开」在内存完成（目录数量有限，且树结构本就常驻）。
    /// 空查询不是引擎职责（由 Handler 报 LP.VAL.001）；范围全不选 = 无命中（引导空态属界面）。
    /// </summary>
    public static async Task<IReadOnlyList<(Link Link, List<string> Matched)>> SearchAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        string sortBy, string sortOrder,
        CancellationToken ct,
        int page = 1, int perPage = 0)
    {
        var scope = new LinkSearchScope
        {
            Query = query,
            Title = searchTitle,
            Url = searchUrl,
            Description = searchDescription,
            Folders = searchPath ? await ExpandMatchingFoldersAsync(uow, query, ct) : [],
        };

        if (!scope.Any) return [];   // 全范围未启用 = 无命中

        var links = await uow.Links.ListAsync(
            new LinkQuerySpec
            {
                Filter = new LinkFilter { SearchScope = scope },
                // 缺省/空串/非法 sort_by 统一落回「title 升序」（与 search.links 文档口径一致；
                // 此前 fallback=created_at，`sort_by:""` 会静默变成按创建时间排序）
                Sort = QueryParsing.ParseSort(sortBy, sortOrder, QueryParsing.LinkSortFields, "title"),
                // per_page > 0 = SQL 级 LIMIT（十万级命中的关键路径：界面只渲染前几百条，
                // 全量物化 + 12MB JSON 过管道是实测的 ~800ms 纯浪费；0 = 全量，向后兼容）
                Page = perPage > 0 ? new PageSpec(page, perPage) : new PageSpec(1, 0),
            }, ct);

        // 命中字段：对已筛出的候选集重算谓词（SQL 匹配 ⊇ 内存 OrdinalIgnoreCase 匹配，不会漏标）
        var pathSet = scope.Folders.Select(f => f.Value).ToHashSet(StringComparer.Ordinal);
        return links.Select(l => (l, MatchedFields(l, scope, pathSet, query))).ToList();
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
        var scope = new LinkSearchScope
        {
            Query = query,
            Title = searchTitle,
            Url = searchUrl,
            Description = searchDescription,
            Folders = searchPath ? await ExpandMatchingFoldersAsync(uow, query, ct) : [],
        };

        if (!scope.Any) return 0;   // 全范围未启用 = 无命中（与 SearchAsync 口径一致）
        return await uow.Links.CountAsync(new LinkFilter { SearchScope = scope }, ct);
    }

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
        Link link, LinkSearchScope scope, IReadOnlySet<string> pathSet, string query)
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
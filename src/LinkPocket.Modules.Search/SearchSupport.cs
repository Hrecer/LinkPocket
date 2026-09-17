using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>搜索域内部支撑：范围谓词匹配与路径展开（与既有 SearchAsync 逐条等价）。</summary>
internal static class SearchSupport
{
    /// <summary>
    /// 四范围匹配：title / url / description（OrdinalIgnoreCase contains）+ path（文件夹名命中 → 子树展开）。
    /// 全部范围未选 = 标题（既有 API 兜底口径；UI 层另有引导空态守卫）。
    /// </summary>
    public static async Task<IReadOnlyList<(Link Link, List<string> Matched)>> MatchAsync(
        Kernel.IUnitOfWork uow, string query,
        bool searchTitle, bool searchUrl, bool searchDescription, bool searchPath,
        CancellationToken ct)
    {
        var links = await uow.Links.ListAsync(new LinkQuerySpec(), ct);

        var folders = searchPath ? await uow.Folders.ListAllAsync(ct) : null;
        HashSet<string>? expanded = null;
        if (folders != null)
        {
            var matched = folders
                .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FolderId)
                .ToHashSet(StringComparer.Ordinal);
            if (matched.Count > 0)
            {
                expanded = new HashSet<string>(matched);
                foreach (var fid in matched)
                    Expand(folders, fid, expanded);
            }
        }

        var hits = new List<(Link, List<string>)>();
        foreach (var link in links)
        {
            var matchedFields = new List<string>();
            if (searchTitle && link.Title != null && link.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                matchedFields.Add("title");
            if (searchUrl && link.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                matchedFields.Add("url");
            if (searchDescription && link.Description != null
                && link.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                matchedFields.Add("description");
            if (searchPath && expanded != null && link.ListId != null && expanded.Contains(link.ListId))
                matchedFields.Add("path");

            // 全部范围未选 → 标题兜底（与既有口径一致）
            if (matchedFields.Count == 0 && !searchTitle && !searchUrl && !searchDescription && !searchPath)
            {
                if (link.Title != null && link.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    matchedFields.Add("title");
            }

            if (matchedFields.Count > 0)
                hits.Add((link, matchedFields));
        }

        return hits;

        static void Expand(IReadOnlyList<Folder> all, string folderId, HashSet<string> into)
        {
            foreach (var child in all.Where(f => f.ParentId == folderId))
            {
                if (into.Add(child.FolderId))
                    Expand(all, child.FolderId, into);
            }
        }
    }

    /// <summary>排序（与既有 SortLinks 逐条等价：CurrentCulture 标题、ID 兜底）。</summary>
    public static List<T> SortHits<T>(
        IEnumerable<T> source, Func<T, Link> link, string sortBy, string sortOrder)
    {
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<T> ordered = sortBy switch
        {
            "title" => desc
                ? source.OrderByDescending(x => link(x).Title, StringComparer.CurrentCulture)
                : source.OrderBy(x => link(x).Title, StringComparer.CurrentCulture),
            "updated_at" => desc ? source.OrderByDescending(x => link(x).UpdatedAt) : source.OrderBy(x => link(x).UpdatedAt),
            "last_visited_at" => desc
                ? source.OrderByDescending(x => link(x).LastVisitedAt ?? DateTime.MinValue)
                : source.OrderBy(x => link(x).LastVisitedAt ?? DateTime.MinValue),
            "visit_count" => desc ? source.OrderByDescending(x => link(x).VisitCount) : source.OrderBy(x => link(x).VisitCount),
            _ => desc ? source.OrderByDescending(x => link(x).CreatedAt) : source.OrderBy(x => link(x).CreatedAt),
        };
        return ordered.ThenBy(x => link(x).LinkId, StringComparer.Ordinal).ToList();
    }
}

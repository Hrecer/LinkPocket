using System.Text;
using LinkPocket.Data;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>
/// Netscape 书签文件写出器（internal，现状算法平移）。
/// 完整性口径：全库链接与文件夹一次性读入内存建树后输出——无归属书签落根级（绝不丢数据），
/// 父链成环的文件夹按根级补写；UTF-8 无 BOM（与 Chrome 一致）、CRLF、缩进 4 空格。
/// </summary>
internal static class NetscapeWriter
{
    private const string Indent = "    ";

    /// <summary>写出统计（调用方落到命令结果）。</summary>
    internal sealed class WriteStats
    {
        public int FoldersExported;
        public int LinksExported;
        public int RootLinksExported;
        public int OrphanLinksExported;
    }

    /// <summary>由全量文件夹/链接构建 Netscape HTML（不落盘——落盘归 Handler，便于单测）。</summary>
    public static Task<string> BuildHtmlAsync(
        IReadOnlyList<Folder> folders, IReadOnlyList<Link> links,
        WriteStats stats, CancellationToken ct)
    {
        var folderIds = new HashSet<string>(folders.Select(f => f.FolderId), StringComparer.Ordinal);

        // 子文件夹索引：父 ID（根用空串占位）→ 子文件夹（按名称升序，与界面口径一致）
        var childrenByParent = folders
            .GroupBy(f => f.ParentId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        // 链接归桶：无归属（ListId 空/指向已删文件夹）落根级，不丢数据、单独计数
        var linksByFolder = new Dictionary<string, List<Link>>(StringComparer.Ordinal);
        var rootLinks = new List<Link>();
        foreach (var link in links.OrderBy(l => ToUtc(l.CreatedAt)))
        {
            ct.ThrowIfCancellationRequested();
            var listId = link.ListId;
            if (!string.IsNullOrEmpty(listId) && folderIds.Contains(listId))
            {
                if (!linksByFolder.TryGetValue(listId, out var bucket))
                    linksByFolder[listId] = bucket = [];
                bucket.Add(link);
            }
            else
            {
                rootLinks.Add(link);
                if (!string.IsNullOrEmpty(listId)) stats.OrphanLinksExported++;
            }
        }

        // 根级文件夹：ParentId 为空，或父 ID 已不存在（悬挂父链，视为根级以便可见）
        var rootFolders = folders
            .Where(f => string.IsNullOrEmpty(f.ParentId) || !folderIds.Contains(f.ParentId))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        // 根级文件夹 ID 集合：判定非根可达（悬挂父链/环）时用 HashSet 而非 List.Contains（避免 O(N²)）
        var rootFolderIds = rootFolders.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);

        var html = new StringBuilder();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        html.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n");
        html.Append("<!-- This is an automatically generated file.\r\n");
        html.Append("     It will be read and overwritten.\r\n");
        html.Append("     DO NOT EDIT! -->\r\n");
        html.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\r\n");
        html.Append("<TITLE>Bookmarks</TITLE>\r\n");
        html.Append("<H1>Bookmarks</H1>\r\n");
        html.Append("<DL><p>\r\n");

        // 根级书签（含无归属）——与 Chrome 一致：直接挂在顶层 DL 下
        foreach (var link in rootLinks)
        {
            ct.ThrowIfCancellationRequested();
            html.Append(FormatLink(link, 1)).Append("\r\n");
            stats.RootLinksExported++;
        }

        // 文件夹树（含环引用兜底）：显式栈先序遍历 + 后序关标签——语义等价原递归版本，
        // 但绝不依赖调用栈深度（5k+ 深层嵌套时递归会 StackOverflow，进程无法 catch，必须迭代）。
        var pending = new Stack<(Folder Folder, int Depth)>();
        foreach (var folder in folders
                     .Where(f => !rootFolderIds.Contains(f.FolderId))   // 未从根可达的（悬挂父链/环）按根级补写
                     .OrderBy(f => f.Name, StringComparer.Ordinal)
                     .Reverse())
            pending.Push((folder, 1));
        foreach (var folder in ((IEnumerable<Folder>)rootFolders).Reverse())   // List<T>.Reverse() 是 void 实例方法，须显式走后序 LINQ 扩展
            pending.Push((folder, 1));

        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            if (depth < 0)   // 关标签标记：其子树输出完毕后闭合 <DL>
            {
                html.Append(Pad(-depth)).Append("</DL><p>\r\n");
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (!emitted.Add(folder.FolderId)) continue;   // 已输出（含环引用）→ 跳过

            var pad = Pad(depth);
            var name = EscapeHtml(folder.Name ?? "未命名文件夹");

            html.Append(pad).Append("<DT><H3 ADD_DATE=\"").Append(ToUnix(folder.CreatedAt))
                .Append("\" LAST_MODIFIED=\"").Append(ToUnix(folder.UpdatedAt))
                .Append("\">").Append(name).Append("</H3>\r\n");
            html.Append(pad).Append("<DL><p>\r\n");
            stats.FoldersExported++;

            if (linksByFolder.TryGetValue(folder.FolderId, out var folderLinks))
            {
                foreach (var link in folderLinks)
                {
                    html.Append(FormatLink(link, depth + 1)).Append("\r\n");
                    stats.LinksExported++;
                }
            }

            if (childrenByParent.TryGetValue(folder.FolderId, out var children))
            {
                pending.Push((folder, -depth));   // 后序：子树输出完再闭合本层
                foreach (var child in ((IEnumerable<Folder>)children).Reverse())
                    pending.Push((child, depth + 1));
            }
            else
            {
                html.Append(pad).Append("</DL><p>\r\n");
            }
        }

        html.Append("</DL><p>\r\n");
        return Task.FromResult(html.ToString());
    }

    private static string Pad(int depth) => new string(' ', depth * Indent.Length);

    /// <summary>单条书签行：Chrome 形态 <c>&lt;DT&gt;&lt;A HREF="…" ADD_DATE="…" [ICON="…"]&gt;标题&lt;/A&gt;</c>。</summary>
    private static string FormatLink(Link link, int depth)
    {
        var pad = Pad(depth);
        var url = EscapeHtml(link.Url ?? string.Empty);
        var title = EscapeHtml(string.IsNullOrEmpty(link.Title) ? (link.Url ?? "无标题") : link.Title);

        var sb = new StringBuilder();
        sb.Append(pad).Append("<DT><A HREF=\"").Append(url).Append('"');
        sb.Append(" ADD_DATE=\"").Append(ToUnix(link.CreatedAt)).Append('"');
        if (link.UpdatedAt != default)
            sb.Append(" LAST_MODIFIED=\"").Append(ToUnix(link.UpdatedAt)).Append('"');
        if (!string.IsNullOrWhiteSpace(link.FaviconUrl))
            sb.Append(" ICON=\"").Append(EscapeHtml(link.FaviconUrl)).Append('"');
        sb.Append('>').Append(title).Append("</A>");

        if (!string.IsNullOrWhiteSpace(link.Description))
            sb.Append("\r\n").Append(pad).Append("<DD>").Append(EscapeHtml(link.Description));

        return sb.ToString();
    }

    /// <summary>把任意 Kind 的时间归一到 UTC 再取 Unix 秒（库中持久化的都是 UTC 时间）。</summary>
    private static long ToUnix(DateTime value) => new DateTimeOffset(ToUtc(value)).ToUnixTimeSeconds();

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>HTML 转义（顺序敏感：&amp; 必须最先替换）。</summary>
    private static string EscapeHtml(string text)
        => text
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}

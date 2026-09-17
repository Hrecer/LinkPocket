using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;

namespace LinkPocket.Services;

/// <summary>
/// Netscape 书签文件格式（Netscape Bookmark File Format）导出器。
///
/// <para><b>格式确认（2026-09-16 全面复核）</b>：Chrome / Edge / Firefox / Safari / Opera 的
/// 「导出书签」产出的都是同一份 Netscape 书签文件格式，其判别特征为——</para>
/// <list type="bullet">
///   <item>首行 <c>&lt;!DOCTYPE NETSCAPE-Bookmark-file-1&gt;</c>（这是该格式唯一权威判别标记，Chrome 亦如此）；</item>
///   <item><c>&lt;META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8"&gt;</c> + <c>&lt;TITLE&gt;</c> + <c>&lt;H1&gt;</c>；</item>
///   <item>UTF-8（Chrome 写出的文件不带 BOM）；换行 CRLF；文件夹 = <c>&lt;DT&gt;&lt;H3&gt;名&lt;/H3&gt;</c> 后跟一对 <c>&lt;DL&gt;&lt;p&gt;…&lt;/DL&gt;&lt;p&gt;</c>；书签 = <c>&lt;DT&gt;&lt;A HREF="…"&gt;标题&lt;/A&gt;</c>；</item>
///   <item>时间戳统一为 Unix 秒：<c>ADD_DATE</c> / <c>LAST_MODIFIED</c>；</item>
///   <item>书签栏文件夹带 <c>PERSONAL_TOOLBAR_FOLDER="true"</c> 属性（可选，导入方可忽略）。</item>
/// </list>
///
/// <para><b>解耦约定</b>：本类只依赖 EF 数据上下文（<see cref="LinkPocketDbContext"/>），不引用任何界面/窗口类型，
/// 因此可被任意宿主（后端 API、命令行工具、测试工程）直接调用；
/// 对外可达路径见 <c>ILinkPocketApi.ExportBookmarksHtmlAsync</c> 与协议方法 <c>export.bookmarks_html</c>。</para>
///
/// <para><b>完整性口径</b>：全库链接与文件夹一次性读入内存建树后输出——
/// 归属文件夹已被删除的「无归属书签」落到根级（绝不静默丢弃数据），
/// 父链成环的文件夹做兜底输出，写出的条目数必须等于库中条目数。</para>
/// </summary>
public class BookmarkExporter
{
    private readonly LinkPocketDbContext _db;

    public BookmarkExporter(LinkPocketDbContext db)
    {
        _db = db;
    }

    /// <summary>缩进单位：Chrome 每层 4 空格。</summary>
    private const string Indent = "    ";

    /// <summary>
    /// 导出全部书签为 Netscape 书签文件（.html）。
    /// </summary>
    /// <param name="outputFilePath">输出<b>文件</b>的完整路径（不是目录）。目录必须已存在。</param>
    /// <param name="progress">可选进度回调：(消息, 已完成, 总数)。</param>
    public async Task<BookmarkExportResult> ExportAsync(string outputFilePath,
        IProgress<(string message, int current, int total)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new ArgumentException("导出文件路径不能为空", nameof(outputFilePath));

        var fullPath = Path.GetFullPath(outputFilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"导出目录不存在：{directory}");

        progress?.Report(("正在读取书签数据...", 0, 0));

        // —— 一次性读库（避免逐文件夹查询的 N+1）——
        var folders = await _db.Folders.AsNoTracking().ToListAsync();
        var links = await _db.Links.AsNoTracking().ToListAsync();

        var folderIds = new HashSet<string>(folders.Select(f => f.FolderId), StringComparer.Ordinal);

        // 子文件夹索引：父 ID（根用空串占位）→ 子文件夹（按名称升序，与界面口径一致）
        var childrenByParent = folders
            .GroupBy(f => f.ParentId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        // 链接归桶：ListId 为空 → 根级；ListId 指向已不存在的文件夹 → 也落根级（不丢数据，单独计数）
        var linksByFolder = new Dictionary<string, List<Link>>(StringComparer.Ordinal);
        var rootLinks = new List<Link>();
        var orphanCount = 0;
        foreach (var link in links.OrderBy(l => ToUtc(l.CreatedAt)))
        {
            var listId = link.ListId;
            if (!string.IsNullOrEmpty(listId) && folderIds.Contains(listId))
            {
                if (!linksByFolder.TryGetValue(listId, out var bucket))
                {
                    bucket = new List<Link>();
                    linksByFolder[listId] = bucket;
                }
                bucket.Add(link);
            }
            else
            {
                rootLinks.Add(link);
                if (!string.IsNullOrEmpty(listId)) orphanCount++;
            }
        }

        // 根级文件夹：ParentId 为空，或父 ID 已不存在（悬挂父链，视为根级以便可见）
        var rootFolders = folders
            .Where(f => string.IsNullOrEmpty(f.ParentId) || !folderIds.Contains(f.ParentId))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        var total = folders.Count + links.Count;
        var done = new ProgressCounter();
        var result = new BookmarkExportResult { FilePath = fullPath, OrphanLinksExported = orphanCount };

        await using (var writer = new StreamWriter(fullPath, false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))   // UTF-8 无 BOM（与 Chrome 一致）
        {
            writer.NewLine = "\r\n";                                     // Chrome 同样使用 CRLF

            await writer.WriteLineAsync("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
            await writer.WriteLineAsync("<!-- This is an automatically generated file.");
            await writer.WriteLineAsync("     It will be read and overwritten.");
            await writer.WriteLineAsync("     DO NOT EDIT! -->");
            await writer.WriteLineAsync("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
            await writer.WriteLineAsync("<TITLE>Bookmarks</TITLE>");
            await writer.WriteLineAsync("<H1>Bookmarks</H1>");
            await writer.WriteLineAsync("<DL><p>");

            // 根级书签（含无归属书签）——与 Chrome 一致：直接挂在顶层 DL 下
            foreach (var link in rootLinks)
            {
                await writer.WriteLineAsync(FormatLink(link, 1));
                done.Value++;
                Report(progress, "正在导出根级书签...", done.Value, total);
            }
            result.RootLinksExported = rootLinks.Count;

            // 文件夹树
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in rootFolders)
            {
                await EmitFolderAsync(writer, folder, 1, childrenByParent, linksByFolder, emitted, result,
                    progress, total, done);
            }

            // 环引用兜底：父链成环导致未被输出的文件夹，按根级补写（否则其链接会丢失）
            foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (emitted.Contains(folder.FolderId)) continue;
                Logger.Info($"[导出] 检测到父链异常（成环）的文件夹，按根级补写：{folder.Name} ({folder.FolderId})");
                await EmitFolderAsync(writer, folder, 1, childrenByParent, linksByFolder, emitted, result,
                    progress, total, done);
            }

            await writer.WriteLineAsync("</DL><p>");
        }

        result.FileBytes = new FileInfo(fullPath).Length;
        progress?.Report(("导出完成", done.Value, total));
        return result;
    }

    /// <summary>递归写出一个文件夹（含其子树）；<paramref name="emitted"/> 防环。</summary>
    private static async Task EmitFolderAsync(StreamWriter writer, Folder folder, int depth,
        Dictionary<string, List<Folder>> childrenByParent,
        Dictionary<string, List<Link>> linksByFolder,
        HashSet<string> emitted, BookmarkExportResult result,
        IProgress<(string message, int current, int total)>? progress, int total, ProgressCounter done)
    {
        if (!emitted.Add(folder.FolderId)) return;   // 已输出（含环引用）→ 跳过

        var pad = string.Concat(Enumerable.Repeat(Indent, depth));
        var name = EscapeHtml(folder.Name ?? "未命名文件夹");

        await writer.WriteLineAsync(
            $"{pad}<DT><H3 ADD_DATE=\"{ToUnix(folder.CreatedAt)}\" LAST_MODIFIED=\"{ToUnix(folder.UpdatedAt)}\">{name}</H3>");
        await writer.WriteLineAsync($"{pad}<DL><p>");

        result.FoldersExported++;
        done.Value++;
        Report(progress, $"正在导出: {folder.Name}", done.Value, total);

        if (linksByFolder.TryGetValue(folder.FolderId, out var links))
        {
            foreach (var link in links)
            {
                await writer.WriteLineAsync(FormatLink(link, depth + 1));
                result.LinksExported++;
                done.Value++;
                Report(progress, $"正在导出: {folder.Name}", done.Value, total);
            }
        }

        if (childrenByParent.TryGetValue(folder.FolderId, out var children))
        {
            foreach (var child in children)
            {
                await EmitFolderAsync(writer, child, depth + 1, childrenByParent, linksByFolder,
                    emitted, result, progress, total, done);
            }
        }

        await writer.WriteLineAsync($"{pad}</DL><p>");
    }

    /// <summary>单条书签行：Chrome 形态 <c>&lt;DT&gt;&lt;A HREF="…" ADD_DATE="…" [ICON="…"]&gt;标题&lt;/A&gt;</c>。</summary>
    private static string FormatLink(Link link, int depth)
    {
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));
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

        // <DD> 描述（Netscape 格式的可选字段，Firefox 会读取）
        if (!string.IsNullOrWhiteSpace(link.Description))
            sb.Append('\n').Append(pad).Append("<DD>").Append(EscapeHtml(link.Description));

        return sb.ToString();
    }

    private static void Report(IProgress<(string message, int current, int total)>? progress,
        string message, int done, int total)
        => progress?.Report((message, done, total));

    /// <summary>把任意 Kind 的时间归一到 UTC 再取 Unix 秒（数据库持久化的都是 UTC 时间）。</summary>
    private static long ToUnix(DateTime value) => new DateTimeOffset(ToUtc(value)).ToUnixTimeSeconds();

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>HTML 转义（顺序敏感：&amp; 必须最先替换）。</summary>
    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}

/// <summary>递归导出时的可变计数器（异步方法不能使用 ref 参数）。</summary>
public class ProgressCounter
{
    public int Value { get; set; }
}

/// <summary>导出结果统计（对外可读：供界面展示"已导出 N 个书签 / M 个文件夹"）。</summary>
public class BookmarkExportResult
{
    /// <summary>写出文件的完整路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    public int FoldersExported { get; set; }
    public int LinksExported { get; set; }
    /// <summary>根级（未归类）书签数，含无归属书签。</summary>
    public int RootLinksExported { get; set; }
    /// <summary>归属文件夹已不存在、被落到根级的书签数（导出不丢数据）。</summary>
    public int OrphanLinksExported { get; set; }
    public long FileBytes { get; set; }

    public int TotalLinks => LinksExported + RootLinksExported;
    public int TotalItems => FoldersExported + TotalLinks;
}

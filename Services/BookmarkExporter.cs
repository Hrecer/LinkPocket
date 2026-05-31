using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace LinkPocket.Services;

public class BookmarkExporter
{
    private readonly LinkPocketDbContext _db;

    public BookmarkExporter(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task ExportAsync(string outputPath, IProgress<(string message, int current, int total)>? progress = null)
    {
        progress?.Report(("正在获取书签数据...", 0, 1));

        var rootFolders = await _db.Folders
            .Where(f => f.ParentId == null || f.ParentId == "0")
            .OrderBy(f => f.Name)
            .ToListAsync();

        var rootLinks = await _db.Links
            .Where(l => l.ListId == null || l.ListId == "0")
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        var totalItems = rootLinks.Count;
        var folderQueue = new Queue<string>(rootFolders.Select(f => f.FolderId));
        var allFolderIds = new List<string>(folderQueue);

        while (folderQueue.Count > 0)
        {
            var fid = folderQueue.Dequeue();
            var children = await _db.Folders
                .Where(f => f.ParentId == fid)
                .OrderBy(f => f.Name)
                .ToListAsync();
            foreach (var child in children)
            {
                allFolderIds.Add(child.FolderId);
                folderQueue.Enqueue(child.FolderId);
            }
            var linkCount = await _db.Links.CountAsync(l => l.ListId == fid);
            totalItems += linkCount;
        }

        var counter = new ProgressCounter { Value = 0 };

        using var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);

        await writer.WriteLineAsync("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        await writer.WriteLineAsync("<!-- This is an automatically generated file.");
        await writer.WriteLineAsync("     It will be read and overwritten. Do NOT edit! -->");
        await writer.WriteLineAsync("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        await writer.WriteLineAsync("<TITLE>Bookmarks</TITLE>");
        await writer.WriteLineAsync("<H1>Bookmarks</H1>");
        await writer.WriteLineAsync("<DL><p>");

        foreach (var link in rootLinks)
        {
            WriteLink(writer, link);
            counter.Value++;
            progress?.Report(("正在导出根目录书签...", counter.Value, totalItems));
        }

        foreach (var folder in rootFolders)
        {
            await WriteFolderAsync(writer, folder, progress, counter, totalItems);
        }

        await writer.WriteLineAsync("</DL><p>");

        progress?.Report(("导出完成", counter.Value, totalItems));
    }

    private async Task WriteFolderAsync(StreamWriter writer, Folder folder,
        IProgress<(string message, int current, int total)>? progress, ProgressCounter counter, int total)
    {
        var folders = await _db.Folders
            .Where(f => f.ParentId == folder.FolderId)
            .OrderBy(f => f.Name)
            .ToListAsync();

        var links = await _db.Links
            .Where(l => l.ListId == folder.FolderId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        var name = EscapeHtml(folder.Name ?? "未命名文件夹");
        await writer.WriteLineAsync($"  <DT><H3>{name}</H3>");
        await writer.WriteLineAsync("  <DL><p>");

        foreach (var link in links)
        {
            WriteLink(writer, link);
            counter.Value++;
            progress?.Report(($"正在导出: {name}", counter.Value, total));
        }

        foreach (var child in folders)
        {
            await WriteFolderAsync(writer, child, progress, counter, total);
        }

        await writer.WriteLineAsync("  </DL><p>");
    }

    private static void WriteLink(StreamWriter writer, Link link)
    {
        var url = EscapeHtml(link.Url ?? "");
        var title = EscapeHtml(link.Title ?? link.Url ?? "无标题");
        var addDate = new DateTimeOffset(link.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds();

        writer.Write($"  <DT><A HREF=\"{url}\" ADD_DATE=\"{addDate}\"");

        if (!string.IsNullOrWhiteSpace(link.FaviconUrl))
        {
            var icon = EscapeHtml(link.FaviconUrl);
            writer.Write($" ICON=\"{icon}\"");
        }

        writer.WriteLine($">{title}</A>");
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    public class ProgressCounter
    {
        public int Value { get; set; }
    }
}
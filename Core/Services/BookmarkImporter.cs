using System.Text.RegularExpressions;
using LinkPocket.Data;

namespace LinkPocket.Services;

public class BookmarkImporter
{
    private readonly LinkPocketDbContext _db;

    public BookmarkImporter(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<ImportResult> ImportAsync(string filePath, IProgress<(string message, int current, int total)>? progress = null)
    {
        var html = await System.IO.File.ReadAllTextAsync(filePath);
        var result = new ImportResult();

        progress?.Report(("正在解析文件...", 0, 1));

        var rootContent = ExtractRootDlContent(html);
        if (rootContent == null)
        {
            result.Errors.Add("未找到有效的书签数据 (<DL>)");
            return result;
        }
        var items = new List<ParsedItem>();
        ParseEntries(rootContent, null, items);

        var total = items.Count(i => !i.IsSkipped);
        var done = 0;

        progress?.Report(("正在导入...", 0, total));

        foreach (var item in items)
        {
            if (item.IsSkipped) continue;

            if (item.IsFolder)
            {
                string? realParentId = null;
                if (item.TempParentId != null)
                {
                    var parentFolder = items.FirstOrDefault(i => i.IsFolder && i.TempId == item.TempParentId);
                    realParentId = parentFolder?.GeneratedId;
                }

                var folder = new Folder
                {
                    Name = item.Title,
                    Description = null,
                    ParentId = realParentId,
                    LinkCount = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Folders.Add(folder);
                await _db.SaveChangesAsync();

                item.GeneratedId = folder.FolderId;

                result.FoldersCreated++;
            }
            else
            {
                string? realListId = null;
                if (item.TempParentId != null)
                {
                    var parentFolder = items.FirstOrDefault(i => i.IsFolder && i.TempId == item.TempParentId);
                    realListId = parentFolder?.GeneratedId;
                }

                var link = new Link
                {
                    Url = item.Url ?? string.Empty,
                    Title = item.Title,
                    Description = null,
                    FaviconUrl = item.IconUrl,
                    ListId = realListId,
                    VisitCount = 0,
                    IsImportant = false,
                    CreatedAt = item.AddDate ?? DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Links.Add(link);
                result.LinksCreated++;
            }

            done++;
            progress?.Report(($"正在导入: {item.Title}", done, total));
        }

        await _db.SaveChangesAsync();
        progress?.Report(("导入完成", done, total));

        return result;
    }

    private static string? ExtractRootDlContent(string html)
    {
        var startMatch = Regex.Match(html, @"<DL[^>]*>", RegexOptions.IgnoreCase);
        if (!startMatch.Success) return null;

        var start = startMatch.Index + startMatch.Length;
        var depth = 1;
        var pos = start;

        while (depth > 0 && pos < html.Length)
        {
            var openTag = Regex.Match(html.Substring(pos), @"<DL[>\s]", RegexOptions.IgnoreCase);
            var closeTag = Regex.Match(html.Substring(pos), @"</DL>", RegexOptions.IgnoreCase);

            if (!openTag.Success && !closeTag.Success)
                break;

            if (closeTag.Success && (!openTag.Success || closeTag.Index < openTag.Index))
            {
                depth--;
                pos += closeTag.Index + 5;
            }
            else if (openTag.Success)
            {
                depth++;
                pos += openTag.Index + openTag.Length;
            }
        }

        if (depth != 0) return null;

        return html.Substring(start, pos - start - 5);
    }

    private static string? ExtractInnerDl(string afterH3, out int innerStart, out int innerEnd)
    {
        innerStart = -1;
        innerEnd = -1;

        var startMatch = Regex.Match(afterH3, @"<DL[^>]*>", RegexOptions.IgnoreCase);
        if (!startMatch.Success) return null;

        innerStart = startMatch.Index;
        var start = startMatch.Index + startMatch.Length;
        var depth = 1;
        var pos = start;

        while (depth > 0 && pos < afterH3.Length)
        {
            var openTag = Regex.Match(afterH3.Substring(pos), @"<DL[>\s]", RegexOptions.IgnoreCase);
            var closeTag = Regex.Match(afterH3.Substring(pos), @"</DL>", RegexOptions.IgnoreCase);

            if (!openTag.Success && !closeTag.Success)
                break;

            if (closeTag.Success && (!openTag.Success || closeTag.Index < openTag.Index))
            {
                depth--;
                if (depth == 0)
                {
                    innerEnd = pos + closeTag.Index + closeTag.Length;
                    return afterH3.Substring(start, pos + closeTag.Index - start);
                }
                pos += closeTag.Index + closeTag.Length;
            }
            else if (openTag.Success)
            {
                depth++;
                pos += openTag.Index + openTag.Length;
            }
        }

        return null;
    }

    private void ParseEntries(string dlContent, string? parentTempId, List<ParsedItem> items)
    {
        var pos = 0;
        while (pos < dlContent.Length)
        {
            var dtMatch = Regex.Match(dlContent.Substring(pos), @"<DT\b[^>]*>", RegexOptions.IgnoreCase);
            if (!dtMatch.Success) break;

            var entryStart = pos + dtMatch.Index + dtMatch.Length;
            var rest = dlContent.Substring(entryStart);

            var folderMatch = Regex.Match(rest, @"^[\s\r\n]*(<H3\b[^>]*>.*?</H3>)", RegexOptions.IgnoreCase);
            if (folderMatch.Success)
            {
                var h3Tag = folderMatch.Groups[1].Value;
                var folderTitle = StripHtml(Regex.Match(h3Tag, @"<H3\b[^>]*>(.*?)</H3>", RegexOptions.IgnoreCase).Groups[1].Value);
                var tempId = Guid.NewGuid().ToString("N");

                var afterH3 = rest.Substring(folderMatch.Index + folderMatch.Length);
                ExtractInnerDl(afterH3, out var dlStart, out var dlEnd);

                items.Add(new ParsedItem
                {
                    IsFolder = true,
                    Title = folderTitle,
                    TempId = tempId,
                    ParentId = parentTempId,
                    TempParentId = parentTempId
                });

                if (dlEnd > 0)
                {
                    var innerContent = afterH3.Substring(dlStart, dlEnd - dlStart);
                    ParseEntries(innerContent, tempId, items);
                    pos = entryStart + folderMatch.Index + folderMatch.Length + dlEnd;
                }
                else
                {
                    pos = entryStart + folderMatch.Index + folderMatch.Length;
                }
                continue;
            }

            var linkMatch = Regex.Match(rest, @"^[\s\r\n]*(<A\b[^>]*HREF\s*=\s*""([^""]*)""[^>]*>.*?</A>)", RegexOptions.IgnoreCase);
            if (linkMatch.Success)
            {
                var fullTag = linkMatch.Groups[1].Value;
                var url = linkMatch.Groups[2].Value;

                if (!string.IsNullOrWhiteSpace(url) && url != "about:blank")
                {
                    var title = StripHtml(Regex.Match(fullTag, @"<A\b[^>]*>(.*?)</A>", RegexOptions.IgnoreCase).Groups[1].Value);

                    DateTime? addDate = null;
                    var dateMatch = Regex.Match(fullTag, @"ADD_DATE\s*=\s*""?(\d+)""?", RegexOptions.IgnoreCase);
                    if (dateMatch.Success && long.TryParse(dateMatch.Groups[1].Value, out var ts))
                        addDate = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

                    string? iconUrl = null;
                    var iconMatch = Regex.Match(fullTag, @"ICON\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
                    if (iconMatch.Success)
                        iconUrl = iconMatch.Groups[1].Value;

                    items.Add(new ParsedItem
                    {
                        IsFolder = false,
                        Title = string.IsNullOrEmpty(title) ? url : title,
                        Url = url,
                        IconUrl = iconUrl,
                        AddDate = addDate,
                        ParentId = parentTempId,
                        TempParentId = parentTempId,
                        TempId = Guid.NewGuid().ToString("N")
                    });
                }

                pos = entryStart + linkMatch.Index + linkMatch.Length;
                continue;
            }

            pos = entryStart + 1;
        }
    }

    private static string StripHtml(string text)
    {
        return Regex.Replace(text, @"<[^>]+>", "").Trim();
    }

    private class ParsedItem
    {
        public bool IsFolder { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? IconUrl { get; set; }
        public DateTime? AddDate { get; set; }
        public string? ParentId { get; set; }
        public string? TempParentId { get; set; }
        public string TempId { get; set; } = string.Empty;
        public string? GeneratedId { get; set; }
        public bool IsSkipped => string.IsNullOrWhiteSpace(Title) && !IsFolder;
    }

    public class ImportResult
    {
        public int FoldersCreated { get; set; }
        public int LinksCreated { get; set; }
        public List<string> Errors { get; set; } = new();
        public int TotalItems => FoldersCreated + LinksCreated;
        public bool Success => Errors.Count == 0;
    }
}
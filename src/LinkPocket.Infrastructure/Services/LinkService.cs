using LinkPocket.Data;
using LinkPocket.Api;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LinkPocket.Services;

public class LinkService
{
    private LinkPocketDbContext _db;

    public LinkService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public void SetDb(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<(List<Link> Links, int TotalCount, int CurrentPage, int LastPage)> GetLinksAsync(
        string? search = null,
        string? listId = null,
        bool? isImportant = null,
        string? dateFrom = null,
        string? dateTo = null,
        string sortBy = "created_at",
        string sortOrder = "desc",
        int page = 1,
        int perPage = 20)
    {
        IQueryable<Link> query = _db.Links
            .Include(l => l.Folder);

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(l =>
                l.Url.Contains(search) ||
                (l.Title != null && l.Title.Contains(search)) ||
                (l.Description != null && l.Description.Contains(search)));
        }

        if (!string.IsNullOrEmpty(listId))
        {
            query = query.Where(l => l.ListId == listId);
        }

        if (isImportant.HasValue)
        {
            query = query.Where(l => l.IsImportant == isImportant);
        }

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var from))
        {
            query = query.Where(l => l.CreatedAt >= from);
        }

        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var to))
        {
            query = query.Where(l => l.CreatedAt <= to);
        }

        var allowedSortFields = new[] { "created_at", "updated_at", "last_visited_at", "visit_count", "title" };
        var field = allowedSortFields.Contains(sortBy) ? sortBy : "created_at";
        var order = sortOrder == "asc" ? "asc" : "desc";

        query = (field, order) switch
        {
            ("created_at", "asc") => query.OrderBy(l => l.CreatedAt),
            ("updated_at", "asc") => query.OrderBy(l => l.UpdatedAt),
            // 从未查看（null）恒排最后，与文件夹侧同口径
            ("last_visited_at", "asc") => query.OrderBy(l => l.LastVisitedAt == null).ThenBy(l => l.LastVisitedAt),
            ("visit_count", "asc") => query.OrderBy(l => l.VisitCount),
            ("title", "asc") => query.OrderBy(l => l.Title),
            ("updated_at", _) => query.OrderByDescending(l => l.UpdatedAt),
            ("last_visited_at", _) => query.OrderBy(l => l.LastVisitedAt == null).ThenByDescending(l => l.LastVisitedAt),
            ("visit_count", _) => query.OrderByDescending(l => l.VisitCount),
            ("title", _) => query.OrderByDescending(l => l.Title),
            _ => query.OrderByDescending(l => l.CreatedAt)
        };

        var totalCount = await query.CountAsync();
        var lastPage = (int)Math.Ceiling((double)totalCount / perPage);

        var links = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync();

        return (links, totalCount, page, lastPage);
    }

    public async Task<int> GetTotalCountAsync()
    {
        return await _db.Links.CountAsync();
    }

    public async Task<Dictionary<string, int>> GetLinkCountByFolderAsync()
    {
        return await _db.Links
            .Where(l => l.ListId != null)
            .GroupBy(l => l.ListId!)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    public async Task<int> GetRootLevelLinkCountAsync()
    {
        return await _db.Links
            .Where(l => l.ListId == null)
            .CountAsync();
    }

    public async Task<List<Link>> GetRootLevelLinksAsync(string sortBy = "created_at", string sortOrder = "desc", int perPage = 50)
    {
        var query = _db.Links
            .Include(l => l.Folder)
            .Where(l => l.ListId == null);

        var allowedSortFields = new[] { "created_at", "updated_at", "last_visited_at", "visit_count", "title" };
        var field = allowedSortFields.Contains(sortBy) ? sortBy : "created_at";
        var order = sortOrder == "asc" ? "asc" : "desc";

        query = (field, order) switch
        {
            ("created_at", "asc") => query.OrderBy(l => l.CreatedAt),
            ("updated_at", "asc") => query.OrderBy(l => l.UpdatedAt),
            ("last_visited_at", "asc") => query.OrderBy(l => l.LastVisitedAt == null).ThenBy(l => l.LastVisitedAt),
            ("visit_count", "asc") => query.OrderBy(l => l.VisitCount),
            ("title", "asc") => query.OrderBy(l => l.Title),
            ("updated_at", _) => query.OrderByDescending(l => l.UpdatedAt),
            ("last_visited_at", _) => query.OrderBy(l => l.LastVisitedAt == null).ThenByDescending(l => l.LastVisitedAt),
            ("visit_count", _) => query.OrderByDescending(l => l.VisitCount),
            ("title", _) => query.OrderByDescending(l => l.Title),
            _ => query.OrderByDescending(l => l.CreatedAt)
        };

        return await query.Take(perPage).ToListAsync();
    }

    public async Task<List<Link>> GetAllActiveLinksAsync()
    {
        return await _db.Links
            .ToListAsync();
    }

    /// <summary>按 ID 取单条链接（不存在返回 null）。供按 ID 定位场景使用，避免取全库再筛。</summary>
    public async Task<Link?> GetActiveByIdAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return await _db.Links.FirstOrDefaultAsync(l => l.LinkId == id);
    }

    /// <summary>回收站平铺列表：只取「单独删除」的根级书签（随文件夹单元进来的在 trash_folders 树里）。</summary>
    public async Task<List<TrashedLink>> GetDeletedLinksAsync()
    {
        return await _db.TrashedLinks
            .Where(t => t.TrashFolderId == null)
            .OrderByDescending(t => t.DeletedAt)
            .ToListAsync();
    }

    public async Task<Link> CreateLinkAsync(string url, string? title = null, string? description = null,
        string? listId = null, bool isImportant = false,
        bool autoFetchMetadata = true, string? faviconUrl = null)
    {
        var link = new Link
        {
            Url = url.Trim(),
            Title = title,
            Description = description,
            ListId = FolderIds.Normalize(listId),
            IsImportant = isImportant,
            VisitCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (autoFetchMetadata && (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(description)))
        {
            try
            {
                var metadata = await FetchMetadataAsync(url);
                if (metadata != null)
                {
                    if (string.IsNullOrEmpty(link.Title) && !string.IsNullOrEmpty(metadata.Title))
                    {
                        link.Title = metadata.Title;
                    }
                    if (string.IsNullOrEmpty(link.Description) && !string.IsNullOrEmpty(metadata.Description))
                    {
                        link.Description = metadata.Description;
                    }
                    if (string.IsNullOrEmpty(link.FaviconUrl) && !string.IsNullOrEmpty(metadata.FaviconUrl))
                    {
                        link.FaviconUrl = metadata.FaviconUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"元数据抓取失败: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(faviconUrl))
            link.FaviconUrl = faviconUrl;

        _db.Links.Add(link);
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(listId))
        {
            var folder = await _db.Folders.FindAsync(listId);
            folder?.UpdateLinkCount(_db);
        }

        return link;
    }

    public async Task<Link> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null,
        bool? isImportant = null, string? faviconUrl = null)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.LinkId == id)
            ?? throw new Exception("Link not found");

        if (!string.IsNullOrEmpty(url))
        {
            link.Url = url.Trim();
        }

        if (title != null) link.Title = title;
        if (description != null) link.Description = description;
        if (listId != null) link.ListId = FolderIds.Normalize(listId);
        if (isImportant != null) link.IsImportant = isImportant.Value;
        if (faviconUrl != null) link.FaviconUrl = faviconUrl;

        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return link;
    }

    public async Task DeleteLinkAsync(string id)
    {
        var link = await _db.Links
            .FirstOrDefaultAsync(l => l.LinkId == id)
            ?? throw new Exception("Link not found");

        // Windows 式回收站：单独删除的书签挂在回收站根（TrashFolderId=null），
        // 并定格删除时的位置（origin_list_id + origin_path 快照）。
        var trashedLink = new TrashedLink
        {
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title,
            Description = link.Description,
            FaviconUrl = link.FaviconUrl,
            TrashFolderId = null,
            OriginListId = link.ListId,
            OriginPath = await OriginPath.BuildListPathAsync(_db, link.ListId),
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            IsImportant = link.IsImportant,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = link.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        _db.TrashedLinks.Add(trashedLink);
        _db.Links.Remove(link);

        await _db.SaveChangesAsync();
    }

    public async Task PermanentDeleteLinkAsync(string id)
    {
        var trashedLink = await _db.TrashedLinks
            .FirstOrDefaultAsync(t => t.LinkId == id)
            ?? throw new Exception("Link not found in trash");

        _db.TrashedLinks.Remove(trashedLink);

        await _db.SaveChangesAsync();
    }

    public async Task<Link> RestoreLinkAsync(string id)
    {
        var trashedLink = await _db.TrashedLinks
            .FirstOrDefaultAsync(t => t.LinkId == id)
            ?? throw new Exception("Link not found in trash");

        var restoredLink = new Link
        {
            LinkId = trashedLink.LinkId,
            Url = trashedLink.Url,
            Title = trashedLink.Title,
            Description = trashedLink.Description,
            FaviconUrl = trashedLink.FaviconUrl,
            ListId = null,
            LastVisitedAt = trashedLink.LastVisitedAt,
            VisitCount = trashedLink.VisitCount,
            IsImportant = trashedLink.IsImportant,
            CreatedAt = trashedLink.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Links.Add(restoredLink);
        _db.TrashedLinks.Remove(trashedLink);

        await _db.SaveChangesAsync();

        return restoredLink;
    }

    /// <summary>
    /// 记录一次「查看」：只影响访问统计（LastVisitedAt / VisitCount）。
    /// <b>不改 UpdatedAt</b>——「最后更新」严格由内容变动驱动，与文件夹口径一致；
    /// 文件夹侧的「最后查看 / 查看次数」由 <see cref="FolderService.RecordFolderViewAsync"/> 沿父链刷新，
    /// 本方法只返回链接所在文件夹 id 供调用方接力。
    /// </summary>
    public async Task<string?> RecordVisitAsync(string id)
    {
        var link = await _db.Links.FindAsync(id) ?? throw new Exception("Link not found");

        link.VisitCount++;
        link.LastVisitedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return link.ListId;
    }

    public async Task<MetadataResult?> FetchMetadataAsync(string url)
    {
        if (!IsValidUrl(url))
            throw new ArgumentException("Invalid URL format");

        try
        {
            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            });
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var html = await httpClient.GetStringAsync(url);

            var metadata = new MetadataResult();

            // Title
            var titleMatch = Regex.Match(html,
                @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }

            // Description
            var descMatch = Regex.Match(html,
                @"<meta\s+[^>]*name=[""']description[""'][^>]*content=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (!descMatch.Success)
            {
                descMatch = Regex.Match(html,
                    @"<meta\s+[^>]*content=[""'](.*?)[""'][^>]*name=[""']description[""']",
                    RegexOptions.IgnoreCase);
            }
            if (descMatch.Success)
            {
                metadata.Description = System.Net.WebUtility.HtmlDecode(descMatch.Groups[1].Value.Trim());
            }

            var ogTitleMatch = Regex.Match(html,
                @"<meta\s+[^>]*property=[""']og:title[""'][^>]*content=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (!ogTitleMatch.Success)
            {
                ogTitleMatch = Regex.Match(html,
                    @"<meta\s+[^>]*content=[""'](.*?)[""'][^>]*property=[""']og:title[""']",
                    RegexOptions.IgnoreCase);
            }
            if (!ogTitleMatch.Success)
            {
                ogTitleMatch = Regex.Match(html,
                    @"<meta\s+[^>]*(?:property|name)=[""'](?:og:title|twitter:title)[""'][^>]*content=[""'](.*?)[""']",
                    RegexOptions.IgnoreCase);
            }
            if (!ogTitleMatch.Success)
            {
                ogTitleMatch = Regex.Match(html,
                    @"<meta\s+[^>]*content=[""'](.*?)[""'][^>]*(?:property|name)=[""'](?:og:title|twitter:title)[""']",
                    RegexOptions.IgnoreCase);
            }
            if (ogTitleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value.Trim());
            }

            var ogDescMatch = Regex.Match(html,
                @"<meta\s+[^>]*property=[""']og:description[""'][^>]*content=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (!ogDescMatch.Success)
            {
                ogDescMatch = Regex.Match(html,
                    @"<meta\s+[^>]*content=[""'](.*?)[""'][^>]*property=[""']og:description[""']",
                    RegexOptions.IgnoreCase);
            }
            if (ogDescMatch.Success)
            {
                metadata.Description = System.Net.WebUtility.HtmlDecode(ogDescMatch.Groups[1].Value.Trim());
            }

            var uri = new Uri(url);
            var baseUrl = $"{uri.Scheme}://{uri.Host}";

            var faviconMatch = Regex.Match(html,
                @"<link\s+[^>]*rel=[""'](?:shortcut\s+icon|icon)[""'][^>]*href=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (!faviconMatch.Success)
            {
                faviconMatch = Regex.Match(html,
                    @"<link\s+[^>]*rel=[""'](?!apple-touch-icon)[^""']*icon[^""']*[""'][^>]*href=[""'](.*?)[""']",
                    RegexOptions.IgnoreCase);
            }
            if (faviconMatch.Success)
            {
                var faviconPath = faviconMatch.Groups[1].Value;
                string? resolvedUrl = null;
                if (faviconPath.StartsWith("http://") || faviconPath.StartsWith("https://"))
                    resolvedUrl = faviconPath;
                else if (faviconPath.StartsWith("//"))
                    resolvedUrl = $"https:{faviconPath}";
                else if (faviconPath.StartsWith("/"))
                    resolvedUrl = $"{baseUrl}{faviconPath}";
                else
                    resolvedUrl = $"{baseUrl}/{faviconPath}";

                var lowerUrl = resolvedUrl.ToLower();
                if (lowerUrl.Contains("favicon") || lowerUrl.Contains(".ico") || lowerUrl.Contains(".png") ||
                    lowerUrl.Contains(".jpg") || lowerUrl.Contains(".jpeg") || lowerUrl.Contains(".gif") ||
                    lowerUrl.Contains(".svg") || lowerUrl.Contains(".webp"))
                {
                    metadata.FaviconUrl = resolvedUrl;
                }
                else
                {
                    metadata.FaviconUrl = $"{baseUrl}/favicon.ico";
                }
            }
            else
            {
                metadata.FaviconUrl = $"{baseUrl}/favicon.ico";
            }

            return metadata;
        }
        catch (Exception ex)
        {
            Logger.Error($"FetchMetadata 异常 for {url}: {ex.Message}", ex);
            return null;
        }
    }

    private bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }

    public async Task<List<Link>> GetRecentlyAddedAsync(int days = 7, int limit = 50)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        return await _db.Links
            .Include(l => l.Folder)
            .Where(l => l.CreatedAt >= since)
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<Link>> GetRecentlyVisitedAsync(int days = 7, int limit = 50)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        return await _db.Links
            .Include(l => l.Folder)
            .Where(l => l.LastVisitedAt.HasValue && l.LastVisitedAt.Value >= since)
            .OrderByDescending(l => l.LastVisitedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<Link>> GetRecentlyEditedAsync(int days = 7, int limit = 50)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        return await _db.Links
            .Include(l => l.Folder)
            .Where(l => l.UpdatedAt >= since)
            .OrderByDescending(l => l.UpdatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<Link>> GetMostVisitedAsync(int limit = 20)
    {
        return await _db.Links
            .Include(l => l.Folder)
            .Where(l => l.VisitCount > 0)
            .OrderByDescending(l => l.VisitCount)
            .ThenByDescending(l => l.LastVisitedAt)
            .Take(limit)
            .ToListAsync();
    }
}

public class MetadataResult
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? FaviconUrl { get; set; }
}

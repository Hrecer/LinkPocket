using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LinkPocket.Services;

public class LinkService
{
    private readonly LinkPocketDbContext _db;

    public LinkService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<(List<Link> Links, int TotalCount, int CurrentPage, int LastPage)> GetLinksAsync(
        string? search = null,
        int? listId = null,
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
                (l.OriginalTitle != null && l.OriginalTitle.Contains(search)) ||
                (l.Description != null && l.Description.Contains(search)));
        }

        if (listId.HasValue)
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
        if (!allowedSortFields.Contains(sortBy)) sortBy = "created_at";

        sortOrder = sortOrder.ToLower() == "asc" ? "asc" : "desc";

        query = sortBy switch
        {
            "created_at" => sortOrder == "asc" ? query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id) : query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id),
            "updated_at" => sortOrder == "asc" ? query.OrderBy(l => l.UpdatedAt).ThenBy(l => l.Id) : query.OrderByDescending(l => l.UpdatedAt).ThenBy(l => l.Id),
            "last_visited_at" => sortOrder == "asc" ? query.OrderBy(l => l.LastVisitedAt).ThenBy(l => l.Id) : query.OrderByDescending(l => l.LastVisitedAt).ThenBy(l => l.Id),
            "visit_count" => sortOrder == "asc" ? query.OrderBy(l => l.VisitCount).ThenBy(l => l.Id) : query.OrderByDescending(l => l.VisitCount).ThenBy(l => l.Id),
            "title" => sortOrder == "asc" ? query.OrderBy(l => l.Title).ThenBy(l => l.Id) : query.OrderByDescending(l => l.Title).ThenBy(l => l.Id),
            _ => query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
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

    public async Task<Dictionary<int, int>> GetLinkCountByFolderAsync()
    {
        return await _db.Links
            .Where(l => l.ListId != null)
            .GroupBy(l => l.ListId!.Value)
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
            ("last_visited_at", "asc") => query.OrderBy(l => l.LastVisitedAt),
            ("visit_count", "asc") => query.OrderBy(l => l.VisitCount),
            ("title", "asc") => query.OrderBy(l => l.Title),
            ("updated_at", _) => query.OrderByDescending(l => l.UpdatedAt),
            ("last_visited_at", _) => query.OrderByDescending(l => l.LastVisitedAt),
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

    public async Task<List<TrashedLink>> GetDeletedLinksAsync()
    {
        return await _db.TrashedLinks
            .OrderByDescending(t => t.DeletedAt)
            .ToListAsync();
    }

    public async Task<TrashedLink?> GetTrashedLinkByIdAsync(int id)
    {
        return await _db.TrashedLinks
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Link?> GetLinkByIdAsync(int id)
    {
        return await _db.Links
            .Include(l => l.Folder)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    public async Task<Link> CreateLinkAsync(string url, string? title = null, string? description = null,
        int? listId = null, bool isImportant = false,
        bool autoFetchMetadata = true, string? faviconUrl = null)
    {
        var link = new Link
        {
            Url = url.Trim(),
            Title = title,
            OriginalTitle = title,
            Description = description,
            ListId = listId,
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
                        link.OriginalTitle = metadata.Title;
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

        if (listId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(listId);
            folder?.UpdateLinkCount(_db);
        }

        return link;
    }

    public async Task<Link> UpdateLinkAsync(int id, string? url = null, string? title = null,
        string? description = null, int? listId = null,
        bool? isImportant = null, string? faviconUrl = null)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new Exception("Link not found");

        if (!string.IsNullOrEmpty(url))
        {
            link.Url = url.Trim();
        }

        if (title != null) link.Title = title;
        if (description != null) link.Description = description;
        if (listId.HasValue) link.ListId = listId.Value == 0 ? null : listId.Value;
        if (isImportant != null) link.IsImportant = isImportant.Value;
        if (faviconUrl != null) link.FaviconUrl = faviconUrl;

        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return link;
    }

    public async Task DeleteLinkAsync(int id)
    {
        var link = await _db.Links
            .FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new Exception("Link not found");

        var trashedLink = new TrashedLink
        {
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title,
            OriginalTitle = link.OriginalTitle,
            Description = link.Description,
            FaviconUrl = link.FaviconUrl,
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

    public async Task PermanentDeleteLinkAsync(int id)
    {
        var trashedLink = await _db.TrashedLinks
            .FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new Exception("Link not found in trash");

        _db.TrashedLinks.Remove(trashedLink);

        await _db.SaveChangesAsync();
    }

    public async Task<Link> RestoreLinkAsync(int id)
    {
        var trashedLink = await _db.TrashedLinks
            .FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new Exception("Link not found in trash");

        var restoredLink = new Link
        {
            LinkId = trashedLink.LinkId,
            Url = trashedLink.Url,
            Title = trashedLink.Title,
            OriginalTitle = trashedLink.OriginalTitle,
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

    public async Task RecordVisitAsync(int id)
    {
        var link = await _db.Links.FindAsync(id) ?? throw new Exception("Link not found");

        link.VisitCount++;
        link.LastVisitedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;

        if (link.ListId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(link.ListId);
            if (folder != null)
            {
                folder.LastVisitedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
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
                MaxAutomaticRedirections = 5,
                UseCookies = false
            });
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Info($"FetchMetadata HTTP {response.StatusCode} for {url}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync();
            Logger.Info($"FetchMetadata 成功获取HTML, 长度: {html.Length}, URL: {url}");
            var metadata = new MetadataResult();

            var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }

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
            if (ogTitleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value.Trim());
            }

            var ogDescMatch = Regex.Match(html,
                @"<meta\s+[^>]*property=[""']og:description[""'][^>]*content=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (ogDescMatch.Success)
            {
                metadata.Description = System.Net.WebUtility.HtmlDecode(ogDescMatch.Groups[1].Value.Trim());
            }

            var uri = new Uri(url);
            var baseUrl = $"{uri.Scheme}://{uri.Host}";

            var faviconMatch = Regex.Match(html,
                @"<link\s+[^>]*rel=[""'].*icon.*[""'][^>]*href=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
            if (faviconMatch.Success)
            {
                var faviconPath = faviconMatch.Groups[1].Value;
                if (faviconPath.StartsWith("http://") || faviconPath.StartsWith("https://"))
                    metadata.FaviconUrl = faviconPath;
                else if (faviconPath.StartsWith("//"))
                    metadata.FaviconUrl = $"https:{faviconPath}";
                else if (faviconPath.StartsWith("/"))
                    metadata.FaviconUrl = $"{baseUrl}{faviconPath}";
                else
                    metadata.FaviconUrl = $"{baseUrl}/{faviconPath}";
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

    public async Task<(bool IsDuplicate, List<Link> Duplicates)> CheckDuplicateAsync(string url)
    {
        var normalizedUrl = url.ToLower().Trim().TrimEnd('/');
        normalizedUrl = Regex.Replace(normalizedUrl, @"^(https?://)(www\.)", "$1");

        var duplicates = await _db.Links
            .Where(l => l.Url == normalizedUrl || l.Url.StartsWith(normalizedUrl + "/"))
            .ToListAsync();

        return (duplicates.Count > 0, duplicates);
    }

    public async Task<(List<Link> Results, int TotalCount, int CurrentPage, int LastPage)> SearchAsync(
        string query, int page = 1, int perPage = 20)
    {
        var searchQuery = _db.Links
            .Include(l => l.Folder)
            .Where(l => l.Url.Contains(query) ||
                    (l.Title != null && l.Title.Contains(query)) ||
                    (l.OriginalTitle != null && l.OriginalTitle.Contains(query)) ||
                    (l.Description != null && l.Description.Contains(query)))
            .OrderByDescending(l => l.CreatedAt);

        var totalCount = await searchQuery.CountAsync();
        var lastPage = (int)Math.Ceiling((double)totalCount / perPage);

        var results = await searchQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync();

        return (results, totalCount, page, lastPage);
    }

    public async Task<List<Link>> GetSmartListAsync(string type, int page = 1, int perPage = 20)
    {
        IQueryable<Link> query = _db.Links
            .Include(l => l.Folder);

        query = type switch
        {
            "recently-added" => query.Where(l => l.CreatedAt >= DateTime.UtcNow.AddDays(-7))
                                     .OrderByDescending(l => l.CreatedAt),
            "recently-visited" => query.Where(l => l.LastVisitedAt.HasValue && l.LastVisitedAt.Value >= DateTime.UtcNow.AddDays(-7))
                                       .OrderByDescending(l => l.LastVisitedAt),
            "most-visited" => query.Where(l => l.VisitCount > 0)
                                   .OrderByDescending(l => l.VisitCount),
            "important" => query.Where(l => l.IsImportant)
                               .OrderByDescending(l => l.UpdatedAt),
            "trash" => _db.TrashedLinks
                       .OrderByDescending(t => t.DeletedAt)
                       .Select(t => new Link
                       {
                           Id = t.Id,
                           LinkId = t.LinkId,
                           Url = t.Url,
                           Title = t.Title,
                           OriginalTitle = t.OriginalTitle,
                           Description = t.Description,
                           FaviconUrl = t.FaviconUrl,
                           ListId = null,
                           LastVisitedAt = t.LastVisitedAt,
                           VisitCount = t.VisitCount,
                           IsImportant = t.IsImportant,
                           CreatedAt = t.CreatedAt,
                           UpdatedAt = t.UpdatedAt
                       }),
            _ => throw new ArgumentException($"Invalid smart list type: {type}")
        };

        return await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync();
    }

    private bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }
}

public class MetadataResult
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? FaviconUrl { get; set; }
}
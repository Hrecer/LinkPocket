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
        int? tagId = null,
        bool? isImportant = null,
        int? minRating = null,
        int? maxRating = null,
        bool isDeleted = false,
        string? dateFrom = null,
        string? dateTo = null,
        string sortBy = "created_at",
        string sortOrder = "desc",
        int page = 1,
        int perPage = 20)
    {
        var query = _db.Links
            .Include(l => l.Tags)
            .Include(l => l.Folder)
            .Include(l => l.Notes)
            .Where(l => l.IsDeleted == isDeleted);

        // 搜索关键词
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(l => 
                l.Url.Contains(search) ||
                (l.Title != null && l.Title.Contains(search)) ||
                (l.OriginalTitle != null && l.OriginalTitle.Contains(search)) ||
                (l.Description != null && l.Description.Contains(search)));
        }

        // 按列表筛选
        if (listId.HasValue)
        {
            query = query.Where(l => l.ListId == listId);
        }

        // 按标签筛选
        if (tagId.HasValue)
        {
            query = query.Where(l => l.Tags.Any(t => t.Id == tagId));
        }

        // 按星标筛选
        if (isImportant.HasValue)
        {
            query = query.Where(l => l.IsImportant == isImportant);
        }

        // 按评分范围筛选
        if (minRating.HasValue)
        {
            query = query.Where(l => l.Rating >= minRating);
        }
        if (maxRating.HasValue)
        {
            query = query.Where(l => l.Rating <= maxRating);
        }

        // 时间范围筛选
        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var from))
        {
            query = query.Where(l => l.CreatedAt >= from);
        }
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var to))
        {
            query = query.Where(l => l.CreatedAt <= to);
        }

        // 排序
        var allowedSortFields = new[] { "created_at", "updated_at", "last_visited_at", "visit_count", "title", "rating" };
        if (!allowedSortFields.Contains(sortBy)) sortBy = "created_at";
        
        sortOrder = sortOrder.ToLower() == "asc" ? "asc" : "desc";

        query = sortBy switch
        {
            "created_at" => sortOrder == "asc" ? query.OrderBy(l => l.CreatedAt) : query.OrderByDescending(l => l.CreatedAt),
            "updated_at" => sortOrder == "asc" ? query.OrderBy(l => l.UpdatedAt) : query.OrderByDescending(l => l.UpdatedAt),
            "last_visited_at" => sortOrder == "asc" ? query.OrderBy(l => l.LastVisitedAt) : query.OrderByDescending(l => l.LastVisitedAt),
            "visit_count" => sortOrder == "asc" ? query.OrderBy(l => l.VisitCount) : query.OrderByDescending(l => l.VisitCount),
            "title" => sortOrder == "asc" ? query.OrderBy(l => l.Title) : query.OrderByDescending(l => l.Title),
            "rating" => sortOrder == "asc" ? query.OrderBy(l => l.Rating) : query.OrderByDescending(l => l.Rating),
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
        return await _db.Links.CountAsync(l => !l.IsDeleted);
    }

    public async Task<Link?> GetLinkByIdAsync(int id)
    {
        return await _db.Links
            .Include(l => l.Tags)
            .Include(l => l.Folder)
            .Include(l => l.Notes.OrderByDescending(n => n.CreatedAt))
            .FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted);
    }

    public async Task<Link> CreateLinkAsync(string url, string? title = null, string? description = null,
        int? listId = null, List<int>? tagIds = null, int rating = 0, bool isImportant = false,
        bool autoFetchMetadata = true, string? faviconUrl = null)
    {
        var link = new Link
        {
            Url = url.Trim(),
            Title = title,
            OriginalTitle = title,
            Description = description,
            ListId = listId,
            Rating = rating,
            IsImportant = isImportant,
            VisitCount = 0,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 自动抓取元数据（如果需要）
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

        // 关联标签
        if (tagIds != null && tagIds.Any())
        {
            var tags = await _db.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync();
            link.Tags = tags;
            await _db.SaveChangesAsync();
        }

        // 更新文件夹链接计数
        if (listId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(listId);
            folder?.UpdateLinkCount(_db);
        }

        return link;
    }

    public async Task<Link> UpdateLinkAsync(int id, string? url = null, string? title = null,
        string? description = null, int? listId = null, List<int>? tagIds = null,
        int? starRating = null, bool? isImportant = null, string? faviconUrl = null)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted)
            ?? throw new Exception("Link not found");

        if (!string.IsNullOrEmpty(url))
        {
            link.Url = url.Trim();
        }

        if (title != null) link.Title = title;
        if (description != null) link.Description = description;
        if (listId != null) link.ListId = listId;
        if (starRating != null)
        {
            if (starRating < 0 || starRating > 5)
                throw new ArgumentException("Rating must be between 0 and 5");
            link.Rating = starRating.Value;
        }
        if (isImportant != null) link.IsImportant = isImportant.Value;
        if (faviconUrl != null) link.FaviconUrl = faviconUrl;

        link.UpdatedAt = DateTime.UtcNow;

        // 更新标签关联
        if (tagIds != null)
        {
            link.Tags.Clear();
            var tags = await _db.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync();
            foreach (var tag in tags)
            {
                link.Tags.Add(tag);
            }
        }

        await _db.SaveChangesAsync();

        // 重新加载导航属性
        await _db.Entry(link).Collection(l => l.Tags).Query().LoadAsync();

        return link;
    }

    public async Task DeleteLinkAsync(int id)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted)
            ?? throw new Exception("Link not found");

        link.IsDeleted = true;
        link.DeletedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // 更新文件夹计数
        if (link.ListId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(link.ListId);
            folder?.UpdateLinkCount(_db);
        }
    }

    public async Task PermanentDeleteLinkAsync(int id)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id && l.IsDeleted)
            ?? throw new Exception("Link not found in trash");

        var listId = link.ListId;

        // 删除笔记
        var notes = await _db.Notes.Where(n => n.LinkId == id).ToListAsync();
        _db.Notes.RemoveRange(notes);

        // 解除标签关联
        link.Tags.Clear();

        // 永久删除
        _db.Links.Remove(link);
        await _db.SaveChangesAsync();

        // 更新文件夹计数
        if (listId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(listId);
            folder?.UpdateLinkCount(_db);
        }
    }

    public async Task<Link> RestoreLinkAsync(int id)
    {
        var link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id && l.IsDeleted)
            ?? throw new Exception("Link not found in trash");

        link.IsDeleted = false;
        link.DeletedAt = null;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // 更新文件夹计数
        if (link.ListId.HasValue)
        {
            var folder = await _db.Folders.FindAsync(link.ListId);
            folder?.UpdateLinkCount(_db);
        }

        return link;
    }

    public async Task RecordVisitAsync(int id)
    {
        var link = await _db.Links.FindAsync(id) ?? throw new Exception("Link not found");

        link.VisitCount++;
        link.LastVisitedAt = DateTime.UtcNow;
        link.UpdatedAt = DateTime.UtcNow;

        // 更新标签查看次数
        await _db.Entry(link).Collection(l => l.Tags).LoadAsync();
        foreach (var tag in link.Tags)
        {
            tag.RecordView();
        }

        // 更新所属文件夹的最后访问时间
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

            // 提取标题
            var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }

            // 提取meta description
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

            // 提取Open Graph标题
            var ogTitleMatch = Regex.Match(html, 
                @"<meta\s+[^>]*property=[""']og:title[""'][^>]*content=[""'](.*?)[""']", 
                RegexOptions.IgnoreCase);
            if (ogTitleMatch.Success)
            {
                metadata.Title = System.Net.WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value.Trim());
            }

            // 提取OG描述
            var ogDescMatch = Regex.Match(html, 
                @"<meta\s+[^>]*property=[""']og:description[""'][^>]*content=[""'](.*?)[""']", 
                RegexOptions.IgnoreCase);
            if (ogDescMatch.Success)
            {
                metadata.Description = System.Net.WebUtility.HtmlDecode(ogDescMatch.Groups[1].Value.Trim());
            }

            // 提取favicon
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
            .Include(l => l.Tags)
            .Include(l => l.Folder)
            .Where(l => !l.IsDeleted &&
                   (l.Url.Contains(query) ||
                    (l.Title != null && l.Title.Contains(query)) ||
                    (l.OriginalTitle != null && l.OriginalTitle.Contains(query)) ||
                    (l.Description != null && l.Description.Contains(query))))
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
            .Include(l => l.Tags)
            .Include(l => l.Folder)
            .Where(l => !l.IsDeleted);

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
            "trash" => _db.Links.Include(l => l.Tags).Include(l => l.Folder)
                       .Where(l => l.IsDeleted)
                       .OrderByDescending(l => l.DeletedAt),
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

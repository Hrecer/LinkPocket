using LinkPocket.Api;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LinkPocket.Api;

/// <summary>
/// 后端 API 的默认实现：组合 LinkService/FolderService 等服务，
/// 对外只暴露 ILinkPocketApi 契约与 DTO。运行在后端进程内；
/// 前端永远不直接接触本类，只通过通信层调用。
/// 同时实现 ILinkPocketEventSource：数据变更后推送 links.changed /
/// folders.changed / trash.changed 事件（P3）。
/// </summary>
public class LinkPocketApi : ILinkPocketApi, ILinkPocketEventSource
{
    private LinkPocketDbContext _db;
    private Services.LinkService _links;
    private Services.FolderService _folders;

    public LinkPocketApi(LinkPocketDbContext? db = null)
    {
        _db = db ?? new LinkPocketDbContext();
        _db.Database.EnsureCreated();
        _links = new Services.LinkService(_db);
        _folders = new Services.FolderService(_db);
    }

    /// <summary>过渡期兼容入口：设置页重置数据库 / 备份面板需要共享同一个 DbContext。</summary>
    public LinkPocketDbContext Db => _db;

    // —— 数据变更事件推送（P3）——

    /// <summary>数据变更事件。payload 为 JSON 字符串：{"event":"links.changed","data":{...},"at":"..."}</summary>
    public event EventHandler<string>? DataChanged;

    /// <summary>推送一条数据变更事件。事件推送失败不影响主流程。</summary>
    private void RaiseChanged(string kind, object? data = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { @event = kind, data, at = DateTime.UtcNow });
            DataChanged?.Invoke(this, payload);
        }
        catch { /* 事件序列化/订阅方异常不应中断业务操作 */ }
    }

    // —— 浏览 ——

    public async Task<FolderContentsDto> GetFolderContentsAsync(string? folderId, string sortBy = "title", string sortOrder = "asc", int page = 1, int perPage = 0)
    {
        if (page < 1) page = 1;
        if (perPage < 0) perPage = 0;
        // 未启用分页时沿用旧版上限（一次取回最多 10000 条）
        var effectivePerPage = perPage > 0 ? perPage : 10000;

        var isRoot = string.IsNullOrEmpty(folderId) || folderId == "0";
        var allFolders = await _folders.GetAllFoldersAsync();
        var counts = await _links.GetLinkCountByFolderAsync();

        var dto = new FolderContentsDto { FolderId = isRoot ? "0" : folderId, PerPage = effectivePerPage };

        if (isRoot)
        {
            dto.FolderName = "全部书签";
            dto.SubFolders = allFolders
                .Where(f => string.IsNullOrEmpty(f.ParentId))
                .Select(f => MapFolder(f, counts))
                .OrderBy(f => f.Name, StringComparer.CurrentCulture)
                .ToList();
            var (links, _, currentPage, lastPage) = await _links.GetLinksAsync(sortBy: sortBy, sortOrder: sortOrder, page: page, perPage: effectivePerPage);
            dto.Links = links.Select(MapLink).ToList();
            dto.TotalLinkCount = await _links.GetRootLevelLinkCountAsync();
            dto.CurrentPage = currentPage;
            dto.LastPage = perPage > 0 ? lastPage : 1;
            dto.Breadcrumb = new List<string> { "全部书签" };
        }
        else
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId)
                ?? throw new InvalidOperationException($"文件夹 {folderId} 不存在");
            dto.FolderName = folder.Name;
            dto.SubFolders = allFolders
                .Where(f => f.ParentId == folderId)
                .Select(f => MapFolder(f, counts))
                .OrderBy(f => f.Name, StringComparer.CurrentCulture)
                .ToList();
            var (links, _, currentPage, lastPage) = await _links.GetLinksAsync(listId: folderId, sortBy: sortBy, sortOrder: sortOrder, page: page, perPage: effectivePerPage);
            dto.Links = links.Select(MapLink).ToList();
            dto.TotalLinkCount = counts.TryGetValue(folderId ?? string.Empty, out var c) ? c : 0;
            dto.CurrentPage = currentPage;
            dto.LastPage = perPage > 0 ? lastPage : 1;
            dto.Breadcrumb = BuildBreadcrumb(folder, allFolders);
        }

        return dto;
    }

    public async Task<List<FolderDto>> GetFolderTreeAsync()
    {
        var allFolders = await _folders.GetAllFoldersAsync();
        var counts = await _links.GetLinkCountByFolderAsync();
        return allFolders
            .Select(f => MapFolder(f, counts))
            .OrderBy(f => f.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<List<string>> GetBreadcrumbAsync(string? folderId)
    {
        if (string.IsNullOrEmpty(folderId) || folderId == "0")
            return new List<string> { "全部书签" };

        var allFolders = await _folders.GetAllFoldersAsync();
        var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId);
        return folder == null ? new List<string> { "全部书签" } : BuildBreadcrumb(folder, allFolders);
    }

    // —— 文件夹管理 ——

    public async Task<FolderDto> CreateFolderAsync(string name, string? parentId)
    {
        var parent = string.IsNullOrEmpty(parentId) || parentId == "0" ? null : parentId;
        var folder = await _folders.CreateFolderAsync(name, parentId: parent);
        RaiseChanged("folders.changed", new { folder_id = folder.FolderId, parent_id = folder.ParentId });
        return new FolderDto { FolderId = folder.FolderId, Name = folder.Name, ParentId = folder.ParentId };
    }

    public async Task<FolderDto> UpdateFolderAsync(string id, string? name = null, string? description = null, string? parentId = null)
    {
        var folder = await _folders.UpdateFolderAsync(id, name, description, parentId == "0" ? null : parentId);
        RaiseChanged("folders.changed", new { folder_id = folder.FolderId, parent_id = folder.ParentId });
        return new FolderDto { FolderId = folder.FolderId, Name = folder.Name, ParentId = folder.ParentId };
    }

    public async Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null)
    {
        await _folders.DeleteFolderAsync(id, cascade, targetListId);
        RaiseChanged("folders.changed", new { folder_id = id });
        RaiseChanged("links.changed", new { folder_id = id });
    }

    public async Task MoveFolderAsync(string folderId, string? targetParentId)
    {
        await _folders.MoveFolderAsync(folderId, targetParentId == "0" ? null : targetParentId);
        RaiseChanged("folders.changed", new { folder_id = folderId, target_parent_id = targetParentId });
    }

    public async Task<string> CopyFolderAsync(string folderId, string? targetParentId)
    {
        var newId = await _folders.CopyFolderDeepAsync(folderId, targetParentId == "0" ? null : targetParentId);
        RaiseChanged("folders.changed", new { folder_id = newId, copied_from = folderId });
        return newId;
    }

    public Task<bool> WouldMoveCreateCycleAsync(string folderId, string targetParentId)
        => _folders.WouldCreateCycleAsync(folderId, targetParentId);

    public async Task UpdateSortAsync(string? parentId, List<string> itemIds)
    {
        await _folders.UpdateSortAsync(parentId == "0" ? null : parentId, itemIds);
        RaiseChanged("folders.changed", new { parent_id = parentId });
    }

    // —— 链接 ——

    public async Task<PagedLinksDto> GetLinksAsync(string? listId = null, string? search = null, bool? isImportant = null,
        string? dateFrom = null, string? dateTo = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20)
    {
        var (links, total, currentPage, lastPage) = await _links.GetLinksAsync(
            search: search, listId: listId == "0" ? null : listId, isImportant: isImportant,
            dateFrom: dateFrom, dateTo: dateTo,
            sortBy: sortBy, sortOrder: sortOrder, page: page, perPage: perPage);
        return new PagedLinksDto
        {
            Links = links.Select(MapLink).ToList(),
            TotalCount = total,
            CurrentPage = currentPage,
            LastPage = lastPage
        };
    }

    public async Task<List<LinkDto>> GetAllLinksAsync()
    {
        var links = await _links.GetAllActiveLinksAsync();
        return links.Select(MapLink).ToList();
    }

    public async Task<List<LinkDto>> GetRootLevelLinksAsync(string sortBy = "created_at", string sortOrder = "desc", int perPage = 50)
    {
        var links = await _links.GetRootLevelLinksAsync(sortBy, sortOrder, perPage);
        return links.Select(MapLink).ToList();
    }

    public async Task<LinkDto> CreateLinkAsync(string url, string? title = null, string? description = null,
        string? listId = null, bool isImportant = false, bool autoFetchMetadata = false, string? faviconUrl = null)
    {
        var link = await _links.CreateLinkAsync(url, title, description,
            listId: listId == "0" ? null : listId, isImportant: isImportant,
            autoFetchMetadata: autoFetchMetadata, faviconUrl: faviconUrl);
        RaiseChanged("links.changed", new { link_id = link.LinkId, list_id = link.ListId });
        return MapLink(link);
    }

    public async Task<LinkDto> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null, bool? isImportant = null, string? faviconUrl = null)
    {
        var link = await _links.UpdateLinkAsync(id, url, title, description, listId, isImportant, faviconUrl);
        RaiseChanged("links.changed", new { link_id = link.LinkId, list_id = link.ListId });
        return MapLink(link);
    }

    public async Task TrashLinkAsync(string id)
    {
        await _links.DeleteLinkAsync(id);
        RaiseChanged("trash.changed", new { link_id = id });
    }

    public async Task RecordVisitAsync(string id)
    {
        await _links.RecordVisitAsync(id);
        RaiseChanged("links.changed", new { link_id = id });
    }

    // —— 回收站 ——

    public async Task<List<TrashEntryDto>> GetTrashAsync()
    {
        var items = await _links.GetDeletedLinksAsync();
        return items.Select(t => new TrashEntryDto
        {
            LinkId = t.LinkId,
            Url = t.Url,
            Title = t.Title,
            Description = t.Description,
            FaviconUrl = t.FaviconUrl,
            OriginalListId = t.ListId,
            LastVisitedAt = t.LastVisitedAt,
            VisitCount = t.VisitCount,
            IsImportant = t.IsImportant,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            DeletedAt = t.DeletedAt
        }).ToList();
    }

    public async Task<LinkDto> RestoreLinkAsync(string linkId)
    {
        var dto = MapLink(await _links.RestoreLinkAsync(linkId));
        RaiseChanged("trash.changed", new { link_id = linkId });
        return dto;
    }

    public async Task PurgeLinkAsync(string linkId)
    {
        await _links.PermanentDeleteLinkAsync(linkId);
        RaiseChanged("trash.changed", new { link_id = linkId });
    }

    // —— 搜索与智能列表 ——

    public async Task<List<LinkDto>> SearchAsync(string query, bool searchTitle = true, bool searchUrl = false,
        bool searchDescription = false, bool searchPath = false,
        string sortBy = "title", string sortOrder = "asc")
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<LinkDto>();

        var links = await _links.GetAllActiveLinksAsync();
        var predicates = new List<Func<Link, bool>>();

        if (searchTitle)
            predicates.Add(l => l.Title != null && l.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (searchUrl)
            predicates.Add(l => l.Url != null && l.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (searchDescription)
            predicates.Add(l => l.Description != null && l.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (searchPath)
        {
            var allFolders = await _folders.GetAllFoldersAsync();
            var matchedFolderIds = allFolders
                .Where(f => f.Name != null && f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FolderId)
                .ToHashSet();
            if (matchedFolderIds.Count > 0)
            {
                var expanded = new HashSet<string>(matchedFolderIds);
                foreach (var fid in matchedFolderIds.ToList())
                {
                    foreach (var d in CollectDescendantIds(allFolders, fid))
                        expanded.Add(d);
                }
                predicates.Add(l => l.ListId != null && expanded.Contains(l.ListId));
            }
        }

        if (predicates.Count == 0)
            predicates.Add(l => l.Title != null && l.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

        var filtered = links.Where(l => predicates.Any(p => p(l)));
        filtered = SortLinks(filtered, sortBy, sortOrder);

        return filtered.Select(MapLink).ToList();
    }

    public async Task<List<LinkDto>> GetSmartListAsync(string kind, int limit = 50)
    {
        List<Link> items = kind switch
        {
            "recently_added" => await _links.GetRecentlyAddedAsync(limit: limit),
            "recently_visited" => await _links.GetRecentlyVisitedAsync(limit: limit),
            "recently_edited" => await _links.GetRecentlyEditedAsync(limit: limit),
            "most_visited" => await _links.GetMostVisitedAsync(limit: limit),
            _ => throw new ArgumentException($"未知智能列表类型: {kind}")
        };
        return items.Select(MapLink).ToList();
    }

    // —— 元数据与统计 ——

    public async Task<MetadataDto?> FetchMetadataAsync(string url)
    {
        var meta = await _links.FetchMetadataAsync(url);
        if (meta == null) return null;
        return new MetadataDto { Title = meta.Title, Description = meta.Description, FaviconUrl = meta.FaviconUrl };
    }

    public async Task<LinkCountsDto> GetCountsAsync()
    {
        return new LinkCountsDto
        {
            Total = await _links.GetTotalCountAsync(),
            Trash = (await _links.GetDeletedLinksAsync()).Count,
            RootLevel = await _links.GetRootLevelLinkCountAsync(),
            ByFolder = await _links.GetLinkCountByFolderAsync()
        };
    }

    // —— 导入 / 导出 ——

    public async Task<string> ExportBookmarksHtmlAsync(string outputPath)
    {
        await new Services.BookmarkExporter(_db).ExportAsync(outputPath);
        return outputPath;
    }

    public async Task<int> ImportBookmarksHtmlAsync(string filePath)
    {
        var result = await new Services.BookmarkImporter(_db).ImportAsync(filePath);
        if (!result.Success && result.TotalItems == 0)
            throw new InvalidOperationException(string.Join("; ", result.Errors));
        RaiseChanged("links.changed", new { source = "import.bookmarks_html", count = result.TotalItems });
        RaiseChanged("folders.changed", new { source = "import.bookmarks_html" });
        return result.TotalItems;
    }

    // —— .lpbackup 备份 ——

    public Task ExportBackupAsync(string outputPath)
        => new Services.LinkPocketBackupService(_db).ExportAsync(outputPath);

    public async Task<BackupImportDto> ImportBackupAsync(string filePath)
    {
        var result = await new Services.LinkPocketBackupService(_db).ImportAsync(filePath);
        return new BackupImportDto
        {
            FoldersCreated = result.FoldersCreated,
            LinksCreated = result.LinksCreated,
            TotalItems = result.TotalItems,
            Success = result.Success,
            Errors = result.Errors
        };
    }

    // —— 维护 ——

    public async Task ReinitializeDatabaseAsync(bool resetData = true)
    {
        try { _db.Database.GetDbConnection().Close(); } catch { }
        try { _db.Dispose(); } catch { }

        var dbPath = Path.Combine(AppContext.BaseDirectory, "linkpocket.db");
        var connString = $"Data Source={dbPath}";
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection(connString)); } catch { }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(200);

        if (resetData)
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var faviconDir = Path.Combine(AppContext.BaseDirectory, "favicons");
            if (Directory.Exists(faviconDir))
                Directory.Delete(faviconDir, true);
        }

        _db = new LinkPocketDbContext();
        _db.Database.EnsureCreated();
        _links.SetDb(_db);
        _folders.SetDb(_db);
    }

    // —— 内部工具 ——

    private static LinkDto MapLink(Link l) => new()
    {
        LinkId = l.LinkId,
        Url = l.Url,
        Title = l.Title ?? string.Empty,
        Description = l.Description ?? string.Empty,
        FaviconUrl = l.FaviconUrl ?? string.Empty,
        ListId = l.ListId,
        LastVisitedAt = l.LastVisitedAt,
        VisitCount = l.VisitCount,
        IsImportant = l.IsImportant,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt
    };

    private static FolderDto MapFolder(Folder f, Dictionary<string, int> counts) => new()
    {
        FolderId = f.FolderId,
        Name = f.Name,
        ParentId = f.ParentId,
        LinkCount = counts.TryGetValue(f.FolderId, out var c) ? c : 0
    };

    private static List<string> BuildBreadcrumb(Folder folder, List<Folder> allFolders)
    {
        var dict = allFolders.ToDictionary(f => f.FolderId);
        var parts = new List<string>();
        var currentId = folder.FolderId;
        for (int i = 0; i < 50 && dict.TryGetValue(currentId, out var f); i++)
        {
            parts.Insert(0, f.Name);
            currentId = f.ParentId ?? string.Empty;
        }
        return new List<string> { "全部书签" }.Concat(parts).ToList();
    }

    private static IEnumerable<string> CollectDescendantIds(List<Folder> allFolders, string folderId)
    {
        foreach (var child in allFolders.Where(f => f.ParentId == folderId))
        {
            yield return child.FolderId;
            foreach (var d in CollectDescendantIds(allFolders, child.FolderId))
                yield return d;
        }
    }

    private static IOrderedEnumerable<Link> SortLinks(IEnumerable<Link> source, string sortBy, string sortOrder) =>
        sortOrder == "asc"
            ? sortBy switch
            {
                "title" => source.OrderBy(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId),
                "updated_at" => source.OrderBy(l => l.UpdatedAt).ThenBy(l => l.LinkId),
                "last_visited_at" => source.OrderBy(l => l.LastVisitedAt ?? DateTime.MinValue).ThenBy(l => l.LinkId),
                "visit_count" => source.OrderBy(l => l.VisitCount).ThenBy(l => l.LinkId),
                _ => source.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId)
            }
            : sortBy switch
            {
                "title" => source.OrderByDescending(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId),
                "updated_at" => source.OrderByDescending(l => l.UpdatedAt).ThenBy(l => l.LinkId),
                "last_visited_at" => source.OrderByDescending(l => l.LastVisitedAt ?? DateTime.MinValue).ThenBy(l => l.LinkId),
                "visit_count" => source.OrderByDescending(l => l.VisitCount).ThenBy(l => l.LinkId),
                _ => source.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.LinkId)
            };
}

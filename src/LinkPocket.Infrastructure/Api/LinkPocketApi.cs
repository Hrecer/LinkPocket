using LinkPocket.Api;
using LinkPocket.Data;
using LinkPocket.Services;
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
        // 首次使用直接创建全新 v2 库（方案 6.2 零责任定稿：无迁移组件——
        // 旧版的哨兵迁移/补列/回收站表重建组件已随 v2 schema 全部移除）。
        // 旧格式库（有用户表无 schema_migrations 版本表）在此处被拒绝并明确报错。
        SchemaMigrator.EnsureSchema(_db.DbPath);
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

        var isRoot = FolderIds.IsRoot(folderId);
        var allFolders = await _folders.GetAllFoldersAsync();
        var directCounts = await _links.GetLinkCountByFolderAsync();
        var counts = await GetRecursiveLinkCountsAsync(allFolders, directCounts);

        var dto = new FolderContentsDto { FolderId = folderId, PerPage = effectivePerPage };

        // 子文件夹排序：与列表列头一一对应（名称 / 最后更新 / 最后查看 / 查看次数 / 创建时间），
        // 名称与各维度都遵循升/降序；「最后查看」为空的（从未）恒排最后，与链接侧口径一致。
        List<FolderDto> SortFolders(IEnumerable<Folder> source)
        {
            var mapped = source.Select(f => MapFolder(f, counts));
            var desc = sortOrder == "desc";
            var ordered = sortBy switch
            {
                "updated_at" => desc ? mapped.OrderByDescending(f => f.UpdatedAt) : mapped.OrderBy(f => f.UpdatedAt),
                "created_at" => desc ? mapped.OrderByDescending(f => f.CreatedAt) : mapped.OrderBy(f => f.CreatedAt),
                "visit_count" => desc ? mapped.OrderByDescending(f => f.VisitCount) : mapped.OrderBy(f => f.VisitCount),
                "last_visited_at" => desc
                    ? mapped.OrderBy(f => f.LastVisitedAt == null).ThenByDescending(f => f.LastVisitedAt)
                    : mapped.OrderBy(f => f.LastVisitedAt == null).ThenBy(f => f.LastVisitedAt),
                _ => desc
                    ? mapped.OrderByDescending(f => f.Name, StringComparer.CurrentCulture)
                    : mapped.OrderBy(f => f.Name, StringComparer.CurrentCulture)
            };
            return ordered.ThenBy(f => f.Name, StringComparer.CurrentCulture).ToList();
        }

        if (isRoot)
        {
            dto.FolderName = FolderIds.RootDisplayName;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == null));
            // 根目录只显示根级书签（ListId == null），而不是全库书签
            var rootLinks = await _links.GetRootLevelLinksAsync(sortBy: sortBy, sortOrder: sortOrder, perPage: effectivePerPage);
            dto.Links = rootLinks.Select(MapLink).ToList();
            dto.DirectLinkCount = await _links.GetRootLevelLinkCountAsync();
            dto.CurrentPage = 1;
            dto.LastPage = 1;
            dto.Breadcrumb = new List<string> { FolderIds.RootDisplayName };
        }
        else
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId)
                ?? throw new InvalidOperationException($"文件夹 {folderId} 不存在");
            dto.FolderName = folder.Name;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == folderId));
            var (links, _, currentPage, lastPage) = await _links.GetLinksAsync(listId: folderId, sortBy: sortBy, sortOrder: sortOrder, page: page, perPage: effectivePerPage);
            dto.Links = links.Select(MapLink).ToList();
            dto.DirectLinkCount = directCounts.TryGetValue(folderId ?? string.Empty, out var c) ? c : 0;
            dto.CurrentPage = currentPage;
            dto.LastPage = perPage > 0 ? lastPage : 1;
            dto.Breadcrumb = BuildBreadcrumb(folder, allFolders);
        }

        return dto;
    }

    public async Task<List<FolderDto>> GetFolderTreeAsync()
    {
        var allFolders = await _folders.GetAllFoldersAsync();
        var counts = await GetRecursiveLinkCountsAsync(allFolders);
        return allFolders
            .Select(f => MapFolder(f, counts))
            .OrderBy(f => f.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 按 ID 取单个文件夹。计数口径与 <see cref="GetFolderTreeAsync"/> 完全一致（递归子链接数），
    /// 保证同一文件夹在树里与在定位结果里显示的数字相同。
    /// </summary>
    public async Task<FolderDto?> GetFolderAsync(string folderId)
    {
        if (FolderIds.IsRoot(folderId)) return null;   // 根不是实体、没有 ID
        var allFolders = await _folders.GetAllFoldersAsync();
        var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId);
        if (folder == null) return null;
        var counts = await GetRecursiveLinkCountsAsync(allFolders);
        return MapFolder(folder, counts);
    }

    public async Task<List<string>> GetBreadcrumbAsync(string? folderId)
    {
        if (FolderIds.IsRoot(folderId))
            return new List<string> { FolderIds.RootDisplayName };

        var allFolders = await _folders.GetAllFoldersAsync();
        var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId);
        return folder == null ? new List<string> { FolderIds.RootDisplayName } : BuildBreadcrumb(folder, allFolders);
    }

    // —— 文件夹管理 ——

    public async Task<FolderDto> CreateFolderAsync(string name, string? parentId)
    {
        var parent = parentId;
        var folder = await _folders.CreateFolderAsync(name, parentId: parent);
        await _folders.TouchModifiedAsync(parent); // 新增子文件夹 → 父链内容有变
        RaiseChanged("folders.changed", new { folder_id = folder.FolderId, parent_id = folder.ParentId });
        return new FolderDto { FolderId = folder.FolderId, Name = folder.Name, ParentId = folder.ParentId, UpdatedAt = folder.UpdatedAt };
    }

    public async Task<FolderDto> UpdateFolderAsync(string id, string? name = null, string? description = null)
    {
        var before = (await _folders.GetAllFoldersAsync()).FirstOrDefault(f => f.FolderId == id);
        var folder = await _folders.UpdateFolderAsync(id, name, description);
        // 自身被改名 + 父链的内容构成变化（换父是 folders.move 的语义，不在此处）
        await _folders.TouchModifiedAsync(folder.FolderId);
        await _folders.TouchModifiedAsync(before?.ParentId);
        RaiseChanged("folders.changed", new { folder_id = folder.FolderId, parent_id = folder.ParentId });
        return new FolderDto { FolderId = folder.FolderId, Name = folder.Name, ParentId = folder.ParentId, UpdatedAt = folder.UpdatedAt };
    }

    public async Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null)
    {
        var before = (await _folders.GetAllFoldersAsync()).FirstOrDefault(f => f.FolderId == id);
        await _folders.DeleteFolderAsync(id, cascade, targetListId);
        // 删除（含连带移入回收站的链接）→ 原父级内容有变；move_to_list 模式下目标文件夹也变了
        await _folders.TouchModifiedAsync(before?.ParentId);
        if (cascade == "move_to_list") await _folders.TouchModifiedAsync(targetListId);
        RaiseChanged("folders.changed", new { folder_id = id });
        RaiseChanged("links.changed", new { folder_id = id });
        if (cascade == "trash_links") RaiseChanged("trash.changed", new { folder_id = id });
    }

    public async Task MoveFolderAsync(string folderId, string? targetParentId)
    {
        var before = (await _folders.GetAllFoldersAsync()).FirstOrDefault(f => f.FolderId == folderId);
        await _folders.MoveFolderAsync(folderId, targetParentId);
        await _folders.TouchModifiedAsync(before?.ParentId);
        await _folders.TouchModifiedAsync(targetParentId);
        RaiseChanged("folders.changed", new { folder_id = folderId, target_parent_id = targetParentId });
    }

    public async Task<string> CopyFolderAsync(string folderId, string? targetParentId)
    {
        var newId = await _folders.CopyFolderDeepAsync(folderId, targetParentId);
        await _folders.TouchModifiedAsync(targetParentId);
        RaiseChanged("folders.changed", new { folder_id = newId, copied_from = folderId });
        return newId;
    }

    public Task<bool> WouldMoveCreateCycleAsync(string folderId, string targetParentId)
        => _folders.WouldCreateCycleAsync(folderId, targetParentId);

    public async Task UpdateSortAsync(string? parentId, List<string> itemIds)
    {
        await _folders.UpdateSortAsync(parentId, itemIds);
        RaiseChanged("folders.changed", new { parent_id = parentId });
    }

    // —— 链接 ——

    public async Task<PagedLinksDto> GetLinksAsync(string? listId = null, string? search = null, bool? isImportant = null,
        string? dateFrom = null, string? dateTo = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20)
    {
        var (links, total, currentPage, lastPage) = await _links.GetLinksAsync(
            search: search, listId: listId, isImportant: isImportant,
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

    /// <summary>按 ID 取单条链接（不存在返回 null）：定位组件据此解析链接所属目录。</summary>
    public async Task<LinkDto?> GetLinkAsync(string linkId)
    {
        var link = await _links.GetActiveByIdAsync(linkId);
        return link == null ? null : MapLink(link);
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
            listId: listId, isImportant: isImportant,
            autoFetchMetadata: autoFetchMetadata, faviconUrl: faviconUrl);
        await _folders.TouchModifiedAsync(link.ListId); // 新增链接 → 所在文件夹内容有变
        RaiseChanged("links.changed", new { link_id = link.LinkId, list_id = link.ListId });
        return MapLink(link);
    }

    public async Task<LinkDto> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null, bool? isImportant = null, string? faviconUrl = null)
    {
        var previousListId = (await _links.GetAllActiveLinksAsync()).FirstOrDefault(l => l.LinkId == id)?.ListId;
        var link = await _links.UpdateLinkAsync(id, url, title, description, listId, isImportant, faviconUrl);
        // 链接被编辑（改名/改地址/改描述）或跨文件夹移动 → 新旧两个文件夹的内容都变了
        await _folders.TouchModifiedAsync(link.ListId);
        if (previousListId != link.ListId) await _folders.TouchModifiedAsync(previousListId);
        RaiseChanged("links.changed", new { link_id = link.LinkId, list_id = link.ListId });
        return MapLink(link);
    }

    public async Task TrashLinkAsync(string id)
    {
        var listId = (await _links.GetAllActiveLinksAsync()).FirstOrDefault(l => l.LinkId == id)?.ListId;
        await _links.DeleteLinkAsync(id);
        await _folders.TouchModifiedAsync(listId); // 链接被移入回收站 → 原文件夹内容有变
        RaiseChanged("trash.changed", new { link_id = id });
    }

    public async Task RecordVisitAsync(string id)
    {
        var listId = await _links.RecordVisitAsync(id);
        // 文件夹「最后查看 / 查看次数」：沿父链链式刷新，与「最后更新」（TouchModifiedAsync）同一套
        // 事件驱动增量口径——同一条父链原语，只是触发事件与写入字段不同。
        await _folders.RecordFolderViewAsync(listId);
        RaiseChanged("links.changed", new { link_id = id });
    }

    // —— 回收站 ——

    public async Task<List<TrashEntryDto>> GetTrashAsync()
    {
        var entries = new List<TrashEntryDto>();

        // 被删文件夹单元根（挂在回收站根的删除操作对象）
        var folderRoots = await _db.TrashedFolders.Where(f => f.ParentTrashFolderId == null).ToListAsync();
        foreach (var f in folderRoots)
        {
            entries.Add(new TrashEntryDto
            {
                Id = f.TrashFolderId,
                EntryType = "folder",
                Name = f.Name,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt
            });
        }

        // 单独删除的书签（挂在回收站根）
        var rootLinks = await _links.GetDeletedLinksAsync();
        foreach (var t in rootLinks)
        {
            entries.Add(new TrashEntryDto
            {
                Id = t.LinkId,
                EntryType = "link",
                Name = string.IsNullOrEmpty(t.Title) ? t.Url : t.Title!,
                Url = t.Url,
                FaviconUrl = t.FaviconUrl,
                OriginPath = t.OriginPath,
                DeletedAt = t.DeletedAt
            });
        }

        return entries.OrderByDescending(e => e.DeletedAt).ToList();
    }

    public async Task<List<TrashFolderDto>> GetTrashTreeAsync()
    {
        var folders = await _folders.GetAllTrashFoldersAsync();
        var links = await _db.TrashedLinks.Where(l => l.TrashFolderId != null).ToListAsync();

        // 每个单元的药丸计数 = 单元子树内的书签总数
        var result = new List<TrashFolderDto>();
        foreach (var f in folders)
        {
            var subtreeIds = new List<string> { f.TrashFolderId };
            var added = true;
            while (added)
            {
                added = false;
                foreach (var child in folders)
                {
                    if (child.ParentTrashFolderId != null
                        && subtreeIds.Contains(child.ParentTrashFolderId)
                        && !subtreeIds.Contains(child.TrashFolderId))
                    {
                        subtreeIds.Add(child.TrashFolderId);
                        added = true;
                    }
                }
            }

            result.Add(new TrashFolderDto
            {
                TrashFolderId = f.TrashFolderId,
                ParentTrashFolderId = f.ParentTrashFolderId,
                Name = f.Name,
                LinkCount = links.Count(l => l.TrashFolderId != null && subtreeIds.Contains(l.TrashFolderId)),
                DeletedAt = f.DeletedAt
            });
        }

        return result;
    }

    public async Task<LinkDto> RestoreLinkAsync(string linkId)
    {
        var dto = MapLink(await _links.RestoreLinkAsync(linkId));
        await _folders.TouchModifiedAsync(dto.ListId); // 从回收站还原 → 目标文件夹内容有变
        RaiseChanged("trash.changed", new { link_id = linkId });
        return dto;
    }

    public async Task PurgeTrashAsync(string id, bool isFolder)
    {
        if (isFolder)
            await _folders.PurgeTrashFolderSubtreeAsync(id);
        else
            await _links.PermanentDeleteLinkAsync(id);
        RaiseChanged("trash.changed", new { id });
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
            // 回收站项数 = 单独删除的书签 + 被删文件夹单元（Windows 口径：按删除操作计数）
            Trash = (await _links.GetDeletedLinksAsync()).Count
                    + await _db.TrashedFolders.CountAsync(f => f.ParentTrashFolderId == null),
            RootLevel = await _links.GetRootLevelLinkCountAsync(),
            ByFolder = await _links.GetLinkCountByFolderAsync()
        };
    }

    // —— 导入 / 导出（Netscape 书签文件格式：Chrome / Edge / Firefox 通用交换格式） ——

    /// <param name="outputFilePath">导出目标<b>文件</b>的完整路径（目录须已存在）。</param>
    public async Task<string> ExportBookmarksHtmlAsync(string outputFilePath)
    {
        var result = await new Services.BookmarkExporter(_db).ExportAsync(outputFilePath);
        Logger.Info($"[导出] 完成：文件={result.FilePath} 文件夹={result.FoldersExported} " +
                    $"书签={result.TotalLinks}（根级 {result.RootLinksExported}，其中无归属 {result.OrphanLinksExported}）" +
                    $"大小={result.FileBytes} 字节");
        return result.FilePath;
    }

    public async Task<int> ImportBookmarksHtmlAsync(string filePath)
    {
        var result = await new Services.BookmarkImporter(_db).ImportAsync(filePath);
        if (!result.Success && result.TotalItems == 0)
            throw new InvalidOperationException(string.Join("; ", result.Errors));
        await _folders.TouchAllModifiedAsync(); // 批量写入 → 所有文件夹内容均视为变动
        RaiseChanged("links.changed", new { source = "import.bookmarks_html", count = result.TotalItems });
        RaiseChanged("folders.changed", new { source = "import.bookmarks_html" });
        return result.TotalItems;
    }

    /// <summary>只读预检：识别文件格式并统计条目数，不写任何数据（导入前展示 / 导出后校验共用）。</summary>
    public async Task<BookmarkFileInspectionDto> InspectBookmarksHtmlAsync(string filePath)
    {
        var inspection = await Services.BookmarkImporter.InspectAsync(filePath);
        return new BookmarkFileInspectionDto
        {
            IsValid = inspection.IsValid,
            Error = inspection.Error,
            Format = inspection.Format,
            Warnings = inspection.Warnings,
            FolderCount = inspection.FolderCount,
            LinkCount = inspection.LinkCount,
            SkippedCount = inspection.SkippedCount,
            MaxDepth = inspection.MaxDepth,
            FileBytes = inspection.FileBytes,
            TotalItems = inspection.TotalItems
        };
    }

    // —— .lpbackup 备份 ——

    public Task ExportBackupAsync(string outputPath)
        => new Services.LinkPocketBackupService(_db).ExportAsync(outputPath);

    public async Task<BackupImportDto> ImportBackupAsync(string filePath)
    {
        var result = await new Services.LinkPocketBackupService(_db).ImportAsync(filePath);
        await _folders.TouchAllModifiedAsync(); // 备份恢复 → 所有文件夹内容均视为变动
        return new BackupImportDto
        {
            FoldersCreated = result.FoldersCreated,
            LinksCreated = result.LinksCreated,
            TotalItems = result.TotalItems,
            Success = result.Success,
            Errors = result.Errors
        };
    }

    public Task<List<TrashEntryDto>> GetTrashUnitContentsAsync(string trashFolderId)
        => _folders.GetTrashUnitContentsAsync(trashFolderId);

    // —— 维护 ——

    public async Task ReinitializeDatabaseAsync(bool resetData = true)
    {
        try { _db.Database.GetDbConnection().Close(); } catch { }
        try { _db.Dispose(); } catch { }

        var dbPath = Path.Combine(AppContext.BaseDirectory, "linkpocket.db");
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }

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
        // 库文件已被删除：EnsureSchema 的文件存在性守卫失效，此处直接重建 v2 库
        SchemaMigrator.EnsureSchema(_db.DbPath);
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
        LinkCount = counts.TryGetValue(f.FolderId, out var c) ? c : 0,
        UpdatedAt = f.UpdatedAt,
        CreatedAt = f.CreatedAt,
        LastVisitedAt = f.LastVisitedAt,
        VisitCount = f.VisitCount
    };

    /// <summary>
    /// 递归链接计数：每个文件夹 = 自身直接子链接 + 全部子孙文件夹的链接数。
    /// 文件夹数量有限（内存树遍历），代价可忽略。
    /// </summary>
    private async Task<Dictionary<string, int>> GetRecursiveLinkCountsAsync(
        List<Folder> allFolders, Dictionary<string, int>? directCounts = null)
    {
        directCounts ??= await _links.GetLinkCountByFolderAsync();
        // 子 → 父索引，用于沿父链上溯累加
        var parentOf = allFolders.ToDictionary(f => f.FolderId, f => f.ParentId ?? string.Empty);
        var totals = new Dictionary<string, int>();
        foreach (var f in allFolders)
        {
            var direct = directCounts.TryGetValue(f.FolderId, out var dc) ? dc : 0;
            // 自身及全部祖先都 +direct（祖先含子孙的链接）
            var cur = f.FolderId;
            for (int i = 0; i < 256 && !string.IsNullOrEmpty(cur); i++)
            {
                totals[cur] = totals.TryGetValue(cur, out var t) ? t + direct : direct;
                if (!parentOf.TryGetValue(cur, out var p) || p == cur) break;
                cur = p;
            }
        }
        return totals;
    }

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
        return new List<string> { FolderIds.RootDisplayName }.Concat(parts).ToList();
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

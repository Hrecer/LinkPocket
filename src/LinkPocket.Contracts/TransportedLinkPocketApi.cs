using LinkPocket.Contracts;

namespace LinkPocket.Api;

/// <summary>
/// ILinkPocketApi 的传输代理：前端使用的强类型实现，
/// 每个方法都翻译为 JSON-RPC 协议调用并经 ILinkPocketTransport 发送。
/// 传输实现换成 HTTP/WebSocket（Web 前端形态）时，本类与所有调用方代码无需改动。
/// </summary>
public class TransportedLinkPocketApi : ILinkPocketApi
{
    private readonly ILinkPocketTransport _transport;

    public TransportedLinkPocketApi(ILinkPocketTransport transport) => _transport = transport;

    // —— 浏览 ——

    public Task<FolderContentsDto> GetFolderContentsAsync(string? folderId, string sortBy = "title", string sortOrder = "asc", int page = 1, int perPage = 0)
        => _transport.InvokeAsync<FolderContentsDto>("folders.contents", new
        { folder_id = folderId, sort_by = sortBy, sort_order = sortOrder, page, per_page = perPage });

    public Task<List<FolderDto>> GetFolderTreeAsync()
        => _transport.InvokeAsync<List<FolderDto>>("folders.tree");

    public Task<FolderDto?> GetFolderAsync(string folderId)
        => _transport.InvokeAsync<FolderDto?>("folders.get", new { folder_id = folderId });

    public Task<List<string>> GetBreadcrumbAsync(string? folderId)
        => _transport.InvokeAsync<List<string>>("folders.breadcrumb", new { folder_id = folderId });

    // —— 文件夹管理 ——

    public Task<FolderDto> CreateFolderAsync(string name, string? parentId)
        => _transport.InvokeAsync<FolderDto>("folders.create", new { name, parent_id = parentId });

    public Task<FolderDto> UpdateFolderAsync(string id, string? name = null, string? description = null)
        => _transport.InvokeAsync<FolderDto>("folders.update", new { id, name, description });

    public Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null)
        => _transport.InvokeAsync<object?>("folders.delete", new { id, cascade, target_list_id = targetListId });

    public Task MoveFolderAsync(string folderId, string? targetParentId)
        => _transport.InvokeAsync<object?>("folders.move", new { folder_id = folderId, target_parent_id = targetParentId });

    public Task<string> CopyFolderAsync(string folderId, string? targetParentId)
        => _transport.InvokeAsync<string>("folders.copy", new { folder_id = folderId, target_parent_id = targetParentId });

    public Task<bool> WouldMoveCreateCycleAsync(string folderId, string targetParentId)
        => _transport.InvokeAsync<bool>("folders.would_create_cycle", new { folder_id = folderId, target_parent_id = targetParentId });

    public Task UpdateSortAsync(string? parentId, List<string> itemIds)
        => _transport.InvokeAsync<object?>("folders.update_sort", new { parent_id = parentId, item_ids = itemIds });

    // —— 链接 ——

    public Task<PagedLinksDto> GetLinksAsync(string? listId = null, string? search = null, bool? isImportant = null,
        string? dateFrom = null, string? dateTo = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20)
        => _transport.InvokeAsync<PagedLinksDto>("links.list", new
        {
            list_id = listId, search, is_important = isImportant,
            date_from = dateFrom, date_to = dateTo,
            sort_by = sortBy, sort_order = sortOrder, page, per_page = perPage
        });

    public Task<List<LinkDto>> GetAllLinksAsync()
        => _transport.InvokeAsync<List<LinkDto>>("links.all");

    public Task<LinkDto?> GetLinkAsync(string linkId)
        => _transport.InvokeAsync<LinkDto?>("links.get", new { id = linkId });

    public Task<List<LinkDto>> GetRootLevelLinksAsync(string sortBy = "created_at", string sortOrder = "desc", int perPage = 50)
        => _transport.InvokeAsync<List<LinkDto>>("links.root", new { sort_by = sortBy, sort_order = sortOrder, per_page = perPage });

    public Task<LinkDto> CreateLinkAsync(string url, string? title = null, string? description = null,
        string? listId = null, bool isImportant = false, bool autoFetchMetadata = false, string? faviconUrl = null)
        => _transport.InvokeAsync<LinkDto>("links.create", new
        { url, title, description, list_id = listId, is_important = isImportant, auto_fetch_metadata = autoFetchMetadata, favicon_url = faviconUrl });

    public Task<LinkDto> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null, bool? isImportant = null, string? faviconUrl = null)
        => _transport.InvokeAsync<LinkDto>("links.update", new
        { id, url, title, description, list_id = listId, is_important = isImportant, favicon_url = faviconUrl });

    public Task TrashLinkAsync(string id)
        => _transport.InvokeAsync<object?>("links.trash", new { id });

    public Task RecordVisitAsync(string id)
        => _transport.InvokeAsync<object?>("links.record_visit", new { id });

    // —— 回收站 ——

    public Task<List<TrashEntryDto>> GetTrashAsync()
        => _transport.InvokeAsync<List<TrashEntryDto>>("trash.list");

    public Task<List<TrashFolderDto>> GetTrashTreeAsync()
        => _transport.InvokeAsync<List<TrashFolderDto>>("trash.tree");

    public Task<LinkDto> RestoreLinkAsync(string linkId)
        => _transport.InvokeAsync<LinkDto>("trash.restore", new { link_id = linkId });

    public Task PurgeTrashAsync(string id, bool isFolder)
        => _transport.InvokeAsync<object?>("trash.purge", new { id, is_folder = isFolder });

    // —— 搜索与智能列表 ——

    public Task<List<LinkDto>> SearchAsync(string query, bool searchTitle = true, bool searchUrl = false,
        bool searchDescription = false, bool searchPath = false,
        string sortBy = "title", string sortOrder = "asc")
        => _transport.InvokeAsync<List<LinkDto>>("search", new
        {
            query, search_title = searchTitle, search_url = searchUrl,
            search_description = searchDescription, search_path = searchPath,
            sort_by = sortBy, sort_order = sortOrder
        });

    public Task<List<LinkDto>> GetSmartListAsync(string kind, int limit = 50)
        => _transport.InvokeAsync<List<LinkDto>>("smartlist", new { kind, limit });

    // —— 元数据与统计 ——

    public Task<MetadataDto?> FetchMetadataAsync(string url)
        => _transport.InvokeAsync<MetadataDto?>("meta.fetch", new { url });

    public Task<LinkCountsDto> GetCountsAsync()
        => _transport.InvokeAsync<LinkCountsDto>("stats.counts");

    // —— 导入 / 导出（Netscape 书签文件格式）——

    public Task<string> ExportBookmarksHtmlAsync(string outputFilePath)
        => _transport.InvokeAsync<string>("export.bookmarks_html", new { output_path = outputFilePath });

    public Task<int> ImportBookmarksHtmlAsync(string filePath)
        => _transport.InvokeAsync<int>("import.bookmarks_html", new { file_path = filePath });

    public Task<BookmarkFileInspectionDto> InspectBookmarksHtmlAsync(string filePath)
        => _transport.InvokeAsync<BookmarkFileInspectionDto>("bookmarks.inspect_html", new { file_path = filePath });

    // —— 备份与维护 ——

    public Task ExportBackupAsync(string outputPath)
        => _transport.InvokeAsync<object?>("backup.export", new { output_path = outputPath });

    public Task<BackupImportDto> ImportBackupAsync(string filePath)
        => _transport.InvokeAsync<BackupImportDto>("backup.import", new { file_path = filePath });

    public Task ReinitializeDatabaseAsync(bool resetData = true)
        => _transport.InvokeAsync<object?>("settings.reinit_db", new { reset_data = resetData });

    public Task<List<TrashEntryDto>> GetTrashUnitContentsAsync(string trashFolderId)
        => _transport.InvokeAsync<List<TrashEntryDto>>("trash.unit_contents", new { trash_folder_id = trashFolderId });
}

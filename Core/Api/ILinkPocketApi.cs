using LinkPocket.Api;

namespace LinkPocket.Api;

/// <summary>
/// LinkPocket 后端 API 契约：前端可用的全部数据操作。
/// 实现类（LinkPocketApi）运行在后端进程内；前端只通过通信层
/// （ILinkPocketTransport，进程内直连或未来的 HTTP/WebSocket）调用这些方法。
/// </summary>
public interface ILinkPocketApi
{
    // —— 浏览（资源管理器式逐级进入）——
    /// <summary>获取一个目录的内容。folderId 为 null/"0" 表示根目录（全部书签）。</summary>
    Task<FolderContentsDto> GetFolderContentsAsync(string? folderId, string sortBy = "title", string sortOrder = "asc");
    /// <summary>获取文件夹树（含根节点），用于侧栏树展示。</summary>
    Task<List<FolderDto>> GetFolderTreeAsync();
    /// <summary>获取面包屑路径（从"全部书签"到当前文件夹的名称列表）。</summary>
    Task<List<string>> GetBreadcrumbAsync(string? folderId);

    // —— 文件夹管理 ——
    Task<FolderDto> CreateFolderAsync(string name, string? parentId);
    Task<FolderDto> UpdateFolderAsync(string id, string? name = null, string? description = null, string? parentId = null);
    /// <summary>删除文件夹。cascade: "move_to_parent"（默认）| "trash_links" | "target"，配合 targetListId。</summary>
    Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null);
    Task MoveFolderAsync(string folderId, string? targetParentId);
    Task<string> CopyFolderAsync(string folderId, string? targetParentId);
    Task<bool> WouldMoveCreateCycleAsync(string folderId, string targetParentId);
    Task UpdateSortAsync(string? parentId, List<string> itemIds);

    // —— 链接 ——
    Task<PagedLinksDto> GetLinksAsync(string? listId = null, string? search = null, bool? isImportant = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20);
    Task<LinkDto> CreateLinkAsync(string url, string? title = null, string? description = null,
        string? listId = null, string? faviconUrl = null);
    Task<LinkDto> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? faviconUrl = null);
    /// <summary>把链接移入回收站（软删除）。</summary>
    Task TrashLinkAsync(string id);
    Task RecordVisitAsync(string id);

    // —— 回收站 ——
    Task<List<TrashEntryDto>> GetTrashAsync();
    Task<LinkDto> RestoreLinkAsync(string linkId);
    Task PurgeLinkAsync(string linkId);

    // —— 搜索与智能列表 ——
    Task<List<LinkDto>> SearchAsync(string query, bool searchTitle = true, bool searchUrl = false,
        bool searchDescription = false, bool searchPath = false,
        string sortBy = "title", string sortOrder = "asc");
    /// <summary>kind: "recently_added" | "recently_visited" | "recently_edited" | "most_visited"</summary>
    Task<List<LinkDto>> GetSmartListAsync(string kind, int limit = 50);

    // —— 元数据与统计 ——
    Task<MetadataDto?> FetchMetadataAsync(string url);
    Task<LinkCountsDto> GetCountsAsync();

    // —— 导入 / 导出 ——
    /// <summary>导出浏览器书签 HTML，返回导出文件完整路径。</summary>
    Task<string> ExportBookmarksHtmlAsync(string outputDirectory);
    /// <summary>导入浏览器书签 HTML，返回导入的条目数。</summary>
    Task<int> ImportBookmarksHtmlAsync(string filePath);
}

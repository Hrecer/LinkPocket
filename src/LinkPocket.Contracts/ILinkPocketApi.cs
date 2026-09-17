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
    /// <summary>
    /// 获取一个目录的内容。folderId 为 null 表示根目录（全部书签）——它不是实体、没有 ID。
    /// perPage &gt; 0 时对链接启用分页（page 从 1 开始）；perPage = 0（默认）不分页，
    /// 一次取回全部链接（与旧版行为一致）。
    /// totalLinkCount 只统计直接子链接，不递归子文件夹。
    /// </summary>
    Task<FolderContentsDto> GetFolderContentsAsync(string? folderId, string sortBy = "title", string sortOrder = "asc", int page = 1, int perPage = 0);
    /// <summary>获取文件夹树（含根节点），用于侧栏树展示。</summary>
    Task<List<FolderDto>> GetFolderTreeAsync();
    /// <summary>
    /// 按 ID 取单个文件夹（不存在或为根返回 null）。供「按 ID 跳转」等定位场景使用：
    /// 根不是实体，没有 ID，因此永远查不到。
    /// </summary>
    Task<FolderDto?> GetFolderAsync(string folderId);
    /// <summary>获取面包屑路径（从"全部书签"到当前文件夹的名称列表）。</summary>
    Task<List<string>> GetBreadcrumbAsync(string? folderId);

    // —— 文件夹管理 ——
    Task<FolderDto> CreateFolderAsync(string name, string? parentId);
    Task<FolderDto> UpdateFolderAsync(string id, string? name = null, string? description = null);
    /// <summary>删除文件夹。cascade: "move_to_parent"（默认）| "trash_links" | "target"，配合 targetListId。</summary>
    Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null);
    Task MoveFolderAsync(string folderId, string? targetParentId);
    Task<string> CopyFolderAsync(string folderId, string? targetParentId);
    Task<bool> WouldMoveCreateCycleAsync(string folderId, string targetParentId);
    Task UpdateSortAsync(string? parentId, List<string> itemIds);

    // —— 链接 ——
    Task<PagedLinksDto> GetLinksAsync(string? listId = null, string? search = null, bool? isImportant = null,
        string? dateFrom = null, string? dateTo = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20);
    /// <summary>获取全部活动链接（工具页去重等全量场景）。</summary>
    Task<List<LinkDto>> GetAllLinksAsync();
    /// <summary>按 ID 取单条链接（不存在返回 null）——「按 ID 跳转」定位时解析其所属目录。</summary>
    Task<LinkDto?> GetLinkAsync(string linkId);
    /// <summary>获取根级（未归类文件夹）链接，用于"全部书签"侧栏。</summary>
    Task<List<LinkDto>> GetRootLevelLinksAsync(string sortBy = "created_at", string sortOrder = "desc", int perPage = 50);
    Task<LinkDto> CreateLinkAsync(string url, string? title = null, string? description = null,
        string? listId = null, bool isImportant = false, bool autoFetchMetadata = false, string? faviconUrl = null);
    Task<LinkDto> UpdateLinkAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null, bool? isImportant = null, string? faviconUrl = null);
    /// <summary>把链接移入回收站（软删除）。</summary>
    Task TrashLinkAsync(string id);
    Task RecordVisitAsync(string id);

    // —— 回收站 ——
    /// <summary>回收站平铺条目（folder 单元根 + 单独删除的书签），按删除时间倒序。</summary>
    Task<List<TrashEntryDto>> GetTrashAsync();
    /// <summary>回收站文件夹树（全部单元，UI 组装层级）。</summary>
    Task<List<TrashFolderDto>> GetTrashTreeAsync();
    Task<LinkDto> RestoreLinkAsync(string linkId);
    /// <summary>永久删除：isFolder=true 时删除整个单元（含子单元与单元内书签快照）。</summary>
    Task PurgeTrashAsync(string id, bool isFolder);

    // —— 搜索与智能列表 ——
    Task<List<LinkDto>> SearchAsync(string query, bool searchTitle = true, bool searchUrl = false,
        bool searchDescription = false, bool searchPath = false,
        string sortBy = "title", string sortOrder = "asc");
    /// <summary>kind: "recently_added" | "recently_visited" | "recently_edited" | "most_visited"</summary>
    Task<List<LinkDto>> GetSmartListAsync(string kind, int limit = 50);

    // —— 元数据与统计 ——
    Task<MetadataDto?> FetchMetadataAsync(string url);
    Task<LinkCountsDto> GetCountsAsync();

    // —— 导入 / 导出（Netscape 书签文件格式：Chrome / Edge / Firefox 通用交换格式）——
    /// <summary>
    /// 导出浏览器书签 HTML（Netscape 书签文件格式），返回导出文件完整路径。
    /// <paramref name="outputFilePath"/> 是目标<b>文件</b>的完整路径（不是目录），目录须已存在。
    /// </summary>
    Task<string> ExportBookmarksHtmlAsync(string outputFilePath);
    /// <summary>导入浏览器书签 HTML（Netscape 书签文件格式），返回导入的条目数（文件夹 + 书签）。</summary>
    Task<int> ImportBookmarksHtmlAsync(string filePath);
    /// <summary>
    /// 只读预检书签文件：识别格式并统计条目数，不写任何数据。
    /// 导入前用它展示"将导入 N 个书签 / M 个文件夹"；导出后也可用它校验产物。
    /// </summary>
    Task<BookmarkFileInspectionDto> InspectBookmarksHtmlAsync(string filePath);

    // —— .lpbackup 备份 ——
    /// <summary>导出 .lpbackup 备份（manifest + data(临时 key 层级) + favicons）。回收站不在备份范围内。</summary>
    Task ExportBackupAsync(string outputPath);
    /// <summary>导入 .lpbackup 备份文件，返回导入统计。</summary>
    Task<BackupImportDto> ImportBackupAsync(string filePath);

    /// <summary>被删文件夹单元的内容（回收站「打开目录」）：直接子单元 + 子树内全部书签快照。</summary>
    Task<List<TrashEntryDto>> GetTrashUnitContentsAsync(string trashFolderId);

    // —— 维护 ——
    /// <summary>重建数据库。resetData 为 true 时删除数据库文件与 favicon 缓存后重建。</summary>
    Task ReinitializeDatabaseAsync(bool resetData = true);
}

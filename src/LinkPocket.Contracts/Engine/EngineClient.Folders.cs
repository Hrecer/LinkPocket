using LinkPocket.Contracts;

namespace LinkPocket.Contracts;

/// <summary>EngineClient · folders 域（14 命令，参数与 FoldersModule Handler 逐一对齐）。</summary>
public sealed partial class EngineClient
{
    /// <summary>目录内容（folder_id 缺省 = 根「全部书签」；per_page=0 不分页一次取回）。
    /// 查询命令免审计/免 ChangeSet，直接返回数据本体。</summary>
    public Task<FolderContentsDto> FolderContentsAsync(string? folderId = null,
        string sortBy = "title", string sortOrder = "asc", int page = 1, int perPage = 0,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<FolderContentsDto>("folders.contents",
            new { folder_id = folderId, sort_by = sortBy, sort_order = sortOrder, page, per_page = perPage }, o, ct);

    /// <summary>浏览页主视图一致快照：目录页 + 全量树 + 根级链接数（同一事务快照）。</summary>
    public Task<FolderContentsDto> FoldersOverviewAsync(string? folderId = null,
        string sortBy = "title", string sortOrder = "asc", int page = 1, int perPage = 0,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<FolderContentsDto>("folders.overview",
            new { folder_id = folderId, sort_by = sortBy, sort_order = sortOrder, page, per_page = perPage }, o, ct);

    /// <summary>文件夹树（含根节点，侧栏树数据源；sort_by 可传 sort_order 读取手工排序）。</summary>
    public Task<List<FolderDto>> FolderTreeAsync(string sortBy = "name", string sortOrder = "asc",
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<FolderDto>>("folders.tree",
            new { sort_by = sortBy, sort_order = sortOrder }, o, ct);

    /// <summary>按 ID 取文件夹（根不是实体、无 ID：缺省/null 即向根寻址 → LP.STATE.002）。</summary>
    public Task<FolderDto> FolderGetAsync(string? folderId = null, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<FolderDto>("folders.get", new { folder_id = folderId }, o, ct);

    /// <summary>面包屑（「全部书签」→ 当前目录的名称链）。</summary>
    public Task<List<string>> FolderBreadcrumbAsync(string? folderId = null,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<string>>("folders.breadcrumb", new { folder_id = folderId }, o, ct);

    public Task<CommandResult<FolderDto>> FolderCreateAsync(string name, string? description = null, string? parentId = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderDto>("folders.create", new { name, description, parent_id = parentId }, o, ct);

    public Task<CommandResult<FolderDto>> FolderUpdateAsync(string folderId, string? name = null,
        string? description = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderDto>("folders.update",
            new { folder_id = folderId, name, description }, o, ct);

    /// <summary>删除文件夹：cascade = trash_links（默认，整树入回收站）| move_to_list（配 target_list_id）。</summary>
    public Task<CommandResult<FolderDeleteResult>> FolderDeleteAsync(string folderId, string? cascade = null,
        string? targetListId = null, CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderDeleteResult>("folders.delete",
            new { folder_id = folderId, cascade, target_list_id = targetListId }, o, ct);

    public Task<CommandResult<FolderDto>> FolderMoveAsync(string folderId, string? targetParentId,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderDto>("folders.move", new { folder_id = folderId, target_parent_id = targetParentId }, o, ct);

    /// <summary>深拷贝子树（全新 ID；同名自动编号）。</summary>
    public Task<CommandResult<FolderCopyResult>> FolderCopyAsync(string folderId, string? targetParentId = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderCopyResult>("folders.copy", new { folder_id = folderId, target_parent_id = targetParentId }, o, ct);

    /// <summary>手工排序（item_ids 顺序 = sort_order 顺序）。</summary>
    public Task<CommandResult<FolderSortResult>> FolderSortAsync(IReadOnlyList<string> itemIds, string? parentId = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderSortResult>("folders.sort", new { parent_id = parentId, item_ids = itemIds }, o, ct);

    /// <summary>批量移动（原子 + Windows 式同名自动编号）。</summary>
    public Task<CommandResult<FolderMoveBatchResult>> FolderMoveBatchAsync(IReadOnlyList<string> folderIds, string? targetParentId,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<FolderMoveBatchResult>("folders.move_batch",
            new { folder_ids = folderIds, target_parent_id = targetParentId }, o, ct);

    /// <summary>★按名定位（contains = 模糊匹配）。AI 就绪能力。</summary>
    public Task<List<FolderDto>> FolderFindAsync(string name, bool? contains = null,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<FolderDto>>("folders.find", new { name, contains }, o, ct);

    /// <summary>环检测（移动 folder_id 到 target_parent_id 是否产生环）。</summary>
    public Task<bool> FolderCycleCheckAsync(string folderId, string? targetParentId,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<bool>("folders.cycle_check", new { folder_id = folderId, target_parent_id = targetParentId }, o, ct);
}

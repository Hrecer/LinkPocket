using LinkPocket.Contracts;
using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>EngineClient · trash / search 域（7 + 2 命令，参数与 Handler 逐一对齐）。</summary>
public sealed partial class EngineClient
{
    /// <summary>回收站平铺条目（单独删除书签 + 单元根，按删除时间倒序）。查询直接返回数据本体。</summary>
    public Task<List<TrashEntryDto>> TrashListAsync(CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<TrashEntryDto>>("trash.list", null, o, ct);

    /// <summary>回收站文件夹树（全部单元，UI 组装层级）。</summary>
    public Task<List<TrashFolderDto>> TrashTreeAsync(CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<TrashFolderDto>>("trash.tree", null, o, ct);

    /// <summary>被删单元内容（直接子单元 + 子树内全部书签快照）。</summary>
    public Task<List<TrashEntryDto>> TrashUnitContentsAsync(string id,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<TrashEntryDto>>("trash.unit_contents", new { id }, o, ct);

    /// <summary>回收站页快照（全量单元 + 全量书签快照，每项携归属单元 trash_folder_id；null = 根级）。</summary>
    public Task<TrashOverviewDto> TrashOverviewAsync(CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<TrashOverviewDto>("trash.overview", null, o, ct);

    /// <summary>回收站内搬移（书签快照 / 单元；targetTrashFolderId 缺省 = 回收站根；不是还原、无撤销）。</summary>
    public Task<CommandResult<JsonElement>> TrashMoveAsync(string id, bool isFolder,
        string? targetTrashFolderId = null, CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("trash.move",
            new { id, is_folder = isFolder, target_trash_folder_id = targetTrashFolderId }, o, ct);

    /// <summary>还原单条链接（保留原 ID；to = origin（缺省）回删除前位置 / root 落根）。</summary>
    public Task<CommandResult<TrashRestoreResult>> TrashRestoreAsync(string id, string to = "origin",
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<TrashRestoreResult>("trash.restore", new { id, to }, o, ct);

    /// <summary>混合批量还原（链接 + 单元；缺省回删除前位置；原子单事务）。</summary>
    public Task<CommandResult<TrashRestoreBatchResult>> TrashRestoreBatchAsync(
        IReadOnlyList<string> linkIds, IReadOnlyList<string> folderIds, string to = "origin",
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { link_ids = linkIds, folder_ids = folderIds, to }, o, ct);

    /// <summary>永久删除（破坏性：两阶段确认）。link = 单独删除的书签；folder = 整单元。</summary>
    public Task<CommandResult<JsonElement>> TrashPurgeAsync(string id, bool isFolder,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("trash.purge", new { id, is_folder = isFolder }, o, ct);

    /// <summary>批量永久删除（破坏性：两阶段确认）。</summary>
    public Task<CommandResult<TrashPurgeBatchResult>> TrashPurgeBatchAsync(IReadOnlyList<string> linkIds,
        IReadOnlyList<string> folderIds, CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<TrashPurgeBatchResult>("trash.purge_batch",
            new { link_ids = linkIds, folder_ids = folderIds }, o, ct);

    /// <summary>搜索（query 空 = 空结果口径；范围开关决定命中字段）。</summary>
    public Task<List<LinkDto>> SearchLinksAsync(string query, bool searchTitle = true,
        bool searchUrl = false, bool searchDescription = false, bool searchPath = false,
        string sortBy = "title", string sortOrder = "asc",
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<LinkDto>>("search.links", new
        {
            query, search_title = searchTitle, search_url = searchUrl,
            search_description = searchDescription, search_path = searchPath,
            sort_by = sortBy, sort_order = sortOrder,
        }, o, ct);
}

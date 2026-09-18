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

    /// <summary>还原（保留原 ID；toOrigin = true 还原到删除前所在目录，原目录已不存在时落根）。</summary>
    public Task<CommandResult<TrashRestoreResult>> TrashRestoreAsync(string id, bool toOrigin = false,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<TrashRestoreResult>("trash.restore", new { id, to_origin = toOrigin }, o, ct);

    /// <summary>批量还原（固定落根）。</summary>
    public Task<CommandResult<TrashRestoreBatchResult>> TrashRestoreBatchAsync(IReadOnlyList<string> ids,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch", new { ids }, o, ct);

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

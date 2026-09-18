namespace LinkPocket.Contracts;

/// <summary>
/// 命令结果 DTO 组（wire 对外形状 = 契约；阶段 6 自模块提升，模型归位阶段随域归位）。
/// snake_case JSON 字段名由引擎序列化约定统一生成。
/// </summary>

// —— folders ——

/// <summary>深层复制的产出。</summary>
public sealed record FolderCopyResult(string NewFolderId);

/// <summary>删除结果（含级联模式与连带统计）。</summary>
public sealed record FolderDeleteResult(
    string Cascade,
    int DeletedFolders,
    int TrashedLinks);

/// <summary>批量移动结果（含同名自动编号说明）。</summary>
public sealed record FolderMoveBatchResult(
    int Moved,
    IReadOnlyList<string> RenamedNotes);

/// <summary>重排结果。</summary>
public sealed record FolderSortResult(int Sorted);

// —— links ——

/// <summary>links.trash 结果（回收站保留原 ID + 位置快照口径）。</summary>
public sealed record LinkTrashResult(string LinkId, string? OriginPath);

/// <summary>批量命令共用结果。</summary>
public sealed record LinkBatchResult(string Kind, int Affected);

/// <summary>links.export 结果。</summary>
public sealed record LinkExportResult(string FilePath, string Format, int Count, long FileBytes);

// —— trash ——

/// <summary>
/// 还原结果。<paramref name="ListId"/> = 实际落点目录（null = 根）；
/// <paramref name="RestoredToOrigin"/> = 是否按原位置还原（<c>to_origin: true</c> 且原目录仍存在）。
/// </summary>
public sealed record TrashRestoreResult(string LinkId, string? ListId = null, bool RestoredToOrigin = false);

/// <summary>批量还原结果。</summary>
public sealed record TrashRestoreBatchResult(int Restored);

/// <summary>批量永久删除结果。</summary>
public sealed record TrashPurgeBatchResult(int PurgedLinks, int PurgedFolders);

namespace LinkPocket.Contracts;

/// <summary>
/// 命令结果 DTO 组（wire 对外形状 = 契约）。
/// snake_case JSON 字段名由引擎序列化约定统一生成。
/// </summary>

// —— folders ——

/// <summary>深层复制的产出（Name = 引擎同层唯一编号后的最终名，供调用方提示"重命名"）。</summary>
public sealed record FolderCopyResult(string NewFolderId, string Name);

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
/// <paramref name="FellBackToRoot"/> = 原目录已不存在而回落根（如实回报，绝不静默）。
/// </summary>
public sealed record TrashRestoreResult(string LinkId, string? ListId = null, bool FellBackToRoot = false);

/// <summary>混合批量还原结果（<paramref name="RestoredFolders"/> = 单元含其子单元的还原行数）。</summary>
public sealed record TrashRestoreBatchResult(
    int RestoredLinks,
    int RestoredFolders,
    int RestoredUnits,
    IReadOnlyList<string> FellBackToRoot,
    IReadOnlyList<TrashRestoreRename> Renamed,
    int DuplicateUrls);

/// <summary>还原时撞名自动编号（同层唯一命名服务）：From → To。</summary>
public sealed record TrashRestoreRename(string From, string To);

/// <summary>批量永久删除结果。</summary>
public sealed record TrashPurgeBatchResult(int PurgedLinks, int PurgedFolders);

// —— maintenance ——

/// <summary>整库重置结果（替代匿名对象，客户端可强类型读取；wire 字段名与旧匿名对象形状一致）。</summary>
public sealed record MaintenanceReinitResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("cleared")] bool Cleared,
    [property: System.Text.Json.Serialization.JsonPropertyName("favicon_cache_cleared")] bool FaviconCacheCleared);

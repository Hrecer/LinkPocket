using LinkPocket.Contracts;
using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>EngineClient · bookmarks / backup / dedup / favicon / maintenance 域（3+3+3+2+3 命令）。</summary>
public sealed partial class EngineClient
{
    /// <summary>只读预检 Netscape 书签文件（导入前计数 / 导出后校验）。查询直接返回数据本体。</summary>
    public Task<BookmarkFileInspectionDto> BookmarksInspectAsync(string filePath,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<BookmarkFileInspectionDto>("bookmarks.inspect", new { file_path = filePath }, o, ct);

    /// <summary>导入 Netscape 书签 HTML（LongRunning/单事务）。返回 JsonElement（folders_created/links_created/...）。</summary>
    public Task<CommandResult<JsonElement>> BookmarksImportAsync(string filePath,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = filePath }, o, ct);

    /// <summary>导出 Netscape 书签 HTML。返回 JsonElement（file_path/统计）。</summary>
    public Task<CommandResult<JsonElement>> BookmarksExportAsync(string filePath,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("bookmarks.export", new { file_path = filePath }, o, ct);

    /// <summary>导出 .lpbackup 备份（回收站不进备份）。</summary>
    public Task<CommandResult<JsonElement>> BackupExportAsync(string outputPath, bool includeTrash = false,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("backup.export", new { output_path = outputPath, include_trash = includeTrash }, o, ct);

    /// <summary>导入 .lpbackup（replace = 清空后导入，Destructive 两阶段确认）。返回 JsonElement（统计）。</summary>
    public Task<CommandResult<JsonElement>> BackupImportAsync(string filePath, bool replace = false,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("backup.import", new { file_path = filePath, replace }, o, ct);

    /// <summary>★读备份 manifest + 校验和（不写库）。</summary>
    public Task<JsonElement> BackupInspectAsync(string filePath,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<JsonElement>("backup.inspect", new { file_path = filePath }, o, ct);

    /// <summary>执行查重处置（嵌套派发 links.trash；支持 DryRun 预演；keep_explicit 未列出的组跳过）。返回 JsonElement（strategy/groups/trashed）。</summary>
    public Task<CommandResult<JsonElement>> DedupApplyAsync(string? strategy = null, IReadOnlyList<string>? groupUrls = null,
        IReadOnlyDictionary<string, string>? explicitKeep = null, CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("dedup.apply", new { strategy, group_urls = groupUrls, explicit_keep = explicitKeep }, o, ct);

    /// <summary>图标缓存统计。</summary>
    public Task<JsonElement> FaviconCacheStatsAsync(CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<JsonElement>("favicon.cache_stats", null, o, ct);

    /// <summary>图标预取（后台队列；link_ids 空 = 全库未缓存）。返回 JsonElement。</summary>
    public Task<CommandResult<JsonElement>> FaviconPrefetchAsync(IReadOnlyList<string>? linkIds = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("favicon.prefetch", new { link_ids = linkIds }, o, ct);

    /// <summary>当前库 schema 版本（schema_migrations MAX(version)）。</summary>
    public Task<JsonElement> MaintenanceSchemaVersionAsync(
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<JsonElement>("maintenance.schema_version", null, o, ct);

    /// <summary>★脱敏诊断打包（版本/计数）。</summary>
    public Task<JsonElement> DiagnosticsCollectAsync(
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<JsonElement>("diagnostics.collect", null, o, ct);

    /// <summary>整库重置（破坏性：两阶段确认）。</summary>
    public Task<CommandResult<JsonElement>> MaintenanceReinitAsync(
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("maintenance.reinit", null, o, ct);
}

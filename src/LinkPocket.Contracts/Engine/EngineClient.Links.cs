using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Contracts;

/// <summary>EngineClient · links 域（16 命令，参数与 LinksModule Handler 逐一对齐）。</summary>
public sealed partial class EngineClient
{
    /// <summary>分页链接列表（search/is_important/date_from/date_to 过滤；per_page=0 全量）。查询直接返回数据本体。</summary>
    public Task<PagedLinksDto> LinkListAsync(string? listId = null, string? search = null,
        bool? isImportant = null, string? dateFrom = null, string? dateTo = null,
        string sortBy = "created_at", string sortOrder = "desc", int page = 1, int perPage = 20,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<PagedLinksDto>("links.list", new
        {
            list_id = listId, search, is_important = isImportant,
            date_from = dateFrom, date_to = dateTo,
            sort_by = sortBy, sort_order = sortOrder, page, per_page = perPage,
        }, o, ct);

    public Task<LinkDto> LinkGetAsync(string id, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<LinkDto>("links.get", new { id }, o, ct);

    /// <summary>全库活动链接（per_page=0 一次取回；工具页去重等全量场景）。
    /// 读流每查询一个短 UoW，与旧面 <c>GetAllLinksAsync</c> 同等语义。</summary>
    public async Task<List<LinkDto>> LinkAllAsync(CallOptions? o = null, CancellationToken ct = default)
    {
        var page = await QueryAsync<PagedLinksDto>("links.list",
            new { page = 1, per_page = 0 }, o, ct);
        return page.Links;
    }

    /// <summary>根级（未归类）链接。</summary>
    public Task<List<LinkDto>> LinkRootsAsync(string sortBy = "created_at", string sortOrder = "desc",
        int perPage = 50, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<LinkDto>>("links.roots",
            new { sort_by = sortBy, sort_order = sortOrder, per_page = perPage }, o, ct);

    /// <summary>全库统计（总数/回收站数/按目录计数）。</summary>
    public Task<LinkCountsDto> LinkStatsAsync(CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<LinkCountsDto>("links.stats", null, o, ct);

    /// <summary>智能列表：kind = recently_added | recently_visited | recently_edited | most_visited。</summary>
    public Task<List<LinkDto>> LinkSmartListAsync(string kind, int limit = 50,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<LinkDto>>("links.smart_list", new { kind, limit }, o, ct);

    /// <summary>网络抓取页面元数据（闸外执行；失败 = LP.ENG.002 可重试）。</summary>
    public Task<MetadataDto> LinkMetadataFetchAsync(string url,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<MetadataDto>("links.metadata_fetch", new { url }, o, ct);

    public Task<CommandResult<LinkDto>> LinkCreateAsync(string url, string? title = null, string? description = null,
        string? listId = null, bool? isImportant = null, bool? autoFetchMetadata = null, string? faviconUrl = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkDto>("links.create", new
        {
            url, title, description, list_id = listId,
            is_important = isImportant, auto_fetch_metadata = autoFetchMetadata, favicon_url = faviconUrl,
        }, o, ct);

    public Task<CommandResult<LinkDto>> LinkUpdateAsync(string id, string? url = null, string? title = null,
        string? description = null, string? listId = null, bool? isImportant = null, string? faviconUrl = null,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkDto>("links.update", new
        {
            id, url, title, description, list_id = listId,
            is_important = isImportant, favicon_url = faviconUrl,
        }, o, ct);

    /// <summary>移入回收站（软删除；保留原 ID + 原位置快照）。</summary>
    public Task<CommandResult<LinkTrashResult>> LinkTrashAsync(string id,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkTrashResult>("links.trash", new { id }, o, ct);

    /// <summary>记录访问（链接 + 父链 visit/last_visited 单 UoW 落账）。</summary>
    public Task<CommandResult<JsonElement>> LinkVisitRecordAsync(string id,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("links.visit_record", new { id }, o, ct);

    /// <summary>批量移动（单命令单事务，后端循环）。</summary>
    public Task<CommandResult<LinkBatchResult>> LinkMoveBatchAsync(IReadOnlyList<string> linkIds, string? targetListId,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkBatchResult>("links.move_batch", new { link_ids = linkIds, target_list_id = targetListId }, o, ct);

    /// <summary>批量复制（全新 ID）。</summary>
    public Task<CommandResult<LinkBatchResult>> LinkCopyBatchAsync(IReadOnlyList<string> linkIds, string? targetListId,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkBatchResult>("links.copy_batch", new { link_ids = linkIds, target_list_id = targetListId }, o, ct);

    /// <summary>批量记录访问。</summary>
    public Task<CommandResult<LinkBatchResult>> LinkVisitBatchAsync(IReadOnlyList<string> linkIds,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkBatchResult>("links.visit_batch", new { link_ids = linkIds }, o, ct);

    /// <summary>导出链接（json/csv 文件）。</summary>
    public Task<CommandResult<LinkExportResult>> LinkExportAsync(string filePath, string format = "json",
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<LinkExportResult>("links.export", new { file_path = filePath, format }, o, ct);

    /// <summary>按 URL 精确查找（去重/同步场景）。</summary>
    public Task<List<LinkDto>> LinkFindByUrlAsync(string url,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<LinkDto>>("links.find_by_url", new { url }, o, ct);
}

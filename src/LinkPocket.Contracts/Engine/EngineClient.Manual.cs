using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>
/// EngineClient · 手写补充层：**无法机械生成**的方法——带定制逻辑（条件构造参数 / 名字改写 /
/// 强类型序列化）或非命令直通（批引擎直路由）。其余"每命令一个强类型直通"方法全部由
/// <c>EngineClient.Generated.cs</c> 从命令描述符机械生成（见 tools/LinkPocket.ClientGen）。
/// </summary>
public sealed partial class EngineClient
{
    /// <summary>批引擎入口（batch.run / batch.dry_run / batch.status 直路由；未装配时抛异常）。</summary>
    private IBatchEngine Batch => Engine.Batch
        ?? throw new EngineException(EngineErrors.Of(
            EngineErrors.Internal, "batch engine not wired (assemble it via OrchestrationHost.CreateHandlers)"));

    /// <summary>按脚本执行一批命令（事务批 abort 整批回滚；独立批每步各自提交）。</summary>
    public Task<BatchReport> RunBatchAsync(BatchScript script, CallOptions? options = null, CancellationToken ct = default)
        => Batch.RunAsync(script, options, ct);

    /// <summary>预演批脚本：全步骤执行但不提交，零副作用。</summary>
    public Task<BatchReport> DryRunBatchAsync(BatchScript script, CancellationToken ct = default)
        => Batch.DryRunAsync(script, ct);

    /// <summary>批运行状态（长批处理进度观测；未知批返回 null）。</summary>
    public BatchStatus? BatchStatusOf(string batchId) => Batch.GetStatus(batchId);

    /// <summary>保存宏（命名批脚本；无效脚本拒绝入库）。</summary>
    public Task<CommandResult<JsonElement>> MacroSaveAsync(string name, BatchScript script,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("macro.save",
            new { name, script = JsonSerializer.SerializeToElement(script, EngineOptions) }, options, ct);

    /// <summary>撤销最近一条（或 id 指定条目）可撤销命令。</summary>
    public Task<CommandResult<JsonElement>> UndoAsync(string? id = null,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("undo.undo", id is null ? null : new { id }, options, ct);

    /// <summary>把暂存文件转交正式命令执行（file_path 自动并入参数）。</summary>
    public Task<CommandResult<JsonElement>> StagingCommitAsync(string stagingId, string command,
        object? extraArgs = null, CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("staging.commit", new { staging_id = stagingId, command, args = extraArgs }, options, ct);

    /// <summary>对暂存文件执行纯函数变换管道（**强类型算子列表**；交给 wire 的序列化口径处理命名与枚举，
    /// dry_run 只出预览）。原始 JSON 形态见生成层的 <c>StagingTransformAsync(string, JsonElement, ...)</c>。</summary>
    public Task<CommandResult<StagingTransformReport>> StagingTransformAsync(string stagingId,
        IReadOnlyList<TransformOp> ops, bool dryRun = false,
        CallOptions? o = null, CancellationToken ct = default)
        => ExecuteAsync<StagingTransformReport>("staging.transform",
            new { staging_id = stagingId, ops, dry_run = dryRun }, o, ct);

    /// <summary>全库活动链接（per_page=0 一次取回；工具页去重等全量场景）。
    /// 读流每查询一个短 UoW，与旧面 <c>GetAllLinksAsync</c> 同等语义。复合方法（非单命令直通），故手写。</summary>
    public async Task<List<LinkDto>> LinkAllAsync(CallOptions? o = null, CancellationToken ct = default)
    {
        var page = await QueryAsync<PagedLinksDto>("links.list",
            new { page = 1, per_page = 0 }, o, ct);
        return page.Links;
    }

    // —— search 快照会话（手写：三条命令共享一个生命周期语义，生成层只描述单命令直通）——

    /// <summary>开一个搜索快照会话：count 与后续全部分页同一 SQLite 读事务。用完必须 close。</summary>
    public Task<SearchSessionOpenResult> SearchSessionOpenAsync(string query,
        bool? searchTitle = null, bool? searchUrl = null, bool? searchDescription = null, bool? searchPath = null,
        string? sortBy = null, string? sortOrder = null, int? firstPageSize = null,
        bool? includeFolders = null, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<SearchSessionOpenResult>("search.session_open",
            new
            {
                query,
                search_title = searchTitle,
                search_url = searchUrl,
                search_description = searchDescription,
                search_path = searchPath,
                sort_by = sortBy,
                sort_order = sortOrder,
                first_page_size = firstPageSize,
                include_folders = includeFolders,
            }, o, ct);

    /// <summary>从快照会话取一页（页码从 1 开始；per_page=0 = 引擎单页上限）。</summary>
    public Task<List<LinkDto>> SearchSessionPageAsync(string sessionId, int? page = null, int? perPage = null,
        CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<List<LinkDto>>("search.session_page",
            new { session_id = sessionId, page, per_page = perPage }, o, ct);

    /// <summary>关闭快照会话（幂等；不存在返回 false）。</summary>
    public Task<bool> SearchSessionCloseAsync(string sessionId, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<bool>("search.session_close", new { session_id = sessionId }, o, ct);

    /// <summary>增量对账：只按变更 ID 重新判定命中（界面活结果集；外部宿主不注册）。</summary>
    public Task<SearchReconcileResult> SearchReconcileAsync(string query,
        bool? searchTitle = null, bool? searchUrl = null, bool? searchDescription = null, bool? searchPath = null,
        IReadOnlyList<string>? linkIds = null, CallOptions? o = null, CancellationToken ct = default)
        => QueryAsync<SearchReconcileResult>("search.reconcile",
            new
            {
                query,
                search_title = searchTitle,
                search_url = searchUrl,
                search_description = searchDescription,
                search_path = searchPath,
                link_ids = linkIds,
            }, o, ct);

    /// <summary>Client 侧序列化口径（与 wire 层一致：snake_case + 枚举字符串）。</summary>
    private static readonly JsonSerializerOptions EngineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

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

    /// <summary>Client 侧序列化口径（与 wire 层一致：snake_case + 枚举字符串）。</summary>
    private static readonly JsonSerializerOptions EngineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

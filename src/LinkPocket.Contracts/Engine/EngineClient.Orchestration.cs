using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>编排层便利方法：批 / 宏 / 撤销 / 暂存 / 诊断的强类型入口。
/// 目录导出（IEngineCatalog）由 Engine 侧 OrchestrationHost 装配，不经客户端透传。</summary>
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

    /// <summary>保存宏（脚本 = **原始 JSON 元素**；界面编辑器给的是文本，合法性由引擎校验）。</summary>
    public Task<CommandResult<JsonElement>> MacroSaveRawAsync(string name, JsonElement script,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("macro.save", new { name, script }, options, ct);

    /// <summary>读取宏定义。</summary>
    public Task<JsonElement> MacroGetAsync(string name, CancellationToken ct = default)
        => QueryAsync<JsonElement>("macro.get", new { name }, null, ct);

    /// <summary>列出全部宏（名称 + 更新时间）。</summary>
    public Task<JsonElement> MacroListAsync(CancellationToken ct = default)
        => QueryAsync<JsonElement>("macro.list", null, null, ct);

    /// <summary>运行宏（事务批语义；abort 整体回滚）。</summary>
    public Task<CommandResult<JsonElement>> MacroRunAsync(string name,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("macro.run", new { name }, options, ct);

    /// <summary>删除宏。</summary>
    public Task<CommandResult<JsonElement>> MacroDeleteAsync(string name,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("macro.delete", new { name }, options, ct);

    /// <summary>撤销栈清单（最近在前）。</summary>
    public Task<JsonElement> UndoListAsync(CancellationToken ct = default)
        => QueryAsync<JsonElement>("undo.list", null, null, ct);

    /// <summary>重做栈清单（最近在前）——Ctrl+Y 的可用性据此精确判定（不靠本地猜测）。</summary>
    public Task<JsonElement> UndoListRedoAsync(CancellationToken ct = default)
        => QueryAsync<JsonElement>("undo.list_redo", null, null, ct);

    /// <summary>撤销最近一条（或 id 指定条目）可撤销命令。</summary>
    public Task<CommandResult<JsonElement>> UndoAsync(string? id = null,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("undo.undo", id is null ? null : new { id }, options, ct);

    /// <summary>重做：按原参数重放最近一条被撤销的命令。</summary>
    public Task<CommandResult<JsonElement>> RedoAsync(CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("undo.redo", null, options, ct);

    /// <summary>清空撤销/重做栈。</summary>
    public Task<CommandResult<int>> UndoClearAsync(CancellationToken ct = default)
        => ExecuteAsync<int>("undo.clear", null, null, ct);

    /// <summary>把文件拷入暂存区（SHA-256 指纹登记）。</summary>
    public Task<CommandResult<StagedFile>> StageAsync(string sourcePath,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<StagedFile>("staging.stage", new { source_path = sourcePath }, options, ct);

    /// <summary>列出暂存文件。</summary>
    public Task<JsonElement> StagingListAsync(CancellationToken ct = default)
        => QueryAsync<JsonElement>("staging.list", null, null, ct);

    /// <summary>对暂存文件执行纯函数变换管道（dry_run 只出预览）。</summary>
    public Task<CommandResult<StagingTransformReport>> StagingTransformAsync(string stagingId,
        IReadOnlyList<TransformOp> ops, bool dryRun = false,
        CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<StagingTransformReport>("staging.transform",
            new { staging_id = stagingId, ops, dry_run = dryRun }, options, ct);

    /// <summary>把暂存文件转交正式命令执行（file_path 自动并入参数）。</summary>
    public Task<CommandResult<JsonElement>> StagingCommitAsync(string stagingId, string command,
        object? extraArgs = null, CallOptions? options = null, CancellationToken ct = default)
        => ExecuteAsync<JsonElement>("staging.commit", new { staging_id = stagingId, command, args = extraArgs }, options, ct);

    /// <summary>收集引擎诊断（版本/schema/目录规模/事件水位/审计摘要，脱敏）。</summary>
    public Task<JsonElement> CollectDiagnosticsAsync(CancellationToken ct = default)
        => QueryAsync<JsonElement>("diagnostics.collect", null, null, ct);

    /// <summary>Client 侧序列化口径（与 wire 层一致：snake_case + 枚举字符串）。</summary>
    private static readonly JsonSerializerOptions EngineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

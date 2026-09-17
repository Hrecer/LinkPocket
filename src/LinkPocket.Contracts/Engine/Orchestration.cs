using System.Text.Json;

namespace LinkPocket.Contracts;

// ============================================================
// L2 编排层契约（方案 4.3）：Batch / Macro / Staging / Undo。
// 编排的一切皆走唯一调用模型——编排命令（batch.*/macro.*/undo.*/staging.*）
// 与普通命令同协议、同错误模型、同审计口径（AI 就绪的最后一公里）。
// ============================================================

/// <summary>批的事务范围（方案 3.5：批 = 显式事务边界）。</summary>
public enum BatchScope
{
    /// <summary>事务批：全部步骤共享一个工作单元，abort 时整批回滚（默认）。</summary>
    Transactional = 0,
    /// <summary>非事务批：每步独立提交；abort 只停止后续步骤，已执行步骤保持生效。</summary>
    Independent = 1,
}

/// <summary>批步骤失败策略。</summary>
public enum ErrorPolicy
{
    /// <summary>中止：剩余步骤不再执行（事务批整批回滚）。</summary>
    Abort = 0,
    /// <summary>继续：记录失败并执行剩余步骤。</summary>
    Continue = 1,
    /// <summary>跳过并记录：同 Continue，报告中失败步骤标记 Skipped。</summary>
    SkipAndLog = 2,
}

/// <summary>批步骤：Ref 供后续步骤以 {ref.path} 模板引用本步结果。</summary>
public sealed record BatchStep(
    string Ref,
    string Command,
    JsonElement Args,
    ErrorPolicy OnError = ErrorPolicy.Abort);

/// <summary>批脚本（方案 4.3 BatchScript）。</summary>
public sealed record BatchScript(
    string Name,
    IReadOnlyList<BatchStep> Steps,
    BatchScope Scope = BatchScope.Transactional);

/// <summary>单步执行结果。</summary>
public sealed record BatchStepResult(
    string Ref,
    string Command,
    bool Ok,
    bool Skipped,
    JsonElement? Data,
    string? ErrorCode,
    string? Message,
    long ElapsedMs);

/// <summary>批执行报告：每步结果 + 总变更集 + 审计关联（方案 2.3 批流）。</summary>
public sealed record BatchReport(
    string BatchId,
    string Name,
    bool Ok,
    IReadOnlyList<BatchStepResult> Steps,
    ChangeSet? Changes,
    string? HumanSummary,
    long ElapsedMs,
    string CorrelationId);

/// <summary>批运行状态（长批处理进度观测；方案 4.3 GetStatus）。</summary>
public sealed record BatchStatus(
    string BatchId,
    string Name,
    string State,
    int CompletedSteps,
    int TotalSteps);

/// <summary>批引擎（方案 4.3 IBatchEngine）。</summary>
public interface IBatchEngine
{
    Task<BatchReport> RunAsync(BatchScript script, CallOptions? options = null, CancellationToken ct = default);
    Task<BatchReport> DryRunAsync(BatchScript script, CancellationToken ct = default);
    BatchStatus? GetStatus(string batchId);
}

/// <summary>命名批处理（宏/技能库）持久化契约：存 schema v2 的 macros 表。</summary>
public interface IMacroStore
{
    Task SaveAsync(string name, string scriptJson, CancellationToken ct);
    Task<string?> GetAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<(string Name, string ScriptJson, DateTimeOffset UpdatedAt)>> ListAsync(CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Staging 登记条目：AI 文件准备区的一份文件（拷入 + SHA256 指纹）。</summary>
public sealed record StagedFile(
    string StagingId,
    string FileName,
    string FullPath,
    long SizeBytes,
    string Sha256,
    DateTimeOffset StagedAt);

/// <summary>Staging 变换算子：Op = 算子名（filter_links/rename_folder/map_field/strip_prefix/dedupe/reencode），Args = 算子参数。</summary>
public sealed record TransformOp(string Op, JsonElement Args);

/// <summary>变换结果报告（dry_run = 只报告预览，不落盘）。</summary>
public sealed record StagingTransformReport(
    string StagingId,
    bool DryRun,
    int ItemsBefore,
    int ItemsAfter,
    IReadOnlyList<string> AppliedOps,
    string? PreviewJson);

/// <summary>Staging 服务（方案 4.3 IStagingService）：AI 文件准备区——拷入/检视/纯函数变换/转正式命令。</summary>
public interface IStagingService
{
    Task<StagedFile> StageAsync(string sourcePath, CancellationToken ct);
    Task<IReadOnlyList<StagedFile>> ListAsync(CancellationToken ct);
    Task<bool> DiscardAsync(string stagingId, CancellationToken ct);
    Task<StagingTransformReport> TransformAsync(string stagingId, IReadOnlyList<TransformOp> ops,
        bool dryRun, CancellationToken ct);
    /// <summary>把 staged 文件转交正式命令（如 bookmarks.import）；extraArgs 与 file_path 合并下发。</summary>
    Task<CommandResult> CommitAsync(string stagingId, string targetCommand,
        object? extraArgs = null, CallOptions? options = null, CancellationToken ct = default);
    /// <summary>staged 文件当前内容（供检视/断言）。</summary>
    Task<string> ReadTextAsync(string stagingId, CancellationToken ct);
}

/// <summary>撤销栈条目：命令级逆向（逆向命令 = Descriptor.UndoInverse，逆向参数 = 原参数；对称对 trash↔restore 天然成立）。</summary>
public sealed record UndoEntry(
    string Id,
    DateTimeOffset At,
    string Command,
    JsonElement Args,
    string InverseCommand,
    JsonElement InverseArgs,
    CallerRef Caller);

/// <summary>
/// 撤销协调器（方案 4.3 IUndoCoordinator）：纯状态机（撤销栈 + 重做栈，上限 100 条）。
/// 行为由 undo.list / undo.undo / undo.redo / undo.clear 四个命令驱动；
/// 逆向命令在撤销命令的管道内经嵌套派发执行（与被撤销命令同事务语义）。
/// </summary>
public interface IUndoCoordinator
{
    Task<IReadOnlyList<UndoEntry>> ListAsync(CancellationToken ct);
    Task<int> ClearAsync(CancellationToken ct);

    /// <summary>弹出待撤销条目（id 缺省 = 最近一条；未找到返回 null），弹出后转入重做栈。</summary>
    Task<UndoEntry?> TakeUndoAsync(string? id, CancellationToken ct);

    /// <summary>弹出待重做条目（重放原命令原参数；栈空返回 null）。</summary>
    Task<UndoEntry?> TakeRedoAsync(CancellationToken ct);

    /// <summary>引擎在顶层可撤销命令（Reversible + UndoInverse）成功后登记；栈满丢最旧。</summary>
    void Record(CommandDescriptor descriptor, JsonElement args, CallerRef caller);
}

// ============================================================
// L4 会话 / 系统 API（方案 4.5）
// ============================================================

/// <summary>会话类别：ui = 桌面界面；agent = AI 代理（限流）；agent_readonly = 只读 AI；test = 测试。</summary>
public enum SessionKind
{
    Ui = 0,
    Agent = 1,
    AgentReadonly = 2,
    Test = 3,
}

/// <summary>会话档案（BeginAsync 入参；RateLimitPerMinute 缺省按类别：agent 30，其余不限）。</summary>
public sealed record SessionProfile(SessionKind Kind, int? RateLimitPerMinute = null);

/// <summary>活动会话。</summary>
public sealed record Session(
    string SessionId,
    SessionKind Kind,
    int RateLimitPerMinute,
    DateTimeOffset StartedAt);

/// <summary>会话管理器（方案 4.5 ISessionManager）：Begin/End + 每次 Execute/Query 前的能力校验。</summary>
public interface ISessionManager
{
    Task<Session> BeginAsync(SessionProfile profile, CancellationToken ct = default);
    Task EndAsync(string sessionId, CancellationToken ct = default);
    Session? Get(string sessionId);
    /// <summary>能力门：会话存在性 + 只读拒绝写 + 限流（违反即抛 EngineException）。</summary>
    void Enforce(CallerRef caller, bool isMutation, string correlationId);
}

/// <summary>目录导出格式（方案 4.5 IEngineCatalog.Export）。</summary>
public enum ManifestFormat
{
    /// <summary>AI FunctionCalling 工具清单（OpenAI tools 兼容形态）。</summary>
    FunctionCalling = 0,
    /// <summary>轻量 OpenAPI 描述。</summary>
    OpenApiLite = 1,
    /// <summary>Markdown 用户文档。</summary>
    MarkdownDocs = 2,
}

/// <summary>引擎目录（自描述导出：AI 工具清单/文档/测试骨架的唯一事实源）。</summary>
public interface IEngineCatalog
{
    EngineManifest Manifest(string? category = null);
    string Export(ManifestFormat format, string? category = null);
}

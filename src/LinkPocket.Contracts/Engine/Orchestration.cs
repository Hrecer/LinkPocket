using System.Text.Json;

namespace LinkPocket.Contracts;

// ============================================================
// L2 编排层契约：Batch / Macro / Staging / Undo。
// 编排的一切皆走唯一调用模型——编排命令（batch.*/macro.*/undo.*/staging.*）
// 与普通命令同协议、同错误模型、同审计口径（AI 就绪的最后一公里）。
// ============================================================

/// <summary>批的事务范围（批 = 显式事务边界）。</summary>
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

/// <summary>批脚本（BatchScript）。</summary>
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

/// <summary>批执行报告：每步结果 + 总变更集 + 审计关联（批流）。</summary>
public sealed record BatchReport(
    string BatchId,
    string Name,
    bool Ok,
    IReadOnlyList<BatchStepResult> Steps,
    ChangeSet? Changes,
    string? HumanSummary,
    long ElapsedMs,
    string CorrelationId);

/// <summary>批运行状态（长批处理进度观测；GetStatus）。</summary>
public sealed record BatchStatus(
    string BatchId,
    string Name,
    string State,
    int CompletedSteps,
    int TotalSteps);

/// <summary>批引擎（IBatchEngine）。</summary>
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

/// <summary>Staging 服务（IStagingService）：AI 文件准备区——拷入/检视/纯函数变换/转正式命令。</summary>
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

/// <summary>一个可执行动作（命令 + 参数）：撤销/重做都表达为动作。</summary>
public sealed record UndoAction(string Command, JsonElement Args);

/// <summary>
/// 逆向步骤：撤销该步时要执行的动作（+ 可选的重做动作）。
/// **由处理器在同一个事务内读到旧值后回填**（如移动前的父目录、新建出的新 ID）——
/// 引擎无法从"原参数"反推旧值，这正是"重命名/移动过去无法撤销"的结构原因。
/// Redo 缺省 null = **重放原命令原参数**；创建类命令必须显式给出（否则重做会生成**新 ID**、
/// 原 ID 丢失且回收站里留下旧快照——重做应是"从回收站还原原 ID"）。
/// </summary>
public sealed record UndoInverseStep(string Command, JsonElement Args, UndoAction? Redo = null);

/// <summary>一个可撤销步骤：原命令（重做默认重放）+ 逆向命令（撤销执行）+ 可选显式重做动作。</summary>
public sealed record UndoStep(
    string Command, JsonElement Args, string InverseCommand, JsonElement InverseArgs, UndoAction? Redo = null)
{
    /// <summary>重做该步要执行的动作（显式给出优先，否则重放原命令原参数）。</summary>
    public UndoAction RedoAction => Redo ?? new UndoAction(Command, Args);
}

/// <summary>
/// 撤销记录：**一条 = 一个用户动作**。通常单步；同组 ID 的多次调用（一次粘贴多选）合并为多步，
/// 撤销时按**逆序**逐步执行、重做时按**正序**重放。
/// GroupId = 同一次用户动作的多次调用共享的分组 ID；null = 独立动作。
/// </summary>
public sealed record UndoEntry(
    string Id,
    DateTimeOffset At,
    IReadOnlyList<UndoStep> Steps,
    CallerRef Caller,
    string? GroupId = null)
{
    /// <summary>最近一步的原命令（撤销清单展示用；多步记录取最后一步）。</summary>
    public string Command => Steps.Count > 0 ? Steps[^1].Command : string.Empty;
}

/// <summary>
/// 撤销协调器（IUndoCoordinator）：纯状态机（撤销栈 + 重做栈，上限 100 条）。
/// 行为由 undo.list / undo.list_redo / undo.undo / undo.redo / undo.clear 五个命令驱动；
/// 逆向命令在撤销命令的管道内经嵌套派发执行（与被撤销命令同事务语义）。
/// </summary>
public interface IUndoCoordinator
{
    Task<IReadOnlyList<UndoEntry>> ListAsync(CancellationToken ct);

    /// <summary>重做栈条目快照（最近在前）——redo 消费前先观察（执行成功才弹出，失败不丢栈）。</summary>
    Task<IReadOnlyList<UndoEntry>> ListRedoAsync(CancellationToken ct);

    Task<int> ClearAsync(CancellationToken ct);

    /// <summary>弹出待撤销条目（id 缺省 = 最近一条；未找到返回 null），弹出后转入重做栈。</summary>
    Task<UndoEntry?> TakeUndoAsync(string? id, CancellationToken ct);

    /// <summary>弹出待重做条目（重放原命令原参数；栈空返回 null）。</summary>
    Task<UndoEntry?> TakeRedoAsync(CancellationToken ct);

    /// <summary>
    /// 引擎在顶层可撤销命令成功后登记。
    /// <paramref name="inverse"/> = 处理器回填的逆向步骤（可多步，如批量移动每项一步）；
    /// 为 null 时退回"描述符声明的 UndoInverse + 原参数"（对称对 links.trash↔trash.restore 走这条）。
    /// <paramref name="groupId"/> 非空且与栈顶同组时**追加合并**为同一条记录（一次粘贴多选 = 一个动作）。
    /// </summary>
    void Record(CommandDescriptor descriptor, JsonElement args, CallerRef caller,
        IReadOnlyList<UndoInverseStep>? inverse = null, string? groupId = null);
}

// ============================================================
// L4 会话 / 系统 API
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

/// <summary>当前写锁快照（界面投影用：状态带文案 + 写入入口置灰；**权威在引擎**）。</summary>
public sealed record WriteHold(string SessionId, string Reason, DateTimeOffset TakenAt);

/// <summary>会话管理器（ISessionManager）：Begin/End + 每次 Execute/Query 前的能力校验 + 写入冻结（写锁）。</summary>
public interface ISessionManager
{
    Task<Session> BeginAsync(SessionProfile profile, CancellationToken ct = default);
    Task EndAsync(string sessionId, CancellationToken ct = default);
    Session? Get(string sessionId);
    /// <summary>能力门：会话存在性 + 只读拒绝写 + 限流 + **写入冻结**（违反即抛 EngineException）。</summary>
    void Enforce(CallerRef caller, bool isMutation, string correlationId);

    /// <summary>取写入冻结（写锁）：AI 改数据期间只放行持锁会话的写，其它写入一律 <c>LP.SEC.006</c>。
    /// <b>必须在回合终态 / 用户停止时 Dispose</b>（另有最大存活兜底，见实现）。</summary>
    IDisposable BeginWriteHold(string sessionId, string reason);

    /// <summary>当前写锁（无持锁者 → null）。</summary>
    WriteHold? CurrentWriteHold { get; }
}

/// <summary>目录导出格式（IEngineCatalog.Export）。</summary>
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

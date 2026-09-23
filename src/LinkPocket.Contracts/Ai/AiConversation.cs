using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>消息角色。</summary>
public enum AiRole
{
    User = 0,
    Assistant = 1,
    Notice = 2,
}

/// <summary>一条消息（用户 / 助手 / 系统提示；文本是用户数据或模型输出，界面原样投影，不做取词）。</summary>
public sealed record AiMessage(
    string MessageId,
    int Seq,
    AiRole Role,
    string Text,
    DateTimeOffset At,
    string? TurnId,
    bool IsStreaming = false);

/// <summary>工具调用状态机（一次模型工具调用 = 一个可持久化的状态机对象）。</summary>
public enum AiToolCallState
{
    Pending = 0,
    AwaitingApproval = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Rejected = 5,
    Skipped = 6,
}

/// <summary>一次工具调用（命令名与参数是**英文机器面**；两个摘要文本供界面卡片直接显示）。</summary>
public sealed record AiToolCall(
    string CallId,
    int Seq,
    string TurnId,
    string Command,
    AiToolCallState State,
    string? ArgsJson,
    string? ArgsSummary,
    string? ResultSummary,
    string? ErrorCode,
    long ElapsedMs,
    bool IsDryRun,
    string? CorrelationId,
    string? BatchId,
    DateTimeOffset At,
    IReadOnlyList<string>? ChangeIds = null,
    /// <summary>工具结果的原始 JSON（回灌模型用；界面默认只显示 ResultSummary，"查看原始结果"才读它）。</summary>
    string? ResultJson = null);

/// <summary>变更分类（台账展示用）。</summary>
public enum AiChangeKind
{
    Create = 0,
    Update = 1,
    Move = 2,
    Delete = 3,
    Purge = 4,
    Restore = 5,
    Import = 6,
    Export = 7,
    Diagnostic = 8,
}

/// <summary>变更结果（成功 / 失败 / 跳过 / 被拒 / 干跑）。</summary>
public enum AiChangeOutcome
{
    Applied = 0,
    Failed = 1,
    Skipped = 2,
    Rejected = 3,
    DryRun = 4,
}

/// <summary>变更记录的粒度来源（**如实标注**：引擎字段级 diff / AI 对账 / 仅实体级）。</summary>
public enum AiChangeSource
{
    EngineDiff = 0,
    Reconciled = 1,
    EntityOnly = 2,
}

/// <summary>字段级差异（before/after 是引擎原始值，界面负责格式化显示）。</summary>
public sealed record AiFieldChange(string Field, JsonElement? Before, JsonElement? After);

/// <summary>台账条目：一条变更 = 一个实体的影响（创建 / 修改 / 移动 / 删除 / 还原…）。</summary>
public sealed record AiChange(
    string ChangeId,
    int Seq,
    string TurnId,
    string CallId,
    string Command,
    AiChangeKind Kind,
    string EntityType,
    string EntityId,
    string? EntityName,
    string? EntityPath,
    bool EntityExists,
    IReadOnlyList<AiFieldChange>? Fields,
    AiChangeOutcome Outcome,
    string? ErrorCode,
    bool Undoable,
    AiChangeSource Source,
    bool Truncated,
    int Omitted,
    DateTimeOffset At,
    string? CorrelationId,
    string? BatchId);

/// <summary>审批决定四档（界面默认焦点落在「拒绝」）。</summary>
public enum AiApprovalDecision
{
    AllowOnce = 0,
    AllowForSession = 1,
    Reject = 2,
    RejectAndStop = 3,
}

/// <summary>
/// 批 / 宏审批卡里的一行「逐步骤影响」（功能书 §8.2：批脚本审批必须逐步骤可见）。
/// <paramref name="Command"/> 是机器面命令名；<paramref name="TargetName"/> 是用户数据（名称 / 标题 / URL），
/// 解析不出名称时为 null（由 <see cref="TargetCount"/> 如实报数量）；模板占位符（<c>{ref…}</c>）
/// 不是值——既不当名称也不当数量，此时 <see cref="TargetCount"/> = 0（未指明）。
/// </summary>
public sealed record AiApprovalStep(
    int Index,
    string Command,
    string? TargetName,
    int TargetCount,
    bool IsDestructive,
    string? OnError);

/// <summary>一次审批（请求字段 + 决定的最终结果；理由原样回灌模型）。
/// 影响面取自**入参与引擎**（不是模型自述）：解析有上限，超出只报数量并如实标记。</summary>
public sealed record AiApproval(
    string ApprovalId,
    int Seq,
    string TurnId,
    string CallId,
    string Command,
    bool IsDestructive,
    int TargetCount,
    IReadOnlyList<string> TargetNames,
    string? ScopeDescription,
    string? PreviewSummary,
    string? ErrorCodeWhenWaiting,
    AiApprovalDecision? Decision,
    string? Reason,
    long WaitMs,
    DateTimeOffset At,
    /// <summary>目标名单被截断：还有 <c>TargetMore</c> 个对象没列出来（不猜、不静默丢）。</summary>
    int TargetMore = 0,
    /// <summary>单一目标的 canonical 路径（可解析时；界面按当前语言投影显示）。</summary>
    string? TargetPath = null,
    /// <summary>批 / 宏的逐步骤影响（非批为 null）。</summary>
    IReadOnlyList<AiApprovalStep>? Steps = null,
    /// <summary>「本次会话总是允许」将记住的作用域（机器面：工具名 [+ 对象范围]；必须显示给用户）。</summary>
    string? AllowScope = null);

/// <summary>回合状态机。</summary>
public enum AiTurnState
{
    Pending = 0,
    Streaming = 1,
    ToolRunning = 2,
    AwaitingApproval = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
    Interrupted = 7,
}

/// <summary>一个回合（一次用户输入到模型停止）。</summary>
public sealed record AiTurn(
    string TurnId,
    int Index,
    AiTurnState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? ErrorCode,
    int ToolCallCount,
    int ChangeCount,
    int CallCount,
    bool WriteFrozen);

/// <summary>会话摘要（左栏列表用）。</summary>
public sealed record AiSessionSummary(
    string SessionId,
    string Title,
    AiMode Mode,
    string? ProviderId,
    string? ModelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    int ChangeCount,
    AiTurnState? ActiveTurnState);

/// <summary>会话详情（对话 + 工具调用 + 台账 + 审批 + 回合；各列表按 Seq 排序即时间线）。</summary>
public sealed record AiSessionDetail(
    AiSessionSummary Summary,
    IReadOnlyList<AiMessage> Messages,
    IReadOnlyList<AiToolCall> ToolCalls,
    IReadOnlyList<AiChange> Changes,
    IReadOnlyList<AiApproval> Approvals,
    IReadOnlyList<AiTurn> Turns);

/// <summary>通知种类（单订阅点：UI VM 按 Kind 分派）。</summary>
public enum AiNotificationKind
{
    MessageAdded = 0,
    StreamDelta = 1,
    ToolCallChanged = 2,
    ChangeRecorded = 3,
    ApprovalChanged = 4,
    TurnChanged = 5,
    SessionChanged = 6,
}

/// <summary>增量通知（fat record：只填对应 Kind 的字段；UI 只订阅一次）。</summary>
public sealed record AiNotification(
    AiNotificationKind Kind,
    string SessionId,
    string? TurnId = null,
    string? MessageId = null,
    string? TextDelta = null,
    AiMessage? Message = null,
    AiToolCall? ToolCall = null,
    AiChange? Change = null,
    AiApproval? Approval = null,
    AiTurn? Turn = null,
    AiSessionSummary? Session = null);

/// <summary>导出格式（会话快照与审计报告；CSV 用于台账逐条）。</summary>
public enum AiExportFormat
{
    Markdown = 0,
    Csv = 1,
    Json = 2,
}

/// <summary>回合上下文快照（界面注入：用户"此刻在看什么"；语言决定模型回答语言）。</summary>
public sealed record AiTurnContext(
    string? NavId = null,
    string? FolderPath = null,
    IReadOnlyList<string>? SelectedNames = null,
    string? LanguageCode = null);

/// <summary>
/// 引擎审计页签的查询（数据源 = 引擎的 <c>audit.query</c>）：
/// 按**回合关联**（correlation = <c>ai:&lt;turnId&gt;</c>）取齐该回合的引擎调用史；
/// <see cref="TurnId"/> 为 null = 本会话最近若干回合合并（按时间倒序）。
/// </summary>
public sealed record AiAuditQuery(
    string SessionId,
    string? TurnId = null,
    string? Search = null,        // 命令名子串（客户端过滤：引擎侧无模糊参数）
    bool? Success = null,         // 成功 / 失败筛选（服务端过滤）
    bool IncludePayloads = false, // 是否携带 args_json / changes_json 载荷
    int Page = 1,
    int PerPage = 50,
    /// <summary>时间下界（**含**；服务端过滤 = 分页与总数一起按它算，不做事后裁剪）。</summary>
    DateTimeOffset? From = null,
    /// <summary>时间上界（**不含**，半开区间——与引擎 audit.query 同口径；null = 不限）。</summary>
    DateTimeOffset? To = null);

/// <summary>一行引擎审计记录（机器面字段原样，界面只按键与数值渲染）。</summary>
public sealed record AiEngineCallRow(
    DateTimeOffset At,
    string Command,
    string Caller,
    string CorrelationId,
    bool Success,
    string? ErrorCode,
    long ElapsedMs,
    bool DryRun,
    bool IsNested,
    string? BatchId,
    string? ArgsJson,
    string? ChangesJson);

/// <summary>引擎审计页（分页口径与 audit.query 一致）。</summary>
public sealed record AiAuditPage(
    IReadOnlyList<AiEngineCallRow> Items,
    int Total,
    int Page,
    int PageCount);

/// <summary>
/// 撤销上一轮 AI 变更的机器面回执（界面按键取词）：
/// <see cref="TotalCalls"/> = 本轮在撤销栈里仍有归属记录的可撤销调用数；<see cref="UndoneCalls"/> = 实际撤销数；
/// <see cref="MissingCalls"/> = 应撤销但栈里已不存在的数（如实计数，不猜）；<see cref="ErrorCode"/> = 首个失败的错误码（部分失败如实携带）。
/// </summary>
public sealed record AiUndoResult(
    int TotalCalls,
    int UndoneCalls,
    int MissingCalls,
    string? ErrorCode);

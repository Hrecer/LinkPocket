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
    IReadOnlyList<string>? ChangeIds = null);

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

/// <summary>一次审批（请求字段 + 决定的最终结果；理由原样回灌模型）。</summary>
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
    DateTimeOffset At);

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

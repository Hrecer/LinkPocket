using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.UI.Ai;

/// <summary>对话流里的一行（消息 / 工具调用卡 / 审批卡 / 提示条）：同一 VM、按 Kind 选模板。</summary>
public sealed class AiFeedItem : INotifyPropertyChanged
{
    public enum ItemKind
    {
        UserMessage = 0,
        AssistantMessage = 1,
        ToolCall = 2,
        Approval = 3,
        Notice = 4,
    }

    private string _text = "";
    private string _stateKey = "";
    private string _stateSuffix = "";
    private bool _isStreaming;
    private bool _isApprovalOpen;

    public required ItemKind Kind { get; init; }
    public required string ItemId { get; init; }
    public string? TurnId { get; init; }
    public string Command { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Detail { get; private set; } = "";
    public string? ApprovalId { get; init; }
    public bool IsDestructive { get; init; }
    public AiApproval? Approval { get; private set; }
    public AiToolCall? ToolCall { get; private set; }

    /// <summary>提示条的文案值（系统提示不是用户数据：走键 + 参数，渲染边界取词）。</summary>
    public LocValue NoticeValue { get; init; } = LocValue.Empty;

    private readonly List<AiChangeRow> _changes = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text
    {
        get => _text;
        private set
        {
            _text = value;
            Raise(nameof(Text));
        }
    }

    /// <summary>状态文案键（工具卡用；如 ai.tool.state.running）。</summary>
    public string StateKey
    {
        get => _stateKey;
        private set
        {
            _stateKey = value;
            Raise(nameof(StateKey));
        }
    }

    /// <summary>状态附加文本（耗时 / 错误码等机器面信息，原样显示）。</summary>
    public string StateSuffix
    {
        get => _stateSuffix;
        private set
        {
            _stateSuffix = value;
            Raise(nameof(StateSuffix));
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            _isStreaming = value;
            Raise(nameof(IsStreaming));
        }
    }

    /// <summary>审批卡是否仍可操作（已作决定 → 收起按钮）。</summary>
    public bool IsApprovalOpen
    {
        get => _isApprovalOpen;
        private set
        {
            _isApprovalOpen = value;
            Raise(nameof(IsApprovalOpen));
        }
    }

    /// <summary>工具卡内联的变更卡（一张卡 = 一次调用的全部变更；展开/收起由各行自持）。</summary>
    public IReadOnlyList<AiChangeRow> Changes => _changes;
    public bool HasChanges => _changes.Count > 0;

    /// <summary>"N 项变更"（含变量的整句 = LocValue，渲染边界取词）。</summary>
    public LocValue ChangeCountValue => Loc.K("ai.change.count", _changes.Count);

    // ── 审批卡（功能书 §8.2：做什么 / 动哪些对象 / 影响预览 / 将记住的作用域 / 逐步骤）──

    /// <summary>本地化动作短语键（缺命令名时回落到通用短语）。</summary>
    public string ActionKey => Command.Length > 0 ? LinkPocket.Views.AiKeyMap.Action(Command) : "ai.action.unknown";

    /// <summary>动哪些对象：已知名称原样（用户数据）→ 否则按数量 → 否则如实说"未指明"。</summary>
    public LocValue TargetValue
    {
        get
        {
            var approval = Approval;
            if (approval is null) return LocValue.Empty;
            if (approval.TargetNames.Count > 0) return LocValue.Literal(string.Join(", ", approval.TargetNames));
            return approval.TargetCount > 0
                ? Loc.K("count.itemsN", approval.TargetCount)
                : LocValue.Of("ai.approve.target.unknown");
        }
    }

    /// <summary>名称列表被截断（还有 N 个对象没列出来）——如实标注，不假装列全了。</summary>
    public bool HasMoreTargets
        => Approval is { TargetMore: > 0, TargetNames.Count: > 0 };

    public LocValue MoreTargetsValue => Loc.K("ai.approve.target.more", Approval?.TargetMore ?? 0);

    /// <summary>单一目标的 canonical 路径（可解析时）；显示走当前语言投影。</summary>
    public bool HasTargetPath => Approval?.TargetPath is not null;
    public LocValue TargetPathValue => LocValue.Projection(Approval?.TargetPath ?? "");

    /// <summary>批 / 宏的逐步骤影响（非批为空）。</summary>
    public IReadOnlyList<AiApprovalStepRow> ApprovalSteps
        => Approval?.Steps is { Count: > 0 } steps
            ? steps.Select(step => new AiApprovalStepRow(step)).ToArray()
            : [];
    public bool HasApprovalSteps => Approval?.Steps is { Count: > 0 };

    /// <summary>"批脚本步骤（共 N 步）"的标题（含变量整句 = LocValue）。</summary>
    public LocValue StepsTitleValue
        => Loc.K("ai.approve.steps.title", Approval?.Steps?.Count ?? 0);

    /// <summary>批 / 宏却读不到脚本 → 如实说明"只按命令审批"（不假装看过了）。</summary>
    public bool HasStepsNote => Command is "batch.run" or "macro.run" && !HasApprovalSteps;
    public string StepsNoteKey => "ai.approve.steps.unread";

    /// <summary>「本次会话总是允许」将记住的作用域（必须显示，不允许只显示"记住"）。</summary>
    public bool HasAllowScope => Approval?.AllowScope is not null;
    public LocValue AllowScopeValue
        => Approval?.AllowScope is { } scope ? Loc.K("ai.approve.allowScope", scope) : LocValue.Empty;

    /// <summary>影响预览：引擎下发的影响面 / 逐步骤 / 已知对象，三者都没有才如实说"无法预览"。</summary>
    public LocValue ImpactValue => LinkPocket.Views.AiKeyMap.Impact(Approval?.PreviewSummary);
    public bool HasImpact => Approval?.PreviewSummary is not null;
    public bool HasPreview => HasImpact || HasApprovalSteps
                               || Approval is { TargetCount: > 0 } or { TargetNames.Count: > 0 };

    public static AiFeedItem ForUser(AiMessage message)
        => new() { Kind = ItemKind.UserMessage, ItemId = message.MessageId, TurnId = message.TurnId, Text = message.Text };

    public static AiFeedItem ForAssistant(AiMessage message)
        => new()
        {
            Kind = ItemKind.AssistantMessage,
            ItemId = message.MessageId,
            TurnId = message.TurnId,
            Text = message.Text,
            IsStreaming = message.IsStreaming,
        };

    public static AiFeedItem ForTool(AiToolCall call)
    {
        var item = new AiFeedItem
        {
            Kind = ItemKind.ToolCall,
            ItemId = call.CallId,
            TurnId = call.TurnId,
            Command = call.Command,
            Summary = call.ArgsSummary ?? "",
            ToolCall = call,
        };
        return item.WithState(call);
    }

    public static AiFeedItem ForApproval(AiApproval approval)
        => new()
        {
            Kind = ItemKind.Approval,
            ItemId = approval.ApprovalId,
            TurnId = approval.TurnId,
            Command = approval.Command,
            ApprovalId = approval.ApprovalId,
            IsDestructive = approval.IsDestructive,
            Approval = approval,
            IsApprovalOpen = approval.Decision is null,
        };

    public static AiFeedItem ForNotice(string itemId, LocValue value)
        => new() { Kind = ItemKind.Notice, ItemId = itemId, NoticeValue = value };

    public void AppendDelta(string chunk) => Text += chunk;

    public void Finalize(AiMessage message)
    {
        Text = message.Text;
        IsStreaming = false;
    }

    public void Apply(AiToolCall call)
    {
        ToolCall = call;
        WithState(call);
    }

    public void Apply(AiApproval approval)
    {
        Approval = approval;
        IsApprovalOpen = approval.Decision is null;
        Raise(nameof(Approval));
        Raise(nameof(IsApprovalOpen));
        Raise(nameof(TargetValue));
        Raise(nameof(HasMoreTargets));
        Raise(nameof(MoreTargetsValue));
        Raise(nameof(HasTargetPath));
        Raise(nameof(TargetPathValue));
        Raise(nameof(ApprovalSteps));
        Raise(nameof(HasApprovalSteps));
        Raise(nameof(HasAllowScope));
        Raise(nameof(AllowScopeValue));
        Raise(nameof(ImpactValue));
        Raise(nameof(HasImpact));
        Raise(nameof(HasPreview));
    }

    /// <summary>挂一条本工具调用的变更（一张工具卡一张内联变更卡，见功能书 §7.5）。</summary>
    public void AttachChange(AiChangeRow row)
    {
        _changes.Add(row);
        Raise(nameof(Changes));
        Raise(nameof(HasChanges));
        Raise(nameof(ChangeCountValue));
    }

    private AiFeedItem WithState(AiToolCall call)
    {
        StateKey = LinkPocket.Views.AiKeyMap.ToolState(call.State);
        StateSuffix = call.ElapsedMs > 0 ? $"{call.ElapsedMs} ms" : (call.ErrorCode ?? "");
        if (call.ErrorCode is { Length: > 0 } code && call.ElapsedMs > 0) StateSuffix = $"{code} · {call.ElapsedMs} ms";
        Detail = call.ResultSummary ?? "";
        RaizeAll();
        return this;
    }

    private void RaizeAll()
    {
        Raise(nameof(ToolCall));
        Raise(nameof(StateKey));
        Raise(nameof(StateSuffix));
        Raise(nameof(Detail));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>批 / 宏审批卡里的一行「逐步骤影响」（步骤号与命令是机器面原样，对象是用户数据或数量）。</summary>
public sealed class AiApprovalStepRow(AiApprovalStep step)
{
    public AiApprovalStep Step { get; } = step;

    public int Index => Step.Index;
    public string Command => Step.Command;
    public bool IsDestructive => Step.IsDestructive;

    /// <summary>"步骤 N"（含变量整句 = LocValue，数字走 Invariant）。</summary>
    public LocValue IndexLabel => Loc.K("ai.approve.steps.row", Step.Index);

    /// <summary>本步骤的对象：名称（用户数据原样）→ 数量 → 未指明（模板占位符不当成值）。</summary>
    public LocValue TargetValue => Step.TargetName is { Length: > 0 } name
        ? LocValue.Literal(name)
        : Step.TargetCount > 0
            ? Loc.K("count.itemsN", Step.TargetCount)
            : LocValue.Of("ai.approve.target.unknown");

    /// <summary>错误策略（脚本没写 = 不显示该段）：已知取键，未知原样显示机器面标识符（不猜、不吞）。</summary>
    public bool HasOnError => Step.OnError is not null;
    public LocValue OnErrorValue => Step.OnError is null
        ? LocValue.Empty
        : LinkPocket.Views.AiKeyMap.OnError(Step.OnError) is { } key
            ? LocValue.Of(key)
            : LocValue.Literal(Step.OnError);
}

/// <summary>按 Kind 选模板（模板都在 AiView.xaml 的资源里，键名 = ai.template.*）。</summary>
public sealed class AiFeedTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ApprovalTemplate { get; set; }
    public DataTemplate? NoticeTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        AiFeedItem { Kind: AiFeedItem.ItemKind.UserMessage } => UserTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.AssistantMessage } => AssistantTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.ToolCall } => ToolTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.Approval } => ApprovalTemplate,
        _ => NoticeTemplate,
    };
}

/// <summary>字段级 diff 的一行（字段名走键，值里字符串 = 用户数据原样、布尔/空值走键）。</summary>
public sealed class AiFieldRow
{
    /// <summary>字段名 → 文案键（引擎的字段集固定，见 ENGINE-API §1；表外字段原样显示机器名）。</summary>
    private static readonly Dictionary<string, string> FieldKeys = new(StringComparer.Ordinal)
    {
        ["url"] = "ai.field.url",
        ["title"] = "ai.field.title",
        ["description"] = "ai.field.description",
        ["folder_id"] = "ai.field.folderId",
        ["is_important"] = "ai.field.isImportant",
        ["favicon_url"] = "ai.field.faviconUrl",
        ["name"] = "ai.field.name",
        ["parent_id"] = "ai.field.parentId",
    };

    public required AiFieldChange Field { get; init; }

    public LocValue FieldName => FieldKeys.TryGetValue(Field.Field, out var key)
        ? LocValue.Of(key)
        : LocValue.Literal(Field.Field);

    public LocValue Before => ToValue(Field.Before);
    public LocValue After => ToValue(Field.After);

    private static LocValue ToValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => LocValue.Literal(value.Value.GetString() ?? ""),
        JsonValueKind.True => LocValue.Of("ai.diff.bool.true"),
        JsonValueKind.False => LocValue.Of("ai.diff.bool.false"),
        // 会话文件往返后"空值"与"不适用"统一为 null：界面照实说"空"
        null or JsonValueKind.Null or JsonValueKind.Undefined => LocValue.Of("ai.diff.empty"),
        _ => LocValue.Literal(value.Value.GetRawText()),
    };
}

/// <summary>台账一行（右栏）：分类 / 实体 / 结果都是键或机器面标识符，不拼界面文案。
/// 字段级明细默认折叠（前 5 条），展开后全量（功能书 §7.5）。</summary>
public sealed class AiChangeRow : INotifyPropertyChanged
{
    /// <summary>折叠时显示的字段条数。</summary>
    public const int CollapsedFieldCount = 5;

    private bool _isExpanded;
    private IReadOnlyList<AiFieldRow>? _fields;

    public required AiChange Change { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string KindKey => $"ai.change.kind.{Change.Kind.ToString().ToLowerInvariant()}";
    public string EntityLabel => Change.EntityName is { Length: > 0 } name ? name : Change.EntityId;
    public string EntitySuffix => $"{Change.EntityType} · {Change.Command}";
    public string OutcomeLabel => Change.Outcome.ToString();
    public string UndoableKey => Change.Undoable ? "ai.change.undoable" : "ai.change.notUndoable";
    public string? Reason => Change.ErrorCode;

    /// <summary>实体位置（canonical → 当前语言显示投影）；未知/无位置时为空。</summary>
    public LocValue PathValue => Change.EntityPath is { Length: > 0 } path
        ? LocValue.Projection(path)
        : LocValue.Empty;

    public IReadOnlyList<AiFieldRow> Fields
        => _fields ??= (Change.Fields ?? []).Select(field => new AiFieldRow { Field = field }).ToArray();

    public bool HasFields => Fields.Count > 0;
    public bool CanExpand => Fields.Count > CollapsedFieldCount;

    /// <summary>折叠 = 前 5 条；展开 = 全量。</summary>
    public IReadOnlyList<AiFieldRow> VisibleFields => IsExpanded || !CanExpand
        ? Fields
        : Fields.Take(CollapsedFieldCount).ToArray();

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(VisibleFields));
            Raise(nameof(ExpandKey));
        }
    }

    /// <summary>展开 / 收起的按钮文案键。</summary>
    public string ExpandKey => IsExpanded ? "ai.diff.collapse" : "ai.diff.expand";

    /// <summary>粒度来源标注：只有"仅实体级"需要如实标注（引擎 diff 是默认口径）。</summary>
    public string? SourceKey => Change.Source == AiChangeSource.EntityOnly ? "ai.diff.source.entityOnly" : null;
    public bool HasSourceNote => SourceKey is not null;

    /// <summary>上游截断如实标注（"另有 N 项未列出"）。</summary>
    public LocValue TruncatedValue => Change.Truncated
        ? Loc.K("ai.diff.truncated", Change.Omitted)
        : LocValue.Empty;
    public bool IsTruncated => Change.Truncated;

    public void ToggleExpand() => IsExpanded = !IsExpanded;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>右栏「引擎审计」页签的一行（机器面字段原样；时间走时钟投影、结果走键）。</summary>
public sealed class AiEngineRow : INotifyPropertyChanged
{
    private bool _payloadOpen;

    public required AiEngineCallRow Call { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Command => Call.Command;
    public string Caller => Call.Caller;
    public string CorrelationId => Call.CorrelationId;

    /// <summary>时间走 <see cref="LocValue.Clock"/>（存身份、渲染边界取词，见 WARNINGS 97）。</summary>
    public LocValue TimeValue => LocValue.Clock(Call.At.LocalDateTime, false);

    public string ResultKey => Call.Success ? "ai.audit.result.ok" : "ai.audit.result.failed";
    public string? ErrorCode => Call.ErrorCode;
    public string ElapsedText => $"{Call.ElapsedMs} ms";
    public bool DryRun => Call.DryRun;
    public bool IsNested => Call.IsNested;
    public string? BatchId => Call.BatchId;

    /// <summary>载荷（机器的入参快照与变更载荷；默认收起，功能书 §7.6 的"可开关载荷"）。</summary>
    public string? PayloadText => _payloadOpen
        ? $"args: {Call.ArgsJson ?? "-"}\nchanges: {Call.ChangesJson ?? "-"}"
        : null;

    public bool HasPayload => Call.ArgsJson is { Length: > 0 } || Call.ChangesJson is { Length: > 0 };

    public bool IsPayloadOpen
    {
        get => _payloadOpen;
        private set
        {
            if (_payloadOpen == value) return;
            _payloadOpen = value;
            Raise(nameof(IsPayloadOpen));
            Raise(nameof(PayloadText));
            Raise(nameof(PayloadKey));
        }
    }

    public string PayloadKey => IsPayloadOpen ? "ai.diff.collapse" : "ai.audit.payloads.show";

    public void TogglePayload() => IsPayloadOpen = !IsPayloadOpen;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

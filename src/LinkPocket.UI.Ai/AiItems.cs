using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;

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

    public static AiFeedItem ForApproval(AiApproval approval, string targetSummary)
        => new()
        {
            Kind = ItemKind.Approval,
            ItemId = approval.ApprovalId,
            TurnId = approval.TurnId,
            Command = approval.Command,
            ApprovalId = approval.ApprovalId,
            IsDestructive = approval.IsDestructive,
            Text = targetSummary,
            Approval = approval,
            IsApprovalOpen = approval.Decision is null,
        };

    public static AiFeedItem ForNotice(string itemId, string text)
        => new() { Kind = ItemKind.Notice, ItemId = itemId, Text = text };

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

/// <summary>台账一行（右栏）：分类 / 实体 / 结果都是键或机器面标识符，不拼界面文案。</summary>
public sealed class AiChangeRow
{
    public required AiChange Change { get; init; }
    public string KindKey => $"ai.change.kind.{Change.Kind.ToString().ToLowerInvariant()}";
    public string EntityLabel => Change.EntityName is { Length: > 0 } name ? name : Change.EntityId;
    public string EntitySuffix => $"{Change.EntityType} · {Change.Command}";
    public string OutcomeLabel => Change.Outcome.ToString();
    public string UndoableKey => Change.Undoable ? "ai.change.undoable" : "ai.change.notUndoable";
    public string? Reason => Change.ErrorCode;
}


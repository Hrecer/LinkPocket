using System.Collections.ObjectModel;
using System.ComponentModel;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 助手页 VM：三栏（会话 / 对话 / 变更与审计）。
/// **UI 是纯投影**：会话、回合、台账、冻结状态全在 <see cref="IAiAssistant"/> 侧，本类只订阅 + 投影。
/// 文案一律存**键**（视图用 <c>{loc:LocKey X}</c> 取词），不存成品字符串。
/// </summary>
public sealed partial class AiViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAiAssistant _assistant;

    private string? _activeSessionId;
    private string _composerText = "";
    private string _statusKey = "ai.status.idle";
    private bool _isTurnRunning;
    private AiMode _mode = AiMode.ConfirmEach;
    private bool _isConfigured;
    private string _selectionErrorKey = "";
    private string? _lastErrorKey;

    public AiViewModel(IAiAssistant assistant)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _assistant.Notified += OnNotified;
        _assistant.WriteFreezeChanged += OnFreezeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AiSessionSummary> Sessions { get; } = [];
    public ObservableCollection<AiFeedItem> Feed { get; } = [];
    public ObservableCollection<AiChangeRow> TurnChanges { get; } = [];
    public ObservableCollection<AiChangeRow> SessionChanges { get; } = [];
    public ObservableCollection<AiApprovalRow> Approvals { get; } = [];

    /// <summary>写入冻结（投影自引擎写锁；Shell 侧还有一道全局门）。</summary>
    public bool IsWriteFrozen => _assistant.IsWriteFrozen;

    public bool IsTurnRunning
    {
        get => _isTurnRunning;
        private set
        {
            if (!Set(ref _isTurnRunning, value, nameof(IsTurnRunning))) return;
            Raise(nameof(CanUndoSession));   // 回合在跑时不给撤销（引擎写面正被占用）
            Raise(nameof(IsProgressVisible));
            if (!value)
            {
                ClearProgress();
                ClearStatusOverride();
            }
        }
    }

    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (!Set(ref _composerText, value, nameof(ComposerText))) return;
            Raise(nameof(CanSend));   // 可用性同帧跟上（含"/"开头的斜杠命令在未配置时也可发）
            CommandRefresh.Request();
        }
    }

    /// <summary>状态行文案键（取词在视图侧）。</summary>
    public string StatusKey
    {
        get => _statusKey;
        private set
        {
            if (!Set(ref _statusKey, value, nameof(StatusKey))) return;
            Raise(nameof(StatusValue));
        }
    }

    public string? LastErrorKey
    {
        get => _lastErrorKey;
        private set => Set(ref _lastErrorKey, value, nameof(LastErrorKey));
    }

    public AiMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value, nameof(Mode))) return;
            Raise(nameof(ModeIndex));
        }
    }

    /// <summary>模式分段控件的选中索引（只读 / 每次确认 / 自动应用）。</summary>
    public int ModeIndex
    {
        get => (int)Mode;
        set
        {
            if (value < 0 || value > 2 || value == (int)Mode) return;
            _ = SetModeAsync((AiMode)value);
        }
    }

    public bool IsConfigured
    {
        get => _isConfigured;
        private set => Set(ref _isConfigured, value, nameof(IsConfigured));
    }

    /// <summary>未配置时的引导文案键（取词在视图侧）。</summary>
    public string SelectionErrorKey
    {
        get => _selectionErrorKey;
        private set => Set(ref _selectionErrorKey, value, nameof(SelectionErrorKey));
    }

    public string? ActiveSessionId => _activeSessionId;

    private string? _openApprovalId;

    /// <summary>有待批审批的 ID（null = 没有待批）。视图据此把**默认焦点放到「拒绝」**
    /// （功能书 §8.2 交互红线：默认焦点落在拒绝上，不给"顺手回车就放行"留门）。</summary>
    public string? OpenApprovalId
    {
        get => _openApprovalId;
        private set
        {
            if (!Set(ref _openApprovalId, value, nameof(OpenApprovalId))) return;
            if (value is not null) ApprovalFocusRequested?.Invoke(value);
        }
    }

    /// <summary>出现了一张待批审批（视图把焦点移到该卡的「拒绝」按钮）。</summary>
    public event Action<string>? ApprovalFocusRequested;

    /// <summary>进页对齐：刷新选择与服务商状态；无会话则建一个（缺省模式取偏好）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            RefreshSelection();
            var sessions = await _assistant.ListSessionsAsync().ConfigureAwait(true);
            Sessions.Clear();
            foreach (var session in sessions) Sessions.Add(session);
            if (Sessions.Count == 0)
            {
                var created = await _assistant.CreateSessionAsync().ConfigureAwait(true);
                Sessions.Insert(0, created);
            }
            var target = _activeSessionId is { } id && Sessions.Any(s => s.SessionId == id)
                ? id
                : Sessions[0].SessionId;
            await OpenSessionAsync(target).ConfigureAwait(true);
            await RefreshSkillsAsync().ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>新建会话并切过去。</summary>
    public async Task NewSessionAsync()
    {
        try
        {
            var created = await _assistant.CreateSessionAsync().ConfigureAwait(true);
            Sessions.Insert(0, created);
            await OpenSessionAsync(created.SessionId).ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>切到某个会话（重投影对话流、台账、审批）。</summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        _activeSessionId = sessionId;
        Raise(nameof(ActiveSessionId));
        var detail = await _assistant.GetSessionAsync(sessionId).ConfigureAwait(true);
        Mode = detail.Summary.Mode;

        Feed.Clear();
        foreach (var message in detail.Messages)
            Feed.Add(message.Role == AiRole.User ? AiFeedItem.ForUser(message) : AiFeedItem.ForAssistant(message));
        foreach (var call in detail.ToolCalls) Feed.Add(AiFeedItem.ForTool(call));
        foreach (var approval in detail.Approvals)
            Feed.Add(AiFeedItem.ForApproval(approval));
        OpenApprovalId = detail.Approvals.LastOrDefault(a => a.Decision is null)?.ApprovalId;   // 进页仍待批 → 焦点给「拒绝」

        ApplySession(detail);
        AttachInlineChanges();

        IsTurnRunning = detail.Summary.ActiveTurnState is AiTurnState.Pending or AiTurnState.Streaming
            or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval;
        StatusKey = IsTurnRunning ? "ai.status.running" : "ai.status.idle";
        ClearMentions();   // 提及 chip 是"待发集合"：换会话即清（不跨会话带走）
        _ = RefreshUsageAsync();
    }

    public async Task DeleteSessionAsync(AiSessionSummary session)
    {
        try
        {
            await _assistant.DeleteSessionAsync(session.SessionId).ConfigureAwait(true);
            Sessions.Remove(session);
            if (_activeSessionId == session.SessionId)
            {
                if (Sessions.Count == 0) await NewSessionAsync().ConfigureAwait(true);
                else await OpenSessionAsync(Sessions[0].SessionId).ConfigureAwait(true);
            }
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>导出当前会话（缺省 Markdown；CSV = 台账逐条、JSON = 完整会话文件）。</summary>
    public async Task<bool> ExportSessionAsync(string outputPath, AiExportFormat format = AiExportFormat.Markdown)
    {
        if (_activeSessionId is not { } sessionId) return false;
        try
        {
            await _assistant.ExportAsync(sessionId, format, outputPath).ConfigureAwait(true);
            return true;
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
            return false;
        }
    }

    public async Task SetModeAsync(AiMode mode)
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            await _assistant.SetModeAsync(sessionId, mode).ConfigureAwait(true);
            Mode = mode;
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    private void RefreshSelection()
    {
        var resolution = _assistant.ResolveSelection();
        IsConfigured = resolution.Selection is not null;
        SelectionErrorKey = resolution.Issue switch
        {
            AiSelectionIssue.ProviderMissing => "ai.hint.configure",
            AiSelectionIssue.ApiKeyMissing => "ai.hint.apiKey",
            AiSelectionIssue.ModelMissing or AiSelectionIssue.ModelDisabled => "ai.hint.selectModel",
            AiSelectionIssue.CapabilityMissing => "ai.hint.capability",
            _ => "",
        };
    }

    private static string DescribeTarget(AiApproval approval)
        => approval.TargetNames.Count > 0 ? string.Join(", ", approval.TargetNames) : "";

    private void OnFreezeChanged() => Raise(nameof(IsWriteFrozen));

    private void OnNotified(AiNotification notification)
    {
        switch (notification.Kind)
        {
            case AiNotificationKind.MessageAdded when notification.Message is { } message:
                UpsertMessage(message);
                break;
            case AiNotificationKind.StreamDelta when notification.TextDelta is { } delta:
                var streaming = Feed.FirstOrDefault(i => i.ItemId == notification.MessageId);
                if (streaming is null)
                {
                    var shell = AiFeedItem.ForAssistant(new AiMessage(notification.MessageId ?? "", 0, AiRole.Assistant, "",
                        DateTimeOffset.UtcNow, notification.TurnId, IsStreaming: true));
                    Feed.Add(shell);
                    shell.AppendDelta(delta);
                }
                else
                {
                    streaming.AppendDelta(delta);
                }
                break;
            case AiNotificationKind.ToolCallChanged when notification.ToolCall is { } call:
                UpsertTool(call);
                break;
            case AiNotificationKind.ApprovalChanged when notification.Approval is { } approval:
                UpsertApproval(approval);
                break;
            case AiNotificationKind.ChangeRecorded when notification.Change is { } change:
                if (notification.SessionId == _activeSessionId) OnChangeRecorded(change);
                break;
            case AiNotificationKind.TurnChanged when notification.Turn is { } turn:
                if (notification.SessionId != _activeSessionId) break;
                IsTurnRunning = turn.State is AiTurnState.Pending or AiTurnState.Streaming or AiTurnState.ToolRunning
                    or AiTurnState.AwaitingApproval;
                StatusKey = turn.State switch
                {
                    AiTurnState.AwaitingApproval => "ai.status.awaiting",
                    AiTurnState.ToolRunning => "ai.status.running",
                    AiTurnState.Streaming => "ai.status.running",
                    AiTurnState.Completed => "ai.status.idle",
                    AiTurnState.Failed => "ai.status.failed",
                    AiTurnState.Cancelled => "ai.status.stopped",
                    _ => StatusKey,
                };
                if (turn.State is AiTurnState.Completed or AiTurnState.Failed or AiTurnState.Cancelled
                    or AiTurnState.Interrupted)
                    OnTurnSettled();
                break;
            case AiNotificationKind.SessionChanged when notification.Session is { } summary:
                var index = Sessions.ToList().FindIndex(s => s.SessionId == summary.SessionId);
                if (index >= 0) Sessions[index] = summary;
                else Sessions.Insert(0, summary);
                break;
            case AiNotificationKind.ContextCompacted when notification.Compaction is { } compaction:
                if (notification.SessionId != _activeSessionId) break;
                Notice(compaction.Failed
                    ? Loc.K("ai.context.failed")
                    : compaction.IsSummary
                        ? Loc.K("ai.context.summarized")
                        : Loc.K("ai.context.micro", compaction.ClearedToolResults, compaction.TokensSaved));
                break;
            case AiNotificationKind.ToolProgress when notification.Progress is { } progress:
                if (notification.SessionId != _activeSessionId) break;
                ApplyProgress(progress);
                break;
            case AiNotificationKind.RateLimited when notification.RateLimit is { } rateLimit:
                if (notification.SessionId != _activeSessionId) break;
                ApplyRateLimit(rateLimit);
                break;
        }
    }

    private void UpsertMessage(AiMessage message)
    {
        if (message.Role == AiRole.User && message.TurnId is null) return;
        var existing = Feed.FirstOrDefault(i => i.ItemId == message.MessageId);
        if (existing is null) Feed.Add(AiFeedItem.ForAssistant(message));
        else existing.Finalize(message);
    }

    private void UpsertTool(AiToolCall call)
    {
        var existing = Feed.FirstOrDefault(i => i.ItemId == call.CallId);
        if (existing is null) Feed.Add(AiFeedItem.ForTool(call));
        else existing.Apply(call);
    }

    private void UpsertApproval(AiApproval approval)
    {
        var existing = Feed.FirstOrDefault(i => i.ItemId == approval.ApprovalId);
        if (existing is null) Feed.Add(AiFeedItem.ForApproval(approval));
        else existing.Apply(approval);

        // 待批 → 焦点请求；作决定 → 撤下（同一张卡只请求一次）
        if (approval.Decision is null) OpenApprovalId = approval.ApprovalId;
        else if (_openApprovalId == approval.ApprovalId) OpenApprovalId = null;

        OnApprovalRecorded(approval);
    }

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _assistant.Notified -= OnNotified;
        _assistant.WriteFreezeChanged -= OnFreezeChanged;
    }
}

/// <summary>右栏审批记录行。</summary>
public sealed class AiApprovalRow(AiApproval approval, string targetSummary) : INotifyPropertyChanged
{
    private AiApproval _approval = approval;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ApprovalId => _approval.ApprovalId;
    public string Command => _approval.Command;
    public string Target => targetSummary;
    public bool IsDestructive => _approval.IsDestructive;
    public bool IsOpen => _approval.Decision is null;
    public string DecisionText => _approval.Decision?.ToString() ?? "";
    public string? Reason => _approval.Reason;
    public bool HasDecision => _approval.Decision is not null;

    /// <summary>待批时用于界面按钮的启用（视图侧按钮的可见性判据）。</summary>
    public bool ShowActions => IsOpen;

    public void Apply(AiApproval approval)
    {
        _approval = approval;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecisionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDecision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActions)));
    }
}

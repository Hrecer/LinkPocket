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
    private bool _isPanelCollapsed;
    private bool _isConversationEmpty = true;
    private AiModelOption? _selectedModel;

    public AiViewModel(IAiAssistant assistant)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _assistant.Notified += OnNotified;
        _assistant.WriteFreezeChanged += OnFreezeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>左栏会话清单（行模型：标题 / 时间 / 运行状态 / 变更数徽标）。</summary>
    public ObservableCollection<AiSessionRow> Sessions { get; } = [];

    /// <summary>对话流（平铺：回合分隔行 + 消息 + 工具行 + 审批卡 + 提示条）。</summary>
    public ObservableCollection<AiFeedItem> Feed { get; } = [];

    /// <summary>轮次导航轨的条目（= 对话流里的回合分隔行，按序；视图据此滚动定位与高亮）。</summary>
    public ObservableCollection<AiFeedItem> Turns { get; } = [];

    /// <summary>导航轨显隐（只有一轮时不占位）。</summary>
    public bool ShowRail => Turns.Count > 1;

    public ObservableCollection<AiChangeRow> TurnChanges { get; } = [];
    public ObservableCollection<AiChangeRow> SessionChanges { get; } = [];
    public ObservableCollection<AiApprovalRow> Approvals { get; } = [];

    /// <summary>模型下拉（全部已启用模型；选择写助手偏好，与 `/model` 同一条路径）。</summary>
    public ObservableCollection<AiModelOption> Models { get; } = [];

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
            Raise(nameof(ShowComposerPlaceholder));
            CommandRefresh.Request();
        }
    }

    /// <summary>输入框占位提示的显隐（空文本才显示；WPF 的 TextBox 没有原生占位符）。</summary>
    public bool ShowComposerPlaceholder => _composerText.Length == 0;

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
        private set
        {
            if (!Set(ref _isConfigured, value, nameof(IsConfigured))) return;
            Raise(nameof(ShowGuide));
        }
    }

    /// <summary>未配置时的引导文案键（取词在视图侧）。</summary>
    public string SelectionErrorKey
    {
        get => _selectionErrorKey;
        private set => Set(ref _selectionErrorKey, value, nameof(SelectionErrorKey));
    }

    public string? ActiveSessionId => _activeSessionId;

    /// <summary>右栏「变更与审计」的收起态（顶栏开关；收起后不再占宽）。</summary>
    public bool IsPanelCollapsed
    {
        get => _isPanelCollapsed;
        set
        {
            if (Set(ref _isPanelCollapsed, value, nameof(IsPanelCollapsed))) Raise(nameof(PanelToggleKey));
        }
    }

    public string PanelToggleKey => IsPanelCollapsed ? "ai.panel.expand" : "ai.panel.collapse";

    public void TogglePanel() => IsPanelCollapsed = !IsPanelCollapsed;

    /// <summary>对话区空态（只剩会话头 / 什么都没有）：显示引导卡（三条示例提示 + 模式说明）。</summary>
    public bool IsConversationEmpty => _isConversationEmpty;

    /// <summary>引导卡是否显示（空对话时显示；未配置服务商时同卡换成配置引导）。</summary>
    public bool ShowGuide => _isConversationEmpty;

    /// <summary>模型下拉的当前值（无启用模型 = 提示去配置）。</summary>
    public AiModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!Set(ref _selectedModel, value, nameof(SelectedModel))) return;
            Raise(nameof(ModelLabelValue));
            _ = ApplyModelAsync(value);
        }
    }

    public bool HasModels => Models.Count > 0;

    /// <summary>「新建会话」行的键位提示（取自键位总表，界面不写死键位串；机器面文本与语言无关）。</summary>
    public string NewSessionShortcut { get; } = ComputeNewSessionShortcut();

    private static string ComputeNewSessionShortcut()
    {
        var spec = LinkPocket.Input.ShortcutCatalog.For(LinkPocket.Input.ShortcutPage.Ai).Specs
            .FirstOrDefault(s => string.Equals(s.ActionId, LinkPocket.Input.ShortcutAction.AiNewSession,
                StringComparison.Ordinal));
        return spec is null ? "" : LinkPocket.Input.ShortcutBinding.FormatGesture(spec.Key, spec.Modifiers);
    }

    /// <summary>模型下拉的按钮文案（机器面 `供应商 / 模型` 原样；没有可用模型时按键取词）。</summary>
    public LocValue ModelLabelValue => _selectedModel is { } option
        ? LocValue.Literal(option.Label)
        : LocValue.Of("ai.composer.model.none");

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

    /// <summary>进页对齐：刷新选择 / 模型清单 / 会话清单；无会话则建一个（缺省模式取偏好）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            RefreshSelection();
            await RefreshModelsAsync().ConfigureAwait(true);
            var sessions = await _assistant.ListSessionsAsync().ConfigureAwait(true);
            Sessions.Clear();
            foreach (var session in sessions) Sessions.Add(new AiSessionRow(session));
            if (Sessions.Count == 0)
            {
                var created = await _assistant.CreateSessionAsync().ConfigureAwait(true);
                Sessions.Insert(0, new AiSessionRow(created));
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
            Sessions.Insert(0, new AiSessionRow(created));
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

        BuildFeed(detail);
        OpenApprovalId = detail.Approvals.LastOrDefault(a => a.Decision is null)?.ApprovalId;   // 进页仍待批 → 焦点给「拒绝」

        ApplySession(detail);
        AttachInlineChanges();

        IsTurnRunning = detail.Summary.ActiveTurnState is AiTurnState.Pending or AiTurnState.Streaming
            or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval;
        StatusKey = IsTurnRunning ? "ai.status.running" : "ai.status.idle";
        ClearMentions();   // 提及 chip 是"待发集合"：换会话即清（不跨会话带走）
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// 对话流整份重投影：**平铺**（回合分隔行 + 该轮条目，各按 Seq 排）——折叠只改显隐，条目一个不丢。
    /// 找不到回合记录的孤儿条目照样上屏（自带一个如实的分隔行），绝不静默吞数据。
    /// </summary>
    private void BuildFeed(AiSessionDetail detail)
    {
        Feed.Clear();
        Turns.Clear();
        var order = detail.Turns.OrderBy(t => t.Index).ToList();
        var known = order.Select(t => t.TurnId).ToHashSet(StringComparer.Ordinal);
        var fallbackIndex = order.Count;

        var timeline = new List<(string TurnId, int Seq, AiFeedItem Item)>();
        foreach (var message in detail.Messages)
            timeline.Add((message.TurnId ?? "", message.Seq, message.Role == AiRole.User
                ? AiFeedItem.ForUser(message)
                : AiFeedItem.ForAssistant(message)));
        foreach (var call in detail.ToolCalls) timeline.Add((call.TurnId, call.Seq, AiFeedItem.ForTool(call)));
        foreach (var approval in detail.Approvals)
            timeline.Add((approval.TurnId, approval.Seq, AiFeedItem.ForApproval(approval)));

        var groups = timeline.GroupBy(x => x.TurnId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Seq).Select(x => x.Item).ToList(), StringComparer.Ordinal);

        foreach (var turn in order)
        {
            var header = AiFeedItem.ForTurn(turn);
            header.SetPreview(PreviewOf(groups, turn.TurnId));
            Feed.Add(header);
            Turns.Add(header);
            foreach (var item in groups.GetValueOrDefault(turn.TurnId, [])) Feed.Add(item);
            groups.Remove(turn.TurnId);
        }

        foreach (var (turnId, items) in groups.OrderBy(g => g.Value.FirstOrDefault()?.At ?? DateTimeOffset.MaxValue))
        {
            var header = AiFeedItem.ForTurn(new AiTurn(turnId, ++fallbackIndex, AiTurnState.Completed,
                items.FirstOrDefault()?.At ?? DateTimeOffset.UtcNow, null, null, 0, 0, 0, false));
            Feed.Add(header);
            Turns.Add(header);
            foreach (var item in items) Feed.Add(item);
        }

        if (Turns.LastOrDefault() is { } latest) latest.IsActive = true;
        NotifyFeedShape();
    }

    /// <summary>该轮的首条用户消息（导航轨悬停提示；没有用户消息 = 空）。</summary>
    private static string PreviewOf(IReadOnlyDictionary<string, List<AiFeedItem>> groups, string turnId)
        => groups.GetValueOrDefault(turnId, [])
            .FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.UserMessage)?.Text ?? "";

    /// <summary>会话行清单按摘要就地更新（新会话插入 / 已有会话改状态）。</summary>
    private void UpsertSession(AiSessionSummary summary)
    {
        var index = Sessions.ToList().FindIndex(s => s.SessionId == summary.SessionId);
        if (index >= 0) Sessions[index].Apply(summary);
        else Sessions.Insert(0, new AiSessionRow(summary));
    }

    public async Task DeleteSessionAsync(AiSessionRow session)
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

    /// <summary>秒针（视图每秒调一次）：运行中的回合分隔行重投影"已用 N 秒"。</summary>
    public void Tick()
    {
        foreach (var item in Feed)
            if (item.Kind == AiFeedItem.ItemKind.TurnHeader) item.TickRunning();
    }

    /// <summary>折叠 / 展开一轮（只改显隐，不删任何条目）。</summary>
    public void ToggleTurn(AiFeedItem header)
    {
        if (header.Kind != AiFeedItem.ItemKind.TurnHeader) return;
        header.ToggleCollapsed();
        var index = Feed.IndexOf(header);
        if (index < 0) return;
        for (var i = index + 1; i < Feed.Count && Feed[i].Kind != AiFeedItem.ItemKind.TurnHeader; i++)
            Feed[i].SetHiddenByTurn(header.IsCollapsed);
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

    /// <summary>模型清单（已启用模型；当前值 = 助手偏好里的选择，不在清单里 = 未选）。</summary>
    private async Task RefreshModelsAsync()
    {
        try
        {
            var providers = await _assistant.ListProvidersAsync().ConfigureAwait(true);
            var preferences = await _assistant.GetPreferencesAsync().ConfigureAwait(true);
            var options = providers
                .SelectMany(p => p.Models.Where(m => m.Enabled)
                    .Select(m => new AiModelOption(p.Id, m.Id, $"{p.Id}/{m.Id}")))
                .ToList();
            Models.Clear();
            foreach (var option in options) Models.Add(option);
            Set(ref _selectedModel,
                options.FirstOrDefault(o => o.ProviderId == preferences.ProviderId && o.ModelId == preferences.ModelId),
                nameof(SelectedModel));
            Raise(nameof(SelectedModel));
            Raise(nameof(ModelLabelValue));
            Raise(nameof(HasModels));
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>下拉选模型 = 写助手偏好（与 `/model` 同一条路径：全局默认模型，下一轮生效）。</summary>
    private async Task ApplyModelAsync(AiModelOption? option)
    {
        if (option is null || _activeSessionId is null) return;
        try
        {
            var preferences = await _assistant.GetPreferencesAsync().ConfigureAwait(true);
            if (preferences.ProviderId == option.ProviderId && preferences.ModelId == option.ModelId) return;
            await _assistant.SavePreferencesAsync(preferences with
            {
                ProviderId = option.ProviderId,
                ModelId = option.ModelId,
            }).ConfigureAwait(true);
            RefreshSelection();
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
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
                    AppendToTurn(shell);
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
                HeaderOf(notification.TurnId ?? turn.TurnId)?.ApplyTurn(turn);
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
                UpsertSession(summary);
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
        if (existing is not null)
        {
            existing.Finalize(message);
            return;
        }
        // 角色决定模板（用户消息走气泡、助手消息走 Markdown 文本）——存续与实时两条路径必须同形
        AppendToTurn(message.Role == AiRole.User ? AiFeedItem.ForUser(message) : AiFeedItem.ForAssistant(message));
    }

    private void UpsertTool(AiToolCall call)
    {
        var existing = Feed.FirstOrDefault(i => i.ItemId == call.CallId);
        if (existing is null) AppendToTurn(AiFeedItem.ForTool(call));
        else existing.Apply(call);
    }

    private void UpsertApproval(AiApproval approval)
    {
        var existing = Feed.FirstOrDefault(i => i.ItemId == approval.ApprovalId);
        if (existing is null) AppendToTurn(AiFeedItem.ForApproval(approval));
        else existing.Apply(approval);

        // 待批 → 焦点请求；作决定 → 撤下（同一张卡只请求一次）
        if (approval.Decision is null) OpenApprovalId = approval.ApprovalId;
        else if (_openApprovalId == approval.ApprovalId) OpenApprovalId = null;

        OnApprovalRecorded(approval);
    }

    /// <summary>把新条目插到它所属回合的末尾（回合没有分隔行就先补一条——新回合的第一条消息先到）；
    /// 折叠态回合内的新条目同样隐藏。</summary>
    private void AppendToTurn(AiFeedItem item)
    {
        var at = -1;
        if (item.TurnId is { } turnId)
        {
            var header = EnsureHeader(turnId, item.At);
            if (item.Kind == AiFeedItem.ItemKind.UserMessage) header.SetPreview(item.Text);
            for (var i = Feed.Count - 1; i >= 0; i--)
                if (Feed[i].TurnId == turnId)
                {
                    at = i + 1;
                    break;
                }
        }
        if (at < 0) Feed.Add(item);
        else Feed.Insert(at, item);
        item.SetHiddenByTurn(HeaderOf(item.TurnId)?.IsCollapsed ?? false);
        NotifyFeedShape();
    }

    /// <summary>该回合的分隔行（没有就地补一条：回合序号按现有轮数续，随后的 TurnChanged 会用真读数覆盖）。</summary>
    private AiFeedItem EnsureHeader(string turnId, DateTimeOffset at)
    {
        if (HeaderOf(turnId) is { } existing) return existing;
        var header = AiFeedItem.ForTurn(new AiTurn(turnId, Turns.Count + 1, AiTurnState.Pending, at,
            null, null, 0, 0, 0, false));
        Feed.Add(header);
        Turns.Add(header);
        Raise(nameof(ShowRail));
        return header;
    }

    private AiFeedItem? HeaderOf(string? turnId) => turnId is null
        ? null
        : Feed.FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.TurnHeader && i.TurnId == turnId);

    /// <summary>空态判定（只剩回合头 = 空；提示条也算内容，如实照显示）。</summary>
    private void NotifyFeedShape()
    {
        var empty = !Feed.Any(i => i.Kind != AiFeedItem.ItemKind.TurnHeader);
        if (empty != _isConversationEmpty)
        {
            _isConversationEmpty = empty;
            Raise(nameof(IsConversationEmpty));
            Raise(nameof(ShowGuide));
        }
        Raise(nameof(ShowRail));
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

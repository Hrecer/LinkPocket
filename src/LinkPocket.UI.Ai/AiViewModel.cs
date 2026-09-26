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
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    private string? _activeSessionId;
    private string _composerText = "";
    private string _statusKey = "ai.status.idle";
    private bool _isTurnRunning;
    private AiMode _mode = AiMode.ConfirmEach;
    private bool _isConfigured;
    private string? _lastErrorKey;
    private AiModelOption? _selectedModel;

    public AiViewModel(IAiAssistant assistant)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        // VM 在 UI 线程上构造（视图 Configure 里 new）：把该线程记下来当投影线程。
        // 兜底 = 应用主 Dispatcher（测试宿主会在别的线程上构造 VM，取应用级的那支才对）。
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread)
                     ?? System.Windows.Application.Current?.Dispatcher
                     ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
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

    /// <summary>审批行（对话内审批卡的**回查源**：按钮按审批 ID 从这里取行，见 AiViewModel.Ledger）。</summary>
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
            Raise(nameof(CanSend));          // 回合在跑时发送置灰（不允许并发；停止后可发）
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

    public string? ActiveSessionId => _activeSessionId;

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

    /// <summary>进页对齐：刷新选择 / 模型清单 / 会话清单。**不自动建会话**——一条会话都没有就是空态
    /// （用户点「新建会话」或直接发消息时才建，见 <see cref="NewSessionAsync"/>）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            RetireDraft();   // 进页对齐 = 回到权威列表：上一轮留下的未用草稿在此回收（正式会话不受影响）
            RefreshSelection();
            await RefreshModelsAsync().ConfigureAwait(true);
            var sessions = await _assistant.ListSessionsAsync().ConfigureAwait(true);
            Sessions.Clear();
            foreach (var session in sessions) Sessions.Add(new AiSessionRow(session));
            var target = _activeSessionId is { } id && Sessions.Any(s => s.SessionId == id)
                ? id
                : Sessions.FirstOrDefault()?.SessionId;
            if (target is not null) await OpenSessionAsync(target).ConfigureAwait(true);
            else ClearConversation();
            await RefreshSkillsAsync().ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    // 新建会话见 AiViewModel.Draft.cs（草稿语义 + 单飞：连点不产生多条空会话）。

    /// <summary>切到某个会话（重投影对话流、台账、审批）。切走时回收**未被提升**的草稿——
    /// 用户已经在另一个会话里了，空草稿不该继续占着内存。</summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        // 打开的是"正式会话"（列表里的），说明草稿已被弃用：换代回收（pending 的会等收口，见 RetireDraft）。
        RetireDraft();
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

        // 层级口径（对齐参照设计）：每轮 = **用户消息段（组外）→ 回合状态行 → 该轮工作（组内，可折叠）**；
        // **历史轮缺省收起**（只显示状态行），仅最后一轮展开——重开会话不再被一屏又一屏的旧思考/工具占满。
        var lastTurnId = order.LastOrDefault()?.TurnId;
        void AddTurn(string turnId, AiTurn turn, List<AiFeedItem> items)
        {
            var userItems = items.Where(i => i.Kind == AiFeedItem.ItemKind.UserMessage).ToList();
            var workItems = items.Where(i => i.Kind != AiFeedItem.ItemKind.UserMessage).ToList();

            foreach (var u in userItems) Feed.Add(u);

            var header = AiFeedItem.ForTurn(turn);
            header.SetPreview(PreviewOf(items));
            header.SetReplyPreview(ReplyPreviewOf(items));
            // 与参照实现同规则：**只有还在跑 / 被中断 / 失败的那一轮保持展开**，
            // 其余全部收起（重开会话只看到每轮一行"已工作 X" + AI 的最终回复）。
            var collapsed = !header.IsLockedOpen;
            header.SetInitialCollapsed(collapsed);
            Feed.Add(header);
            Turns.Add(header);
            foreach (var w in workItems)
            {
                w.SetHiddenByTurn(collapsed);
                Feed.Add(w);
            }
        }

        foreach (var turn in order)
        {
            AddTurn(turn.TurnId, turn, groups.GetValueOrDefault(turn.TurnId, []));
            groups.Remove(turn.TurnId);
        }

        foreach (var (turnId, items) in groups.OrderBy(g => g.Value.FirstOrDefault()?.At ?? DateTimeOffset.MaxValue))
        {
            var ordered = items.ToList();
            AddTurn(turnId, new AiTurn(turnId, ++fallbackIndex, AiTurnState.Completed,
                items.FirstOrDefault()?.At ?? DateTimeOffset.UtcNow, null, null, 0, 0, 0, false), ordered);
        }

        if (Turns.LastOrDefault() is { } latest) latest.IsActive = true;
        NotifyFeedShape();
    }

    /// <summary>该轮的首条用户消息（导航轨悬停提示；没有用户消息 = 空）。</summary>
    /// <summary>本轮首条用户消息（导航轨悬停预览卡的"用户说了什么"）。</summary>
    private static string PreviewOf(IReadOnlyList<AiFeedItem> items)
        => items.FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.UserMessage)?.Text ?? "";

    /// <summary>本轮首条助手回复（导航轨悬停预览卡的"助手回了什么"）。</summary>
    private static string ReplyPreviewOf(IReadOnlyList<AiFeedItem> items)
        => items.FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.AssistantMessage)?.Text ?? "";

    /// <summary>会话行清单按摘要就地更新（新会话插入 / 已有会话改状态）。**草稿不进列表**——
    /// 草稿是"还没被用起来的会话"，露在左栏就等于又回到"点一下就多一条空会话"的老毛病。</summary>
    private void UpsertSession(AiSessionSummary summary)
    {
        // 诊断留痕（会话列表"新行不出现"的现场取证）：记录每一次收到的摘要与是否被草稿过滤挡下。
        LpLog.Write(LogLevel.Info, "ai.page",
            $"session upsert: id={summary.SessionId} persistence={summary.Persistence} msgs={summary.MessageCount} title='{summary.Title}'");
        if (summary.Persistence == AiSessionPersistence.Deferred) return;   // 草稿：只在内存，不上列表
        var index = Sessions.ToList().FindIndex(s => s.SessionId == summary.SessionId);
        if (index >= 0) Sessions[index].Apply(summary);
        else Sessions.Insert(0, new AiSessionRow(summary));
    }

    /// <summary>删除会话；删掉的正是当前会话时切到清单里的第一条，**一条不剩就回到无会话空态**
    /// （不再顺手新建一条——那会让人以为"删了又冒出一个"）。</summary>
    public async Task DeleteSessionAsync(AiSessionRow session)
    {
        try
        {
            await _assistant.DeleteSessionAsync(session.SessionId).ConfigureAwait(true);
            Sessions.Remove(session);
            if (_activeSessionId != session.SessionId) return;
            if (Sessions.FirstOrDefault() is { } next) await OpenSessionAsync(next.SessionId).ConfigureAwait(true);
            else ClearConversation();
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>导出当前会话（缺省 Markdown；CSV = 台账逐条、JSON = 完整会话文件）。</summary>
    public async Task<bool> ExportSessionAsync(string outputPath, AiExportFormat format = AiExportFormat.Markdown)    {
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
        if (header.IsLockedOpen) return;   // 运行中 / 中断 / 失败：锁死展开，点了也不收
        header.ToggleCollapsed();
        SyncWorkVisibility(header);
    }

    /// <summary>该轮工作段的显隐对齐分隔行的折叠态（分隔行之后、下一个分隔行之前的全部条目）。</summary>
    private void SyncWorkVisibility(AiFeedItem header)
    {
        var index = Feed.IndexOf(header);
        if (index < 0) return;
        for (var i = index + 1; i < Feed.Count && Feed[i].Kind != AiFeedItem.ItemKind.TurnHeader; i++)
            Feed[i].SetHiddenByTurn(header.IsCollapsed);
    }

    private void RefreshSelection()
    {
        // 未配置时的如实表达只剩一处：CanSend（发送钮可用性）与模型下拉的空态文案。
        var resolution = _assistant.ResolveSelection();
        IsConfigured = resolution.Selection is not null;
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

    /// <summary>
    /// 引擎通知的**唯一入口**，也是它到界面的**线程边界**。
    /// <para>⚠️ 引擎的回合循环内部全部 <c>ConfigureAwait(false)</c>——工具执行、流式解析、通知触发
    /// 都在线程池上（用户日志实录：<c>OnNotified → AppendToTurn → 视图 ScrollToEnd</code> 整条链在后台线程上跑，
    /// 一碰 <c>ScrollViewer</c> 就「调用线程无法访问此对象」→ 回合续体被杀 → 界面从此停在"进行中"，工具卡与
    /// 审批卡永远不出现）。所以**所有投影必须回到 UI 线程**：这里判一次线程，不在就转发过去。
    /// 同优先级的 <c>BeginInvoke</c> 保序，流式增量不会乱序。</para>
    /// </summary>
    private void OnNotified(AiNotification notification)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnNotified(notification));
            return;
        }
        OnNotifiedOnUi(notification);
    }

    private void OnNotifiedOnUi(AiNotification notification)
    {
        switch (notification.Kind)
        {
            case AiNotificationKind.MessageAdded when notification.Message is { } message:
                // 非当前会话的消息不进本视图（否则"在 A 里发消息、切到新草稿"时旧会话内容会串进来）
                if (notification.SessionId != _activeSessionId) break;
                UpsertMessage(message);
                break;
            case AiNotificationKind.StreamDelta when notification.ReasoningDelta is { } thought:
                if (notification.SessionId != _activeSessionId) break;   // 同上：流式增量按会话过滤
                // 思考增量与正文增量同走一条路，但落在**不同字段**（折叠块 vs 气泡）
                var thinking = Feed.FirstOrDefault(i => i.ItemId == notification.MessageId);
                if (thinking is null)
                {
                    var shell = AiFeedItem.ForAssistant(new AiMessage(notification.MessageId ?? "", 0, AiRole.Assistant, "",
                        DateTimeOffset.UtcNow, notification.TurnId, IsStreaming: true));
                    AppendToTurn(shell);
                    shell.AppendReasoning(thought);
                }
                else
                {
                    thinking.AppendReasoning(thought);
                }
                break;
            case AiNotificationKind.StreamDelta when notification.TextDelta is { } delta:
                if (notification.SessionId != _activeSessionId) break;   // 同上：流式增量按会话过滤
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
                if (notification.SessionId != _activeSessionId) break;   // 非当前会话不入本视图
                UpsertTool(call);
                break;
            case AiNotificationKind.ApprovalChanged when notification.Approval is { } approval:
                if (notification.SessionId != _activeSessionId) break;   // 同上
                UpsertApproval(approval);
                break;
            case AiNotificationKind.ChangeRecorded when notification.Change is { } change:
                if (notification.SessionId == _activeSessionId) OnChangeRecorded(change);
                break;
            case AiNotificationKind.TurnChanged when notification.Turn is { } turn:
                if (notification.SessionId != _activeSessionId) break;
                if (HeaderOf(notification.TurnId ?? turn.TurnId) is { } header)
                {
                    header.ApplyTurn(turn);
                    // 结算即收起（ApplyTurn 内）只改到分隔行自己——条目的显隐必须在这里落地。
                    // 少了这一同步：完成后"状态已收起、思考与工具还摊着"，此时点第一下执行的其实是
                    // "展开"（画面本来就没收起，看着毫无反应），第二下才真合（用户实测：要点两下）。
                    SyncWorkVisibility(header);
                }
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
            // **用户消息在回合组之外**（对齐参照设计的层级：用户消息 → 回合状态行 → 该轮工作）：
            // 新回合的第一条用户消息先落位，回合状态行随后补在它**后面**——
            // 此前头先建、用户消息插其后，"本轮进行中"跑到用户气泡的上面，层级完全颠倒。
            if (item.Kind == AiFeedItem.ItemKind.UserMessage && HeaderOf(turnId) is null)
            {
                Feed.Add(item);
                var newHeader = EnsureHeader(turnId, item.At);
                newHeader.SetPreview(item.Text);
                item.SetHiddenByTurn(false);   // 在头之前，不受该轮折叠影响
                NotifyFeedShape();
                return;
            }

            var header = EnsureHeader(turnId, item.At);
            if (item.Kind == AiFeedItem.ItemKind.UserMessage) header.SetPreview(item.Text);
            if (item.Kind == AiFeedItem.ItemKind.AssistantMessage) header.SetReplyPreview(item.Text);
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

    /// <summary>对话流形状变化：轮次导航轨的显隐跟着轮数走（两轮以上才占位）。</summary>
    private void NotifyFeedShape() => Raise(nameof(ShowRail));

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
        RetireDraft();   // 关页：未提升的草稿静默回收（草稿在引擎侧本就是内存态，这里显式清一次）
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

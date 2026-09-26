using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Input;
using LinkPocket.UI.Ai;
using LinkPocket.ViewModels;
using Microsoft.Win32;

namespace LinkPocket.Views;

/// <summary>
/// AI 助手页（三栏：会话 / 对话 / 变更与审计）。视图只做输入采集、滚动定位与命令转发；
/// 业务状态全在 <see cref="IAiAssistant"/> 与 <see cref="AiViewModel"/> 侧（UI 是纯投影）。
/// 键位全部来自总表（<c>ShortcutCatalog</c> 的 Ai 组），本页只做「动作 id → 命令」映射。
/// </summary>
public partial class AiView : UserControl
{
    private AiViewModel? _viewModel;
    private bool _loaded;
    private ShortcutHost? _shortcutHost;
    private readonly ICommand _sendCommand;
    private readonly ICommand _escapeCommand;
    private readonly ICommand _newSessionCommand;
    private readonly ICommand _undoSessionCommand;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _copyTimer;
    private bool _stickToBottom = true;
    private Button? _copiedButton;
    private AiFeedItem? _railHover;
    private AiFeedItem? _railPreviewTarget;
    private bool _railHoverInitialized;
    private readonly DispatcherTimer _railPreviewOpenTimer;
    private readonly DispatcherTimer _railPreviewCloseTimer;

    public AiView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        _sendCommand = new RelayCommand(() => _ = SendAsync(), () => _viewModel?.CanSend == true);
        _escapeCommand = new RelayCommand(OnEscape);
        _newSessionCommand = new RelayCommand(() => _ = RunNewSessionAsync());
        // 面板按钮与 Ctrl+Shift+Z 同一条命令（CanExecute = 有可撤销批次且没有回合在跑）
        _undoSessionCommand = new RelayCommand(() => _ = UndoSessionAsync(),
            () => _viewModel?.CanUndoSession == true);

        // 秒针：运行中的回合分隔行重投影"已用 N 秒"（业务读数在 VM）
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => _viewModel?.Tick();

        // "已复制"回执的复位（同一条消息的按钮 1.2s 后复原）
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _copyTimer.Tick += (_, _) => RestoreCopyLabel();

        // 导航轨预览卡的时延：开 120ms / 收 80ms（跟随鼠标滚过整条轨时不一路闪卡；
        // 收给宽限是为了"从条滑到卡上"或"在相邻条之间挪"不闪断）
        _railPreviewOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AiRailVisual.PreviewOpenDelayMs) };
        _railPreviewOpenTimer.Tick += (_, _) => { _railPreviewOpenTimer.Stop(); OpenRailPreview(); };
        _railPreviewCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AiRailVisual.PreviewCloseDelayMs) };
        _railPreviewCloseTimer.Tick += (_, _) => { _railPreviewCloseTimer.Stop(); CloseRailPreview(); };

        // 用量环的容量面板：**鼠标在环或面板上**才展开（移开就合）。
        // 两处各挂进出，是因为面板在 Popup 里（不在环的视觉树内）——只看环的话，
        // 鼠标移进面板就被判成"离开"而立刻收起来，面板根本点不到。
        ContextRingButton.MouseEnter += (_, _) => SetContextPanel(open: true);
        ContextRingButton.MouseLeave += (_, _) => SetContextPanel(open: false);
        ContextRingButton.GotKeyboardFocus += (_, _) => SetContextPanel(open: true);
        ContextRingButton.LostKeyboardFocus += (_, _) => SetContextPanel(open: false);
        ContextUsagePanel.MouseEnter += (_, _) => SetContextPanel(open: true);
        ContextUsagePanel.MouseLeave += (_, _) => SetContextPanel(open: false);
    }

    /// <summary>面板开合（鼠标在环与面板之间来回移动时不闪：离开环的事件后紧跟面板的进入）。</summary>
    private void SetContextPanel(bool open)
    {
        if (_viewModel is null) return;
        _viewModel.IsContextPanelOpen = open;
    }

    /// <summary>窄注入（与其它页一致：页面不认识容器，由 Shell 传依赖）。</summary>
    public void Configure(IAiAssistant assistant)
    {
        _viewModel = new AiViewModel(assistant);
        _viewModel.Feed.CollectionChanged += OnFeedChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AiViewModel.CanSend) or nameof(AiViewModel.CanUndoSession))
                CommandRefresh.Request();   // 可用性同帧跟上（按钮灰亮 + 键位 CanExecute 同一处通知）
        };
        DataContext = _viewModel;
        _viewModel.ExportRequested += OnExportRequested;
        // 审批卡**不再自动抢焦点**（实测用户要求："4 个选项要平等"）——
        // 卡片一出现就给某一项键盘焦点，那一项的边框/底色就会与其它三项不平等，
        // 用户必须点一下空白才能"抹平"。选项由用户自己点（或 Tab 到）才获得焦点。

        // 输入框的控件级编辑语义（@ 提及面板的 ↑↓/Enter/Esc、光标跟踪）：**先于**快捷键宿主订阅，
        // 面板打开时由这里吃掉 Enter，控件锚定的「发送」因此不会误触发（同元素先订阅者先跑）。
        ComposerBox.TextChanged += OnComposerTextChanged;
        ComposerBox.SelectionChanged += OnComposerSelectionChanged;
        ComposerBox.PreviewKeyDown += OnComposerPreviewKeyDown;

        // 键位：Enter 发送（控件锚定在输入框上）+ Esc（分层出口）+ Ctrl+N 新建会话
        // + Ctrl+Shift+Z 撤销本会话 AI 变更；一页一组、不注册全局键
        var commands = new ShortcutCommandMap()
            .Add(ShortcutAction.AiSend, _sendCommand)
            .Add(ShortcutAction.AiEscape, _escapeCommand)
            .Add(ShortcutAction.AiNewSession, _newSessionCommand)
            .Add(ShortcutAction.AiUndoSession, _undoSessionCommand);
        _shortcutHost = new ShortcutHost(ShortcutCatalog.Build(ShortcutPage.Ai, commands), () => ShortcutScope.Ai);
        _shortcutHost.Attach(this);
        _shortcutHost.AttachControls(ShortcutPage.Ai, this, commands);
        // 右键「刷新」= 重跑入口对齐那条链（重新拉会话清单 + 重投影当前会话；对话内容不动）
        PageRefresh.Register(this, new RelayCommand(() => _ = RefreshPageAsync()));
        _tickTimer.Start();
    }

    /// <summary>入口对齐：进入本页即刷新会话清单并重投影当前会话（对话内容不重载为本地状态）。</summary>
    /// <summary>右键「刷新」：与入口对齐同一条链（重新拉会话清单并重投影当前会话，不重载对话内容）。</summary>
    private async Task RefreshPageAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.LoadAsync();
        SelectActiveInList();
        ScrollFeedToEnd();
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) await EnsureLoadedAsync();
    }

    private async Task EnsureLoadedAsync()
    {
        if (_viewModel is null) return;
        if (_loaded) return;
        _loaded = true;
        await _viewModel.LoadAsync();
        SelectActiveInList();
        _stickToBottom = true;
        ScrollFeedToEnd();
    }

    /// <summary>
    /// 把左栏选中态同步到当前会话。
    /// ⚠️ 当前会话**不在列表里**（草稿态 / 空态）时必须**清空选中**——否则旧行仍选着，
    /// 用户点它时 <c>SelectionChanged</c> 不触发（选中项没变），表现为"新建会话之后怎么点都点不进去、
    /// 完全回不到那个对话"。清空之后任何一行被点到都是"从无到有"的变更，事件必然来。
    /// </summary>
    private void SelectActiveInList()
    {
        if (_viewModel?.ActiveSessionId is not { } id) return;
        foreach (var session in SessionList.Items)
            if (session is AiSessionRow row && row.SessionId == id)
            {
                SessionList.SelectedItem = row;
                return;
            }
        SessionList.SelectedItem = null;   // 草稿不在列表里：清空，让下一次点击一定产生变更
    }

    private void OnFeedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToBottom) ScrollFeedToEnd();
    }

    private void ScrollFeedToEnd() => FeedScroll?.ScrollToEnd();

    // ── 对话区：吸底跟随 / 回到最新 / 轮次导航轨 ─────────────────

    /// <summary>滚动位置投影：吸底跟随（内容变高自动跟）、「回到最新」显隐、导航轨当前轮高亮。</summary>
    private void OnFeedScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (FeedScroll is null) return;
        var atBottom = FeedScroll.ScrollableHeight - FeedScroll.VerticalOffset <= 8;
        if (atBottom) _stickToBottom = true;
        else if (e.VerticalChange < 0) _stickToBottom = false;   // 用户上滚 = 停止跟随
        if (e.ExtentHeightChange != 0 && _stickToBottom) ScrollFeedToEnd();
        if (BackToLatestButton is not null)
            BackToLatestButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
        UpdateRail();
    }

    private void OnBackToLatest(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollFeedToEnd();
    }

    /// <summary>导航轨高亮 = 视口顶端所在的那一轮（纯滚动定位，不改业务状态）。</summary>
    private void UpdateRail()
    {
        if (_viewModel is null || FeedScroll is null) return;
        AiFeedItem? active = null;
        foreach (var header in _viewModel.Turns)
        {
            if (FeedItems.ItemContainerGenerator.ContainerFromItem(header) is not FrameworkElement container) continue;
            if (container.TransformToAncestor(FeedScroll).Transform(new Point(0, 0)).Y > 12) break;
            active = header;
        }
        active ??= _viewModel.Turns.FirstOrDefault();
        foreach (var header in _viewModel.Turns) header.IsActive = ReferenceEquals(header, active);
    }

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem header) return;
        JumpToTurn(header);
        SetRailHover(header);   // 点完条还在鼠标下：预览卡留在原位，不闪一下再回来
    }

    /// <summary>滚动定位到某一轮的开头（条与预览卡共用一条路径：两个入口不许各写一份定位口径）。</summary>
    private void JumpToTurn(AiFeedItem header)
    {
        if (FeedItems.ItemContainerGenerator.ContainerFromItem(header) is not FrameworkElement container) return;
        var offset = container.TransformToAncestor(FeedScroll).Transform(new Point(0, 0)).Y;
        _stickToBottom = false;   // 手动跳转 = 停止吸底跟随，否则下一次内容增量会把人拽回底部
        FeedScroll.ScrollToVerticalOffset(FeedScroll.VerticalOffset + offset - 8);
    }

    /// <summary>鼠标滑到某一根条上：整轨进入"山峰"态（按距离分档推移），并弹该轮的预览卡。</summary>
    private void OnRailEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem header) return;
        SetRailHover(header);
    }

    private void OnRailLeave(object sender, MouseEventArgs e) => ScheduleRailPreviewClose();

    /// <summary>鼠标进到预览卡上：取消收起（卡是可点目标，不该在"从条挪到卡"的路上消失）。</summary>
    private void OnRailPreviewEnter(object sender, MouseEventArgs e) => _railPreviewCloseTimer.Stop();

    private void OnRailPreviewLeave(object sender, MouseEventArgs e) => ScheduleRailPreviewClose();

    /// <summary>点预览卡 = 跳到卡里那一轮（与点条同一条定位路径）。</summary>
    private void OnRailPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (_railPreviewTarget is not { } header) return;
        e.Handled = true;
        CloseRailPreview();
        JumpToTurn(header);
    }

    /// <summary>
    /// 山峰态 = 逐条按"与悬浮位置的距离"取档（0 峰 / 1 相邻 / 2 次相邻 / 其余静止），
    /// 每条自己 150ms 推到目标档——动画值只改横向倍数与不透明度，不改布局宽度。
    /// 预览卡按参照的时延开（120ms）：滚过整条轨不会一路闪卡。
    /// </summary>
    private void SetRailHover(AiFeedItem header)
    {
        if (_viewModel is null) return;
        var hoverIndex = _viewModel.Turns.IndexOf(header);
        if (hoverIndex < 0) return;
        _railHover = header;
        _railPreviewTarget = header;
        var animate = _railHoverInitialized;   // 首次不做动画：从静止态"长出来"会像加载动画
        _railHoverInitialized = true;

        for (var i = 0; i < _viewModel.Turns.Count; i++)
        {
            var visual = AiRailVisual.VisualAt(hoverIndex, i);
            if (RailBarAt(_viewModel.Turns[i]) is { } bar)
            {
                bar.IsRailHovered = true;   // 悬浮期间"当前轮"让位给山峰（与参照的 showScrollActiveColor 同一口径）
                bar.AnimateTo(visual.ScaleX, visual.Opacity, animate);
            }
        }

        _railPreviewOpenTimer.Stop();
        _railPreviewCloseTimer.Stop();
        _railPreviewOpenTimer.Start();
    }

    /// <summary>
    /// 离开一根条 / 离开卡：不立刻收（留宽限让"从条滑到卡"或"相邻条之间挪"不闪断）。
    /// ⚠️ 宽限期**长于**参照的 80ms：参照的卡由 Radix HoverCard 托管、指针进卡即算"还在里面"；
    /// WPF 的 Popup 是另一个窗口，从条挪到卡要跨过那条缝，80ms 会把可点的卡在途中收掉。
    /// 行为（指针停在哪一边都算悬浮）与参照一致，只是把"跨缝"这段时间补上。
    /// </summary>
    private void ScheduleRailPreviewClose()
    {
        _railPreviewOpenTimer.Stop();
        _railPreviewCloseTimer.Stop();
        _railPreviewCloseTimer.Start();
    }

    private void OpenRailPreview()
    {
        if (_railPreviewTarget is null || RailPreviewPopup is null) return;
        RailsPreviewPlacement(_railPreviewTarget);
        RailPreviewPopup.DataContext = _railPreviewTarget;
        RailPreviewPopup.IsOpen = true;
    }

    /// <summary>预览卡贴到"当根条"的右侧（卡的垂直中心对齐该条）。</summary>
    private void RailsPreviewPlacement(AiFeedItem header)
    {
        if (RailPreviewPopup is null) return;
        if (RailBarAt(header) is not { } bar) return;
        RailPreviewPopup.PlacementTarget = bar;
        RailPreviewPopup.Placement = PlacementMode.Right;
        RailPreviewPopup.VerticalOffset = 0;
    }

    /// <summary>真的收：全部回静止档，预览卡收起（目标同时作废——卡都收了就没有可点的对象）。</summary>
    private void CloseRailPreview()
    {
        _railHover = null;
        _railHoverInitialized = false;
        _railPreviewTarget = null;
        if (_viewModel is not null)
            foreach (var header in _viewModel.Turns)
                if (RailBarAt(header) is { } bar)
                {
                    bar.IsRailHovered = false;
                    bar.AnimateTo(1, AiRailVisual.MutedOpacity, animate: true);
                }
        if (RailPreviewPopup is not null) RailPreviewPopup.IsOpen = false;
    }

    /// <summary>取某一轮对应的那条横条（容器还没生成时返回 null：不猜、不缓存过期实例）。</summary>
    private RailBar? RailBarAt(AiFeedItem header)
    {
        if (TurnRail is null) return null;
        if (TurnRail.ItemContainerGenerator.ContainerFromItem(header) is not DependencyObject container) return null;
        return FindDescendant<RailBar>(container);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// 「回溯」这条用户消息带来的那一轮：回退该轮全部操作 + 把该轮从会话里去掉 + 原话填回输入框等发送。
    /// 不可逆的动作先确认；不可撤销的变更引擎会如实跳过并计数，
    /// 所以文案说的是"回退做过的操作"，不承诺"什么都退得回来"。
    /// </summary>
    private async void OnRewindTurn(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem item) return;
        if (item.TurnId is null) return;
        if (!ConfirmDialog.Show(Loc.T("ai.rewind"), Loc.T("ai.rewind.confirm"),
                Loc.T("ai.rewind"), "restore"))
            return;
        await _viewModel.RewindTurnAsync(item);
        _stickToBottom = true;
        ScrollFeedToEnd();
        ComposerBox.Focus();
    }

    /// <summary>展开 / 收起思考原文（缺省收起：思考是参考材料，不是正文）。</summary>
    private void OnToggleReasoning(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiFeedItem item) item.ToggleReasoning();
    }

    /// <summary>折叠 / 展开一轮（只改显隐；折叠后条目不再占位，导航轨照旧可达）。</summary>
    private void OnToggleTurn(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiFeedItem item) return;
        _viewModel.ToggleTurn(item);
        Dispatcher.BeginInvoke(new Action(UpdateRail), DispatcherPriority.Loaded);
    }

    private void OnToggleToolRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiFeedItem item
            && item.Kind == AiFeedItem.ItemKind.ToolCall)
            item.ToggleExpand();
    }

    /// <summary>复制一条消息原文（用户数据原样；回执只改按钮文字，1.2s 后复原）。</summary>
    private void OnCopyMessage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not AiFeedItem item || item.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(item.Text);
        }
        catch (Exception ex)
        {
            LpLog.Warn("clipboard write failed", ex, category: "ai.page");
            return;
        }
        _copiedButton = button;
        button.Content = Loc.T("ai.feed.copied");
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    private void RestoreCopyLabel()
    {
        _copyTimer.Stop();
        if (_copiedButton is { } button) button.Content = Loc.T("ai.feed.copy");
        _copiedButton = null;
    }

    // ── 中栏：发送 / 停止 / 审批 ──────────────────────────────

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.SendAsync();
        SelectActiveInList();   // 斜杠命令 /new 会换会话：左栏选中跟上
        _stickToBottom = true;
        ScrollFeedToEnd();
        CommandRefresh.Request();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.StopAsync();
    }

    /// <summary>Esc 分层出口：回合在跑 → 停止；否则焦点回输入框（输入框获焦时本键让位给编辑语义）。</summary>
    private void OnEscape()
    {
        if (_viewModel is null) return;
        if (_viewModel.IsTurnRunning)
        {
            _ = _viewModel.StopAsync();
            return;
        }
        ComposerBox.Focus();
        Keyboard.Focus(ComposerBox);
    }

    // ── 右栏：展开 / 载荷 / 导出 ──────────────────────────────

    private void OnToggleChangeExpand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiChangeRow row) row.ToggleExpand();
    }

    /// <summary>
    /// <c>/export</c> 斜杠命令：格式由**保存对话框自己**选（三格式各一条筛选器），
    /// 不再依赖右栏那个下拉框——右栏已按产品决定删除，导出能力保留在这一条路径上。
    /// ⚠️ 左栏会话行/右键菜单里的「导出」已退场：那个入口导出的是**当前会话**（不是右键那一行），
    /// 且在草稿态（还没落盘）上必然报错——入口与语义都对不上，直接砍掉。
    /// </summary>
    private async void OnExportRequested() => await RunExportDialogAsync();

    private async Task RunExportDialogAsync()
    {
        if (_viewModel is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = string.Join("|",
                Loc.T("ai.sessions.export.filter"),
                Loc.T("ai.export.filter.csv"),
                Loc.T("ai.export.filter.json")),
            FileName = $"linkpocket-ai-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            FilterIndex = 1,
        };
        if (dialog.ShowDialog() != true) return;
        // 以**用户实际选择的扩展名**为准（改文件名也能改格式），不认识就按 Markdown
        var format = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
        {
            ".csv" => AiExportFormat.Csv,
            ".json" => AiExportFormat.Json,
            _ => AiExportFormat.Markdown,
        };
        await _viewModel.ExportSessionAsync(dialog.FileName, format);
    }

    // ── 左栏：会话 ────────────────────────────────────────────

    private async void OnNewSession(object sender, RoutedEventArgs e) => await RunNewSessionAsync();

    private async Task RunNewSessionAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.NewSessionAsync();
        SelectActiveInList();
        _stickToBottom = true;
        ScrollFeedToEnd();
    }

    /// <summary>右键先选中该行（菜单动作作用于选中行）。</summary>
    private void OnSessionRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || sender is not ListBoxItem item) return;
        item.IsSelected = true;
    }

    /// <summary>
    /// 点在会话行上：切到该会话 + 收掉进行中的重命名。
    /// <para>为什么不能只靠 <c>SelectionChanged</c>：它只在"选中项**变了**"时来。当前会话是草稿（不在列表里）
    /// 而这一行恰好还选着时，点它等于没变 → 事件不来 → 什么都点不进去。这里按"点了哪一行"直接判，
    /// 与选中态无关；重复触发由 <c>ActiveSessionId</c> 相等那一句挡掉。</para>
    /// </summary>
    private async void OnSessionRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || sender is not ListBoxItem { DataContext: AiSessionRow row }) return;
        if (_viewModel.IsRenamingSession && !row.IsRenaming) _viewModel.CancelRename();
        if (row.SessionId == _viewModel.ActiveSessionId) return;
        await _viewModel.OpenSessionAsync(row.SessionId);
        _stickToBottom = true;
        ScrollFeedToEnd();
    }

    /// <summary>
    /// 行级 Enter / Esc。⚠️ 这是"重命名卡死"的兜底出口：编辑框的键处理挂在 TextBox 上，
    /// 万一焦点没落进编辑框（模板刚实例化 / 别处抢了焦点），那条路径就永远不会被走到，
    /// 于是整行停在编辑态、Esc 也救不回来。挂到行容器上，等焦点落在行内任何一处都收得回来。
    /// </summary>
    private async void OnSessionRowKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsRenamingSession) return;
        if (sender is not ListBoxItem { DataContext: AiSessionRow { IsRenaming: true } }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.CommitRenameAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRename();
        }
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not AiSessionRow row) return;
        // 切到别的会话 = 收掉还没提交的重命名（内容已经换人了，旧行的编辑框不该跟着飘过去）
        if (_viewModel.IsRenamingSession && !row.IsRenaming) _viewModel.CancelRename();
        if (row.SessionId == _viewModel.ActiveSessionId) return;
        await _viewModel.OpenSessionAsync(row.SessionId);
        _stickToBottom = true;
        ScrollFeedToEnd();
    }

    /// <summary>
    /// 取本条动作要作用的那一行：**优先看发起者的数据上下文**，取不到才回落到选中行。
    /// ⚠️ 只用 <c>SelectedItem</c> 会删错人：点行内删除钮不会改选中态，悬停 B 点删除而 A 还选着，
    /// 删掉的就是 A。右键菜单那条路走的是"先选中再开菜单"，两条入口因此都落在同一行上。
    /// </summary>
    private AiSessionRow? SessionOf(object sender)
        => (sender as FrameworkElement)?.DataContext as AiSessionRow ?? SessionList.SelectedItem as AiSessionRow;

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SessionOf(sender) is not { } row) return;
        if (!ConfirmDialog.Show(Loc.T("ai.sessions.delete"), Loc.T("ai.sessions.delete.confirm"),
                Loc.T("ai.sessions.delete"), "delete-outline"))
            return;
        await _viewModel.DeleteSessionAsync(row);
        SelectActiveInList();
    }

    private void OnRenameSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not AiSessionRow row) return;
        _viewModel.BeginRename(row);
        // 就地编辑框在行的数据模板里：等模板把可见性样式跑完再聚焦
        Dispatcher.BeginInvoke(new Action(FocusRenameBox), DispatcherPriority.Loaded);
    }

    private void FocusRenameBox()
    {
        var box = FindDescendant<TextBox>(SessionList,
            candidate => candidate.Tag as string == "AiRenameBox" && candidate.IsVisible);
        box?.Focus();
        if (box is not null) Keyboard.Focus(box);
    }

    private static T? FindDescendant<T>(DependencyObject? root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            var nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.CommitRenameAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRename();
        }
    }

    // ── 撤销本会话 AI 变更（面板顶部按钮 + Ctrl+Shift+Z 同一条命令）──────────────

    private async void OnUndoSession(object sender, RoutedEventArgs e) => await UndoSessionAsync();

    /// <summary>
    /// 撤销本会话的 AI 变更：**决策前弹确认**（功能书 §5.7）——弹窗是视图的事，
    /// VM 只负责发命令与"按结果如实提示"；撤完 VM 重算可撤销批次，按钮跟着收起（不给会失败的按钮）。
    /// </summary>
    private async Task UndoSessionAsync()
    {
        if (_viewModel is null || !_viewModel.CanUndoSession) return;
        // 非破坏动作：图标用 restore、chip 走次强调容器（全站不设危险色，不可逆性靠文案）
        if (!ConfirmDialog.Show(Loc.T("ai.sessions.undo"), Loc.T("ai.sessions.undo.confirm"),
                Loc.T("ai.sessions.undo"), "restore"))
            return;
        await _viewModel.UndoSessionAsync();
        CommandRefresh.Request();
    }

    // ── 审批回应 ──────────────────────────────────────────────

    private async void OnApproveOnce(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.AllowOnce);

    /// <summary>点「拒绝」：**先展开可选的反馈区**（附一句说明给模型换方案；不想说就直接「确认拒绝」）。</summary>
    private void OnBeginReject(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiFeedItem item) item.IsRejectFeedbackOpen = true;
    }

    private async void OnConfirmReject(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem item) return;
        item.IsRejectFeedbackOpen = false;
        await RespondAsync(sender, AiApprovalDecision.Reject, item.RejectReason);
    }

    private void OnCancelReject(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiFeedItem item)
        {
            item.IsRejectFeedbackOpen = false;
            item.RejectReason = "";
        }
    }

    private async void OnApproveSession(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.AllowForSession);



    private async void OnRejectStop(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.RejectAndStop);

    private async Task RespondAsync(object sender, AiApprovalDecision decision, string? reason = null)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem { ApprovalId: { } approvalId }) return;
        var row = _viewModel.Approvals.FirstOrDefault(r => r.ApprovalId == approvalId);
        if (row is null) return;
        await _viewModel.RespondAsync(row, decision, reason);
    }

    // ── 提及面板（输入区 @；控件级编辑语义：由输入框自持、只在面板打开时生效）────────────

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
        => _viewModel?.UpdateMentionQuery(ComposerBox.Text, ComposerBox.CaretIndex);

    private void OnComposerSelectionChanged(object sender, RoutedEventArgs e)
    {
        _viewModel?.UpdateMentionQuery(ComposerBox.Text, ComposerBox.CaretIndex);
        ApplyPendingCaret();
    }

    private void OnComposerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsMentionPanelOpen: true }) return;
        switch (e.Key)
        {
            case Key.Up:
                e.Handled = true;
                _viewModel.MoveMentionSelection(-1);
                break;
            case Key.Down:
                e.Handled = true;
                _viewModel.MoveMentionSelection(1);
                break;
            case Key.Enter:
                e.Handled = true;   // 面板打开时 Enter = 选入候选（发送让位）
                _viewModel.CommitMentionSelection();
                ApplyPendingCaret();
                break;
            case Key.Escape:
                e.Handled = true;
                _viewModel.CancelMentions();
                break;
        }
    }

    private void OnMentionCandidateClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsMentionPanelOpen) return;
        _viewModel.CommitMentionSelection();
        ApplyPendingCaret();
        ComposerBox.Focus();
    }

    /// <summary>工具行的 `@` 钮：在光标处插入 `@` 并打开候选面板（与手敲 `@` 同一条路径）。</summary>
    private void OnInsertMention(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var caret = Math.Clamp(ComposerBox.CaretIndex, 0, ComposerBox.Text.Length);
        var text = ComposerBox.Text;
        _viewModel.ComposerText = text[..caret] + "@" + text[caret..];
        ComposerBox.CaretIndex = caret + 1;
        _viewModel.UpdateMentionQuery(_viewModel.ComposerText, caret + 1);
        ComposerBox.Focus();
        Keyboard.Focus(ComposerBox);
    }

    private void OnRemoveMention(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is AiMentionRef mention) _viewModel.RemoveMention(mention);
    }

    /// <summary>把"选入后光标应到哪"落到输入框（VM 给一次性请求；无请求不动光标）。</summary>
    private void ApplyPendingCaret()
    {
        if (_viewModel?.TakePendingCaret() is not { } caret) return;
        ComposerBox.CaretIndex = Math.Clamp(caret, 0, ComposerBox.Text.Length);
    }

    // ── 技能条 / 编辑器 / 参数行 ──────────────────────────────

    // ── 宏管理（清单 / 查看 / 新建 / 编辑 / 运行 / 删除）────────────────────

    private async void OnOpenMacros(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.CancelSkillEditor();   // 两个面板互斥（同区叠放，都开着会挤成一团）
        await _viewModel.OpenMacroPanelAsync();
    }

    private async void OnMacroSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SelectMacroAsync(_viewModel.SelectedMacroName);
    }

    private async void OnNewMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.BeginMacroEditorAsync(null);
    }

    private async void OnEditMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.BeginMacroEditorAsync(_viewModel.SelectedMacroName);
    }

    private async void OnSaveMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SaveMacroAsync();
    }

    private void OnCancelMacroEditor(object sender, RoutedEventArgs e) => _viewModel?.CloseMacroEditor();

    private void OnCloseMacros(object sender, RoutedEventArgs e) => _viewModel?.CloseMacroPanel();

    private async void OnRunMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMacroName is not { } name) return;
        var macro = _viewModel.Macros.FirstOrDefault(m => m.Name == name);
        if (macro != null) await _viewModel.RunMacroAsync(macro);
    }

    private async void OnDeleteMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMacroName is not { } name) return;
        var macro = _viewModel.Macros.FirstOrDefault(m => m.Name == name);
        if (macro != null) await _viewModel.DeleteMacroAsync(macro);
    }

    private async void OnNewSkill(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.CloseMacroPanel();   // 两个面板互斥
        await _viewModel.OpenSkillEditorAsync(null);
        SkillNameBox.Focus();
    }

    private void OnRunSkill(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is AiSkill skill) _viewModel.BeginRunSkill(skill);
    }

    private void OnRunSkillWithParams(object sender, RoutedEventArgs e) => _ = _viewModel?.RunSkillAsync();

    private void OnCancelSkillRun(object sender, RoutedEventArgs e) => _viewModel?.CancelSkillRun();

    private async void OnSaveSkill(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SaveSkillAsync();
    }

    private void OnCancelSkillEditor(object sender, RoutedEventArgs e) => _viewModel?.CancelSkillEditor();

    private void OnClearSkillMacro(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SkillMacro = null;
    }

    private async void OnDeleteSkill(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not AiSkill skill) return;
        if (!ConfirmDialog.Show(Loc.T("ai.skill.delete"), Loc.T("ai.skill.delete.confirm"),
                Loc.T("ai.skill.delete"), "delete-outline"))
            return;
        await _viewModel.DeleteSkillAsync(skill);
    }

}

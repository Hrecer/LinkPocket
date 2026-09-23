using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Input;
using LinkPocket.UI.Ai;
using LinkPocket.ViewModels;
using Microsoft.Win32;

namespace LinkPocket.Views;

/// <summary>
/// AI 助手页（三栏：会话 / 对话 / 变更与审计）。视图只做输入采集与命令转发；
/// 业务状态全在 <see cref="IAiAssistant"/> 与 <see cref="AiViewModel"/> 侧（UI 是纯投影）。
/// 键位全部来自总表（<c>ShortcutCatalog</c> 的 Ai 组），本页只做「动作 id → 命令」映射。
/// </summary>
public partial class AiView : UserControl
{
    private AiViewModel? _viewModel;
    private bool _loaded;
    private Action<string>? _navigate;
    private ShortcutHost? _shortcutHost;
    private readonly ICommand _sendCommand;
    private readonly ICommand _escapeCommand;

    public AiView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        _sendCommand = new RelayCommand(() => _ = SendAsync(), () => _viewModel?.CanSend == true);
        _escapeCommand = new RelayCommand(OnEscape);
    }

    /// <summary>窄注入（与其它页一致：页面不认识容器，由 Shell 传依赖）。
    /// <paramref name="navigate"/> = 页内"去设置"这类直达入口（Shell 提供，页面不认导航表）。</summary>
    public void Configure(IAiAssistant assistant, Action<string>? navigate = null)
    {
        _viewModel = new AiViewModel(assistant);
        _viewModel.Feed.CollectionChanged += OnFeedChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiViewModel.CanSend)) CommandRefresh.Request();
        };
        _navigate = navigate;
        DataContext = _viewModel;

        // 键位：Enter 发送（控件锚定在输入框上）+ Esc（分层出口）；一页一组、不注册全局键
        var commands = new ShortcutCommandMap()
            .Add(ShortcutAction.AiSend, _sendCommand)
            .Add(ShortcutAction.AiEscape, _escapeCommand);
        _shortcutHost = new ShortcutHost(ShortcutCatalog.Build(ShortcutPage.Ai, commands), () => ShortcutScope.Ai);
        _shortcutHost.Attach(this);
        _shortcutHost.AttachControls(ShortcutPage.Ai, this, commands);
    }

    /// <summary>入口对齐：进入本页即刷新会话清单并重投影当前会话（对话内容不重载为本地状态）。</summary>
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
        ScrollFeedToEnd();
    }

    private void SelectActiveInList()
    {
        if (_viewModel?.ActiveSessionId is not { } id) return;
        foreach (var session in SessionList.Items)
            if (session is AiSessionSummary summary && summary.SessionId == id)
            {
                SessionList.SelectedItem = session;
                break;
            }
    }

    private void OnFeedChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollFeedToEnd();

    private void ScrollFeedToEnd() => FeedScroll?.ScrollToEnd();

    // ── 中栏：发送 / 停止 / 审批 ──────────────────────────────

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.SendAsync();
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

    /// <summary>未配置服务商/模型时的直达入口（Shell 负责切页；页面不认导航表）。</summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e) => _navigate?.Invoke("settings");

    // ── 右栏：展开 / 载荷 / 导出 ──────────────────────────────

    private void OnToggleChangeExpand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiChangeRow row) row.ToggleExpand();
    }

    private void OnTogglePayload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AiEngineRow row) row.TogglePayload();
    }

    private async void OnExportSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var format = ExportFormatBox.SelectedIndex switch
        {
            1 => AiExportFormat.Csv,
            2 => AiExportFormat.Json,
            _ => AiExportFormat.Markdown,
        };
        var extension = format switch { AiExportFormat.Csv => "csv", AiExportFormat.Json => "json", _ => "md" };
        var filter = format switch
        {
            AiExportFormat.Csv => Loc.T("ai.export.filter.csv"),
            AiExportFormat.Json => Loc.T("ai.export.filter.json"),
            _ => Loc.T("ai.sessions.export.filter"),
        };
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = $"linkpocket-ai-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
        };
        if (dialog.ShowDialog() != true) return;
        await _viewModel.ExportAuditAsync(format, dialog.FileName);
    }

    // ── 左栏：会话 ────────────────────────────────────────────

    private async void OnNewSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.NewSessionAsync();
        SelectActiveInList();
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not AiSessionSummary summary) return;
        if (summary.SessionId == _viewModel.ActiveSessionId) return;
        await _viewModel.OpenSessionAsync(summary.SessionId);
        ScrollFeedToEnd();
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not AiSessionSummary summary) return;
        if (!ConfirmDialog.Show(Loc.T("ai.sessions.delete"), Loc.T("ai.sessions.delete.confirm"),
                Loc.T("ai.sessions.delete"), "delete-outline"))
            return;
        await _viewModel.DeleteSessionAsync(summary);
        SelectActiveInList();
    }

    private void OnRenameSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not AiSessionSummary summary) return;
        _viewModel.BeginRename(summary);
        RenameBox.Focus();
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

    // ── 审批回应 ──────────────────────────────────────────────

    private async void OnApproveOnce(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.AllowOnce);

    private async void OnApproveSession(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.AllowForSession);

    private async void OnReject(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.Reject);

    private async void OnRejectStop(object sender, RoutedEventArgs e) => await RespondAsync(sender, AiApprovalDecision.RejectAndStop);

    private async Task RespondAsync(object sender, AiApprovalDecision decision)
    {
        if (_viewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not AiFeedItem { ApprovalId: { } approvalId }) return;
        var row = _viewModel.Approvals.FirstOrDefault(r => r.ApprovalId == approvalId);
        if (row is null) return;
        await _viewModel.RespondAsync(row, decision);
    }
}

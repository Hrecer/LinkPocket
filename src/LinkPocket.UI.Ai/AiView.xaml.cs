using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.UI.Ai;
using Microsoft.Win32;

namespace LinkPocket.Views;

/// <summary>
/// AI 助手页（三栏：会话 / 对话 / 变更与审计）。视图只做输入采集与命令转发；
/// 业务状态全在 <see cref="IAiAssistant"/> 与 <see cref="AiViewModel"/> 侧（UI 是纯投影）。
/// </summary>
public partial class AiView : UserControl
{
    private AiViewModel? _viewModel;
    private bool _loaded;

    public AiView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>窄注入（与其它页一致：页面不认识容器，由 Shell 传依赖）。</summary>
    public void Configure(IAiAssistant assistant)
    {
        _viewModel = new AiViewModel(assistant);
        _viewModel.Feed.CollectionChanged += OnFeedChanged;
        DataContext = _viewModel;
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

    private async void OnExportSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("ai.sessions.export.filter"),
            FileName = $"linkpocket-ai-{DateTime.Now:yyyyMMdd-HHmmss}.md",
        };
        if (dialog.ShowDialog() != true) return;
        await _viewModel.ExportSessionAsync(dialog.FileName);
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SendAsync();
        ScrollFeedToEnd();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.StopAsync();
    }

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

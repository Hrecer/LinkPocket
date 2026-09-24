using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.UI.Settings;

namespace LinkPocket.Views;

/// <summary>设置页「AI 服务」面板：服务商 / 密钥 / 模型 / 连通性 / 助手偏好 / 数据与隐私。</summary>
public partial class AiPanel : UserControl
{
    private AiSettingsViewModel? _viewModel;

    public AiPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>窄注入（与设置页其它面板一致：面板不认识容器）。</summary>
    public void Configure(IAiAssistant assistant)
    {
        _viewModel = new AiSettingsViewModel(assistant);
        DataContext = _viewModel;
    }

    /// <summary>入口对齐：进面板即读偏好 + 刷新清单（设置页各面板的既定口径）。</summary>
    public async Task RefreshAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.LoadAsync();
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) await RefreshAsync();
    }

    private async void OnSaveProvider(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SaveProviderAsync();
    }

    private async void OnDeleteProvider(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProvider is not { } provider) return;
        if (!ConfirmDialog.Show(Loc.T("ai.settings.delete"), Loc.T("ai.settings.delete.confirm"),
                Loc.T("ai.settings.delete"), "delete-outline"))
            return;
        await _viewModel.DeleteProviderAsync();
    }

    private async void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var key = KeyBox.Password;
        if (string.IsNullOrWhiteSpace(key)) return;
        await _viewModel.SaveKeyAsync(key);
        KeyBox.Clear();   // 明文不留在界面里
    }

    private async void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.RemoveKeyAsync();
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.TestAsync();
    }

    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.FetchModelsAsync();
    }

    private async void OnAddModel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.AddModelAsync();
    }

    private async void OnToggleModel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        await _viewModel.ToggleModelAsync(row);
    }

    private void OnToggleModelEdit(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        _viewModel.ToggleModelEdit(row);
    }

    private async void OnSaveModelCapabilities(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        await _viewModel.SaveModelCapabilitiesAsync(row);
    }

    private void OnAddReasoningLevel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        AiSettingsViewModel.AddReasoningLevel(row);
    }

    private void OnRemoveReasoningLevel(object sender, RoutedEventArgs e)
    {
        // 删除钮在等级芯片内层模板里：它自己的 DataContext 是等级名，行要从祖先上取。
        if (_viewModel is null || sender is not FrameworkElement { Tag: string level } button) return;
        if (FindRow(button) is not { } row) return;
        AiSettingsViewModel.RemoveReasoningLevel(row, level);
    }

    private static AiModelRow? FindRow(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: AiModelRow row }) return row;
        return null;
    }

    private async void OnSavePreferences(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SavePreferencesAsync();
    }

    private async void OnClearSessions(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (!ConfirmDialog.Show(Loc.T("ai.settings.clearData"), Loc.T("ai.settings.clearData.confirm"),
                Loc.T("ai.settings.clearData"), "delete-outline"))
            return;
        await _viewModel.ClearSessionsAsync();
    }
}

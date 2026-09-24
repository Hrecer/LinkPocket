using System.Windows;
using System.Windows.Controls;
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

    // ── 「添加服务商」模板选择器 ────────────────────────────────

    private void OnBeginAddProvider(object sender, RoutedEventArgs e) => _viewModel?.BeginAddProvider();

    private void OnCancelAddProvider(object sender, RoutedEventArgs e) => _viewModel?.CancelAddProvider();

    /// <summary>点预设卡 = 选中该预设（出厂模板已在左列，不重复建记录）。</summary>
    private void OnPickPresetProvider(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { Tag: AiProviderRow row }) return;
        _viewModel.SelectedProvider = row;
    }

    /// <summary>点「其他」组的兼容卡 = 按该接入格式自建一个服务商（Tag 是协议枚举名，机器面）。</summary>
    private async void OnAddCompatProvider(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { Tag: string name }
            || !Enum.TryParse<AiProtocol>(name, out var protocol)) return;
        await _viewModel.AddCustomProviderAsync(protocol);
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

    private async void OnToggleModel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        await _viewModel.ToggleModelAsync(row);
    }

    // ── 模型元数据弹窗（添加 / 配置；草稿与配置项都在弹窗里）────────

    private void OnBeginAddModel(object sender, RoutedEventArgs e) => _viewModel?.BeginAddModel();

    private void OnEditModel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not AiModelRow row) return;
        _viewModel.BeginEditModel(row);
    }

    private void OnCancelModelDialog(object sender, RoutedEventArgs e) => _viewModel?.CancelModelDialog();

    private async void OnSaveModelDialog(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.SaveModelDialogAsync();
    }

    private void OnAddReasoningLevel(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.ModelDialogRow is not { } row) return;
        AiSettingsViewModel.AddReasoningLevel(row);
    }

    private void OnRemoveReasoningLevel(object sender, RoutedEventArgs e)
    {
        // 删除钮在等级芯片内层模板里：芯片的 DataContext 是等级名，行取弹窗当前那一个。
        if (_viewModel?.ModelDialogRow is not { } row
            || sender is not FrameworkElement { Tag: string level }) return;
        AiSettingsViewModel.RemoveReasoningLevel(row, level);
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

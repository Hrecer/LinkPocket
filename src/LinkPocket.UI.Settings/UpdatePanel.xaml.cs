using System;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.UIKit;
using LinkPocket.ViewModels;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    /// <summary>
    /// 「更新」面板（UPDATE.md §6）：状态与动作在 <see cref="UpdatePanelViewModel"/>，
    /// 视图只做展示（状态 → 文案 / 可见性，经 Loc 取词随语言切换重算）与转发（按钮 → VM 方法）。
    /// </summary>
    public partial class UpdatePanel : UserControl
    {
        private UpdatePanelViewModel? _vm;
        private bool _handOffInProgress;

        public UpdatePanel()
        {
            InitializeComponent();
        }

        /// <summary>注入更新服务（SettingsPage 经 Configure 窄注入；VM 在此时才建，构造期不碰依赖）。</summary>
        public void Configure(IUpdateService service)
        {
            _vm = new UpdatePanelViewModel(service);
            _vm.PropertyChanged += (_, _) => RenderState();
            DataContext = _vm;
            RenderState();
        }

        /// <summary>进面板时调用：消费上次更新结果 + 反映后台自动检查的产物。</summary>
        public void Refresh()
        {
            _vm?.Refresh();
            RenderState();
        }

        /// <summary>状态 → 控件投影（文案走 SetText(LocValue)，语言切换自动重算）。</summary>
        private void RenderState()
        {
            if (_vm is null) return;

            VersionText.Text = "v" + _vm.CurrentVersion;   // 版本号语言无关，直接拼
            StatusText.SetText(StatusLoc(_vm));
            FailHintText.Visibility = _vm.State == UpdatePanelState.Failed ? Visibility.Visible : Visibility.Collapsed;
            CheckButton.IsEnabled = _vm.State is not (UpdatePanelState.Checking or UpdatePanelState.Downloading);
            RestartButton.Visibility = _vm.State == UpdatePanelState.Ready ? Visibility.Visible : Visibility.Collapsed;
            DownloadProgress.Visibility = _vm.State == UpdatePanelState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            DownloadProgress.Value = _vm.Progress;
            LastUpdateText.SetText(LastUpdateLoc(_vm));
            LastUpdateText.Visibility = _vm.LastUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private static LocValue StatusLoc(UpdatePanelViewModel vm)
        {
            var version = vm.LatestVersion ?? "?";
            return vm.State switch
            {
                UpdatePanelState.Idle => Loc.K("update.status.idle"),
                UpdatePanelState.Checking => Loc.K("update.status.checking"),
                UpdatePanelState.UpToDate => Loc.K("update.status.uptodate"),
                UpdatePanelState.Downloading => Loc.K("update.status.downloading", version),
                UpdatePanelState.Ready => Loc.K("update.status.ready", version),
                UpdatePanelState.Failed => ErrorLoc(vm.ErrorMessage),
                _ => LocValue.Empty,
            };
        }

        /// <summary>失败原因码 → 文案（码是语言无关的契约，见 <see cref="UpdateErrorCodes"/>；未知码走兜底文案）。</summary>
        private static LocValue ErrorLoc(string? code) => code switch
        {
            UpdateErrorCodes.Network => Loc.K("update.err.network"),
            UpdateErrorCodes.Http => Loc.K("update.err.http"),
            UpdateErrorCodes.Release => Loc.K("update.err.release"),
            UpdateErrorCodes.Download => Loc.K("update.err.download"),
            UpdateErrorCodes.Digest => Loc.K("update.err.digest"),
            UpdateErrorCodes.ZipMissing => Loc.K("update.err.zipMissing"),
            UpdateErrorCodes.Package => Loc.K("update.err.package"),
            UpdateErrorCodes.NotPrepared => Loc.K("update.err.notPrepared"),
            UpdateErrorCodes.Updater => Loc.K("update.err.updater"),
            _ => Loc.K("update.err.other"),
        };

        private static LocValue LastUpdateLoc(UpdatePanelViewModel vm)
        {
            if (vm.LastUpdate is not { } last) return LocValue.Empty;
            return last.Ok
                ? Loc.K("update.last.ok", last.FromVersion ?? "?", last.ToVersion ?? "?")
                : Loc.K("update.last.failed", last.Error ?? "?");
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            await _vm.CheckAndPrepareAsync();
        }

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null || _handOffInProgress) return;
            if (!ConfirmDialog.Show(Loc.T("update.restart.confirmTitle"), Loc.T("update.restart.confirm"),
                    Loc.T("update.restart.button"), "arrow-up"))
                return;

            var error = _vm.HandOff();
            if (error is null)
            {
                // 交棒成功：立即退出本进程——更新器的 --wait-pid 等它退出后才动手（§5.2）
                _handOffInProgress = true;
                Application.Current?.Shutdown();
                return;
            }

            // 失败原因码 → 文案（与状态行同一张映射表）
            var message = error == UpdateErrorCodes.NotPrepared
                ? Loc.T("update.err.notPrepared")
                : Loc.T("update.err.updater");
            ConfirmDialog.Show(Loc.T("update.handoffFailedTitle"), message, Loc.T("common.ok"), "alert-circle-outline");
        }

        private async void LocalZipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            var title = Loc.T("update.local.pick");
            var filter = Loc.T("update.local.filter");
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;
            await _vm.PrepareFromZipAsync(dialog.FileName);
        }
    }
}

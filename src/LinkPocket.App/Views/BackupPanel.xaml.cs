using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkPocket.Services;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    /// <summary>
    /// 备份与恢复面板。
    /// 导出 = 目录选择（回收站不备份，卡内有提示）；<b>成功后清空保存位防手滑</b>（必须重新选目录才能再导出）。
    /// 导入 = 弹 <see cref="ImportModeDialog"/> 模态弹窗（新增导入 / 清空后导入，后者需文字确认）。
    /// 所有结果提示走统一 <see cref="ConfirmDialog"/> 弹窗（禁原生 MessageBox）；
    /// 进度展示复用设置页根部的 ExportOverlay（经可视树向上查找，名称契约见 SettingsPage.xaml）。
    /// </summary>
    public partial class BackupPanel : UserControl
    {
        private string _exportDirectory = string.Empty;
        private string _importFilePath = string.Empty;
        private bool _pendingReplaceImport;          // 本次导入是否为「清空后导入」

        public BackupPanel()
        {
            InitializeComponent();
        }

        public void ResetState()
        {
            BackupExportDirTextBox.Text = string.Empty;
            BackupExportButton.IsEnabled = false;
            BackupImportFileTextBox.Text = string.Empty;
            BackupImportButton.IsEnabled = false;
            _exportDirectory = string.Empty;
            _importFilePath = string.Empty;
        }

        // ===== 浏览选择 =====

        private void BrowseBackupExportDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择备份导出目录"
            };

            if (dialog.ShowDialog() == true)
            {
                _exportDirectory = dialog.FolderName;
                BackupExportDirTextBox.Text = dialog.FolderName;
                BackupExportButton.IsEnabled = true;
            }
        }

        private void BrowseBackupImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 LinkPocket 备份文件",
                Filter = "LinkPocket 备份文件 (*.lpbackup)|*.lpbackup|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                _importFilePath = dialog.FileName;
                BackupImportFileTextBox.Text = dialog.FileName;
                BackupImportButton.IsEnabled = true;
            }
        }

        // ===== 导出 =====

        private async void BackupExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_exportDirectory)) return;

            var outputPath = System.IO.Path.Combine(_exportDirectory, $"linkpocket_backup_{DateTime.Now:yyyyMMdd_HHmmss}.lpbackup");

            if (!System.IO.Directory.Exists(_exportDirectory))
            {
                ConfirmDialog.Show("导出失败", $"导出目录不存在：\n{_exportDirectory}", "确定", "alert-circle-outline");
                return;
            }

            if (DataContext is not ViewModels.MainViewModel vm) return;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, "正在导出备份...", 0, 0);
            BackupExportButton.IsEnabled = false;

            try
            {
                await Services.AppServices.Api.ExportBackupAsync(outputPath);

                UpdateOverlay(overlay, "导出成功！", 1, 1);
                SetOverlayProgressColor(overlay, true);

                await Task.Delay(100);
                HideOverlay(overlay);

                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\""); }
                catch { }

                ConfirmDialog.Show(
                    "导出成功",
                    $"文件位置：\n{outputPath}\n\n此备份文件包含所有书签、文件夹和图标文件，可用于完全恢复数据。\n\n注意：回收站内容不会被备份。",
                    "确定", "backup-restore", "TintPanel");
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[备份导出] 异常", ex);
                UpdateOverlay(overlay, $"导出失败: {ex.Message}", 0, 0);
                SetOverlayProgressColor(overlay, false);
                await Task.Delay(3000);
                HideOverlay(overlay);
                ConfirmDialog.Show("导出失败", $"导出失败：{ex.Message}", "确定", "alert-circle-outline");
            }
            finally
            {
                // 防手滑：每次导出完成后清空保存位置，必须重新选目录才能再导出
                BackupExportDirTextBox.Text = string.Empty;
                _exportDirectory = string.Empty;
                BackupExportButton.IsEnabled = false;
            }
        }

        // ===== 导入：先弹模态方式选择 =====

        private async void BackupImportButton_Click(object sender, RoutedEventArgs e)
        {
            var filePath = BackupImportFileTextBox.Text;
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

            if (!ImportModeDialog.Show(out var replaceMode)) return;   // 模态弹窗：挡住后面无法操作
            if (DataContext is not ViewModels.MainViewModel vm) return;

            _pendingReplaceImport = replaceMode;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, _pendingReplaceImport ? "正在清空当前数据..." : "正在导入备份...", 0, 0);
            BackupImportButton.IsEnabled = false;

            try
            {
                if (_pendingReplaceImport)
                {
                    // 完全重置：删除数据库文件（含回收站）+ 图标缓存目录后重建（与「清空数据」同机制）
                    Services.Logger.Info("[备份导入] 清空后导入：开始完全重置");
                    await vm.ReinitializeDatabaseAsync(resetData: true);
                }

                UpdateOverlay(overlay, "正在导入备份...", 0, 0);
                var result = await Services.AppServices.Api.ImportBackupAsync(filePath);

                if (!result.Success)
                {
                    var errMsg = string.Join("; ", result.Errors);
                    UpdateOverlay(overlay, $"导入失败\n{errMsg}", 0, 0);
                    SetOverlayProgressColor(overlay, false);
                    await Task.Delay(3000);
                    HideOverlay(overlay);
                    ConfirmDialog.Show("导入失败", $"导入失败：\n{errMsg}", "确定", "alert-circle-outline");
                    return;
                }

                // 刷新界面数据（新增模式导入后也要重载）
                await vm.ReinitializeDatabaseAsync(resetData: false);

                UpdateOverlay(overlay, "导入成功！", 1, 1);
                SetOverlayProgressColor(overlay, true);
                await Task.Delay(100);
                HideOverlay(overlay);

                ConfirmDialog.Show(
                    "导入成功",
                    $"统计信息：\n• 文件夹：{result.FoldersCreated} 个\n• 书签：{result.LinksCreated} 条",
                    "确定", "import", "TintPanel");
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[备份导入] 异常", ex);
                UpdateOverlay(overlay, $"导入失败: {ex.Message}", 0, 0);
                SetOverlayProgressColor(overlay, false);
                await Task.Delay(3000);
                HideOverlay(overlay);
                ConfirmDialog.Show("导入失败", $"导入失败：{ex.Message}", "确定", "alert-circle-outline");
            }
            finally
            {
                // 防手滑：导入完成后清空文件选择，必须重新选文件才能再导入（与导出同口径）
                BackupImportFileTextBox.Text = string.Empty;
                _importFilePath = string.Empty;
                BackupImportButton.IsEnabled = false;
            }
        }

        // ===== 进度遮罩（复用设置页根部的 ExportOverlay） =====

        private Border? FindOverlay()
        {
            var parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                if (parent is Grid grid)
                {
                    var overlay = grid.Children.OfType<Border>().FirstOrDefault(b => b.Name == "ExportOverlay");
                    if (overlay != null) return overlay;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private static T? FindNamedChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var result = FindNamedChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private void ShowOverlay(Border overlay, string message, int current, int total)
        {
            overlay.Visibility = Visibility.Visible;

            // 每次开始都重置为深紫（用户定稿 AccentBtn，不吃上次完成态的颜色）
            var bar = FindNamedChild<WavyProgressBar>(overlay, "ExportProgressBar");
            if (bar != null)
                bar.ActiveBrush = (Brush)Application.Current.FindResource("AccentBtn");

            UpdateOverlay(overlay, message, current, total);
        }

        private void HideOverlay(Border overlay)
        {
            overlay.Visibility = Visibility.Collapsed;
        }

        private static void UpdateOverlay(Border overlay, string message, int current, int total)
        {
            var statusText = FindNamedChild<TextBlock>(overlay, "ExportStatusText");
            if (statusText != null)
                statusText.Text = message;

            var bar = FindNamedChild<WavyProgressBar>(overlay, "ExportProgressBar");
            if (bar != null)
            {
                if (total > 0)
                {
                    bar.Maximum = total;
                    bar.Value = current;
                }
            }

            var progressText = FindNamedChild<TextBlock>(overlay, "ExportProgressText");
            if (progressText != null)
                progressText.Text = total > 0 ? $"{current} / {total}" : "准备中...";
        }

        private static void SetOverlayProgressColor(Border overlay, bool success)
        {
            // 波浪全程保持紫色（用户定稿）；失败 = WarnBg 奶油黄警示（项目铁律禁红色）
            var bar = FindNamedChild<WavyProgressBar>(overlay, "ExportProgressBar");
            if (bar != null && !success)
                bar.ActiveBrush = (Brush)Application.Current.FindResource("WarnBg");

            var progressText = FindNamedChild<TextBlock>(overlay, "ExportProgressText");
            if (progressText != null)
                progressText.Text = success ? "完成" : "失败";
        }
    }
}

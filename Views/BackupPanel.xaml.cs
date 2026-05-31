using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkPocket.Services;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    public partial class BackupPanel : UserControl
    {
        private string _exportDirectory = string.Empty;

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
        }

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
                BackupImportFileTextBox.Text = dialog.FileName;
                BackupImportButton.IsEnabled = true;
            }
        }

        private async void BackupExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_exportDirectory)) return;

            var outputPath = System.IO.Path.Combine(_exportDirectory, $"linkpocket_backup_{DateTime.Now:yyyyMMdd_HHmmss}.lpbackup");

            if (!System.IO.Directory.Exists(_exportDirectory))
            {
                MessageBox.Show($"导出目录不存在\n{_exportDirectory}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (DataContext is not ViewModels.MainViewModel vm) return;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, "正在导出备份...", 0, 0);
            BackupExportButton.IsEnabled = false;

            try
            {
                var backupService = new LinkPocketBackupService(vm.GetDbForBackup());
                var progress = new Progress<(string message, int current, int total)>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateOverlay(overlay, p.message, p.current, p.total);
                    });
                });

                await backupService.ExportAsync(outputPath, progress);

                UpdateOverlay(overlay, $"导出成功！\n共导出所有书签和文件夹", 1, 1);
                SetOverlayProgressColor(overlay, true);

                await Task.Delay(100);
                HideOverlay(overlay);

                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\""); }
                catch { }

                MessageBox.Show(
                    $"备份导出成功！\n\n文件位置：{outputPath}\n\n此备份文件包含所有书签、文件夹、图标文件和元数据，可用于完全恢复数据。",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[备份导出] 异常", ex);
                UpdateOverlay(overlay, $"导出失败: {ex.Message}", 0, 0);
                SetOverlayProgressColor(overlay, false);
                await Task.Delay(3000);
                HideOverlay(overlay);
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupExportButton.IsEnabled = true;
                BackupExportDirTextBox.Text = string.Empty;
                _exportDirectory = string.Empty;
                BackupExportButton.IsEnabled = false;
            }
        }

        private async void BackupImportButton_Click(object sender, RoutedEventArgs e)
        {
            var filePath = BackupImportFileTextBox.Text;
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

            var confirmResult = MessageBox.Show(
                $"确定要从以下文件导入备份数据吗？\n\n{filePath}\n\n" +
                "注意：\n" +
                "• 导入的数据将直接添加到现有数据库中\n" +
                "• 不会删除或覆盖任何现有数据\n" +
                "• 所有书签和文件夹都将获得新的ID\n" +
                "• 图标文件将自动恢复到缓存目录",
                "确认导入",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes) return;

            if (DataContext is not ViewModels.MainViewModel vm) return;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, "正在导入备份...", 0, 0);
            BackupImportButton.IsEnabled = false;

            try
            {
                var backupService = new LinkPocketBackupService(vm.GetDbForBackup());
                var progress = new Progress<(string message, int current, int total)>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateOverlay(overlay, p.message, p.current, p.total);
                    });
                });

                var result = await backupService.ImportAsync(filePath, progress);

                if (result.Success)
                {
                    UpdateOverlay(overlay, $"导入成功！\n{result.FoldersCreated} 个文件夹, {result.LinksCreated} 个书签", 1, 1);
                    SetOverlayProgressColor(overlay, true);

                    await Task.Delay(100);
                    HideOverlay(overlay);

                    await vm.ReinitializeDatabaseAsync(resetData: false);

                    MessageBox.Show(
                        $"备份导入成功！\n\n统计信息：\n• 新增文件夹：{result.FoldersCreated} 个\n• 新增书签：{result.LinksCreated} 条\n• 总计新增：{result.TotalItems} 项",
                        "导入成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var errMsg = string.Join("; ", result.Errors);
                    UpdateOverlay(overlay, $"导入失败\n{errMsg}", 0, 0);
                    SetOverlayProgressColor(overlay, false);
                    await Task.Delay(3000);
                    HideOverlay(overlay);
                    MessageBox.Show($"导入失败：\n{errMsg}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[备份导入] 异常", ex);
                UpdateOverlay(overlay, $"导入失败: {ex.Message}", 0, 0);
                SetOverlayProgressColor(overlay, false);
                await Task.Delay(3000);
                HideOverlay(overlay);
                MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupImportFileTextBox.Text = string.Empty;
                BackupImportButton.IsEnabled = false;
            }
        }

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

            var progressBar = FindNamedChild<ProgressBar>(overlay, "ExportProgressBar");
            if (progressBar != null)
            {
                if (total > 0)
                {
                    progressBar.Maximum = total;
                    progressBar.Value = current;
                }
                progressBar.Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238));
            }

            var progressText = FindNamedChild<TextBlock>(overlay, "ExportProgressText");
            if (progressText != null)
                progressText.Text = total > 0 ? $"{current} / {total}" : "准备中...";
        }

        private static void SetOverlayProgressColor(Border overlay, bool success)
        {
            var progressBar = FindNamedChild<ProgressBar>(overlay, "ExportProgressBar");
            if (progressBar != null)
                progressBar.Foreground = new SolidColorBrush(
                    success ? Color.FromRgb(76, 175, 80) : Color.FromRgb(244, 67, 54));

            var progressText = FindNamedChild<TextBlock>(overlay, "ExportProgressText");
            if (progressText != null)
                progressText.Text = success ? "✅ 完成" : "❌ 失败";
        }
    }
}

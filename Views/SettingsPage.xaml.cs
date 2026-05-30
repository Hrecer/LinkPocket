using System.Windows;
using System.Windows.Controls;
using LinkPocket.Data;
using LinkPocket.Services;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            IsVisibleChanged += SettingsPage_IsVisibleChanged;
            ConfirmInputBox.TextChanged += ConfirmInputBox_TextChanged;
        }

        private void SettingsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                SettingListBox.SelectedIndex = -1;
                ExportDirTextBox.Text = string.Empty;
                ExportButton.IsEnabled = false;
                ImportFileTextBox.Text = string.Empty;
                ImportButton.IsEnabled = false;

                if (DataContext is ViewModels.MainViewModel vm && vm.SettingsViewModel != null)
                    vm.SettingsViewModel.ExportDirectory = string.Empty;

                LogStatusText.Text = string.Empty;
            }
        }

        private void SettingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ExportPanel.Visibility = Visibility.Collapsed;
            ImportPanel.Visibility = Visibility.Collapsed;
            MaintenancePanel.Visibility = Visibility.Collapsed;

            if (SettingListBox.SelectedIndex == 0)
                ExportPanel.Visibility = Visibility.Visible;
            else if (SettingListBox.SelectedIndex == 1)
                ImportPanel.Visibility = Visibility.Visible;
            else if (SettingListBox.SelectedIndex == 2)
                MaintenancePanel.Visibility = Visibility.Visible;
            else
                PlaceholderPanel.Visibility = Visibility.Visible;

            if (SettingListBox.SelectedIndex != 1)
            {
                ImportFileTextBox.Text = string.Empty;
                ImportButton.IsEnabled = false;
            }
        }

        private void BrowseDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择导出目录"
            };

            if (dialog.ShowDialog() == true)
            {
                ExportDirTextBox.Text = dialog.FolderName;

                if (DataContext is ViewModels.MainViewModel vm && vm.SettingsViewModel != null)
                    vm.SettingsViewModel.ExportDirectory = dialog.FolderName;

                ExportButton.IsEnabled = true;
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm || vm.SettingsViewModel == null)
                return;

            var settingsVm = vm.SettingsViewModel;

            if (string.IsNullOrWhiteSpace(settingsVm.ExportDirectory))
                return;

            var outputPath = System.IO.Path.Combine(settingsVm.ExportDirectory, "LinkPocket_书签导出.html");
            var dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "linkpocket.db");

            Services.Logger.Info($"[导出] 开始导出流程");
            Services.Logger.Info($"[导出] 目标目录: {settingsVm.ExportDirectory}");
            Services.Logger.Info($"[导出] 输出路径: {outputPath}");
            Services.Logger.Info($"[导出] DB路径: {dbPath}");
            Services.Logger.Info($"[导出] DB文件存在: {System.IO.File.Exists(dbPath)}");

            if (!System.IO.Directory.Exists(settingsVm.ExportDirectory))
            {
                Services.Logger.Error($"[导出] 导出目录不存在: {settingsVm.ExportDirectory}");
                ExportStatusText.Text = $"导出目录不存在\n{settingsVm.ExportDirectory}";
                ExportOverlay.Visibility = Visibility.Visible;
                await Task.Delay(3000);
                ExportOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            if (!System.IO.File.Exists(dbPath))
            {
                Services.Logger.Error($"[导出] 数据库文件不存在: {dbPath}");
                ExportStatusText.Text = $"数据库文件不存在\n{dbPath}";
                ExportOverlay.Visibility = Visibility.Visible;
                await Task.Delay(3000);
                ExportOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            ExportOverlay.Visibility = Visibility.Visible;
            ExportStatusText.Text = "正在导出书签...";
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "准备中...";
            settingsVm.IsExporting = true;
            settingsVm.IsExportOverlayVisible = true;

            try
            {
                Services.Logger.Info("[导出] 创建 DbContext...");
                using var db = new Data.LinkPocketDbContext();

                Services.Logger.Info("[导出] DbContext.DbPath = " + db.DbPath);
                db.Database.EnsureCreated();

                var validFolderIds = db.Folders.Select(f => f.FolderId).ToList();
                var dbLinkCount = db.Links.Count(l => l.ListId == null || l.ListId == "0" || validFolderIds.Contains(l.ListId));
                var folderCount = db.Folders.Count();
                var totalDbLinks = db.Links.Count();
                var orphanedCount = totalDbLinks - dbLinkCount;
                Services.Logger.Info($"[导出] DB统计: 可导出书签={dbLinkCount}, 文件夹={folderCount}, 总计书签={totalDbLinks}, 无归属书签={orphanedCount}");

                var exporter = new BookmarkExporter(db);

                var progress = new Progress<(string message, int current, int total)>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ExportStatusText.Text = p.message;
                        ExportProgressBar.Maximum = p.total;
                        ExportProgressBar.Value = p.current;
                        ExportProgressText.Text = $"{p.current} / {p.total}";
                        Services.Logger.Info($"[导出] 进度: {p.message} ({p.current}/{p.total})");
                    });
                });

                Services.Logger.Info("[导出] 开始 ExportAsync...");
                await exporter.ExportAsync(outputPath, progress);
                Services.Logger.Info("[导出] ExportAsync 完成");

                var fileExists = System.IO.File.Exists(outputPath);
                Services.Logger.Info($"[导出] 验证: 文件存在={fileExists}");

                var exportedCount = 0;

                if (fileExists)
                {
                    var fileInfo = new System.IO.FileInfo(outputPath);
                    var fileContent = System.IO.File.ReadAllText(outputPath);
                    foreach (var line in fileContent.Split('\n'))
                        if (line.Contains("<A HREF="))
                            exportedCount++;

                    Services.Logger.Info($"[导出] 文件验证: 大小={fileInfo.Length} 字节, 文件中书签数={exportedCount}");
                    Services.Logger.Info($"[导出] 对照: 数据库应导出={dbLinkCount}, 实际导出={exportedCount}");

                    ExportProgressBar.Value = ExportProgressBar.Maximum;

                    if (exportedCount == dbLinkCount)
                    {
                        Services.Logger.Info($"[导出] 数量一致，导出成功");
                        ExportStatusText.Text = $"导出成功！\n共 {exportedCount} 个书签";
                        ExportProgressText.Text = $"{exportedCount} / {dbLinkCount} ✅ 一致";
                        ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                    }
                    else
                    {
                        Services.Logger.Error($"[导出] 数量不一致: 数据库={dbLinkCount}, 文件={exportedCount}");
                        ExportStatusText.Text = $"导出验证失败\n数据库应导出 {dbLinkCount} 个\n文件中仅 {exportedCount} 个";
                        ExportProgressText.Text = "❌ 数量不匹配";
                        ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                    }
                }
                else
                {
                    Services.Logger.Error($"[导出] 文件不存在: {outputPath}");
                    ExportStatusText.Text = $"导出失败：文件未创建\n{outputPath}";
                    ExportProgressText.Text = "❌ 失败";
                }

                if (fileExists && exportedCount == dbLinkCount)
                {
                    await Task.Delay(100);
                }
                else
                {
                    await Task.Delay(3000);
                }

                ExportOverlay.Visibility = Visibility.Collapsed;
                settingsVm.IsExporting = false;
                settingsVm.IsExportOverlayVisible = false;
                Services.Logger.Info("[导出] 流程结束");

                if (fileExists && exportedCount == dbLinkCount)
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\""); }
                    catch (Exception openEx) { Services.Logger.Error($"[导出] 打开目录失败", openEx); }
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Error($"[导出] 异常", ex);
                ExportStatusText.Text = $"导出失败: {ex.Message}";
                ExportProgressText.Text = "❌ 异常";
                await Task.Delay(5000);

                ExportOverlay.Visibility = Visibility.Collapsed;
                settingsVm.IsExporting = false;
                settingsVm.IsExportOverlayVisible = false;
                Services.Logger.Info("[导出] 流程结束");
            }
        }

        private void BrowseImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择书签 HTML 文件",
                Filter = "书签文件 (*.html;*.htm)|*.html;*.htm|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ImportFileTextBox.Text = dialog.FileName;
                ImportButton.IsEnabled = true;
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ImportFileTextBox.Text;
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return;

            ExportOverlay.Visibility = Visibility.Visible;
            ExportStatusText.Text = "正在验证文件...";
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "验证中...";
            ImportButton.IsEnabled = false;

            try
            {
                var (isValid, errorMsg) = await ValidateBookmarkFileAsync(filePath);

                if (!isValid)
                {
                    ExportStatusText.Text = $"导入失败\n{errorMsg}";
                    ExportProgressText.Text = "❌ 格式无效";
                    ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                    await Task.Delay(3000);
                    ExportOverlay.Visibility = Visibility.Collapsed;
                    ImportButton.IsEnabled = true;
                    return;
                }

                ExportStatusText.Text = "正在导入书签...";
                ExportProgressBar.Value = 0;
                ExportProgressText.Text = "准备中...";
                ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 0, 238));

                using var db = new Data.LinkPocketDbContext();
                var importer = new BookmarkImporter(db);

                var progress = new Progress<(string message, int current, int total)>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ExportStatusText.Text = p.message;
                        ExportProgressBar.Maximum = p.total;
                        ExportProgressBar.Value = p.current;
                        ExportProgressText.Text = $"{p.current} / {p.total}";
                        Services.Logger.Info($"[导入] 进度: {p.message} ({p.current}/{p.total})");
                    });
                });

                var result = await importer.ImportAsync(filePath, progress);

                ExportProgressBar.Value = ExportProgressBar.Maximum;

                if (result.Success)
                {
                    ExportStatusText.Text = $"导入成功！\n{result.FoldersCreated} 个文件夹, {result.LinksCreated} 个书签";
                    ExportProgressText.Text = $"✅ {result.TotalItems} 条";
                    ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));

                    if (DataContext is ViewModels.MainViewModel importVm)
                        await importVm.ReinitializeDatabaseAsync(resetData: false);

                    await Task.Delay(100);
                }
                else
                {
                    var errMsg = string.Join("; ", result.Errors);
                    ExportStatusText.Text = $"导入失败\n{errMsg}";
                    ExportProgressText.Text = "❌ 失败";
                    ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                    await Task.Delay(3000);
                }

                ExportOverlay.Visibility = Visibility.Collapsed;
                ImportButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[导入] 异常", ex);
                ExportStatusText.Text = $"导入失败: {ex.Message}";
                ExportProgressText.Text = "❌ 异常";
                ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                await Task.Delay(3000);
                ExportOverlay.Visibility = Visibility.Collapsed;
                ImportButton.IsEnabled = true;
            }
        }

        private static async Task<(bool isValid, string error)> ValidateBookmarkFileAsync(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
                return (false, "文件不存在");

            var content = await System.IO.File.ReadAllTextAsync(filePath);
            if (string.IsNullOrWhiteSpace(content))
                return (false, "文件为空");

            if (!content.Contains("<!DOCTYPE") && !content.Contains("<DL"))
                return (false, "文件不是有效的 Netscape 书签格式（缺少 DOCTYPE 或 <DL> 标签）");

            if (!System.Text.RegularExpressions.Regex.IsMatch(content, @"<A\b[^>]*HREF\s*=\s*""[^""]+""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(content, @"<H3\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return (false, "文件中未找到书签或文件夹（缺少 <A HREF= 或 <H3> 标签）");

            var dlOpenCount = System.Text.RegularExpressions.Regex.Matches(content, @"<DL[ >]", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            var dlCloseCount = System.Text.RegularExpressions.Regex.Matches(content, @"</DL>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            if (dlOpenCount != dlCloseCount)
                return (false, $"标签不匹配：<DL> 出现 {dlOpenCount} 次，</DL> 出现 {dlCloseCount} 次");

            return (true, string.Empty);
        }

        private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            if (!System.IO.Directory.Exists(logDir))
            {
                LogStatusText.Text = "无需清理";
                await Task.Delay(2000);
                LogStatusText.Text = string.Empty;
                return;
            }

            var count = 0;
            foreach (var f in System.IO.Directory.GetFiles(logDir, "*.log"))
            {
                try { System.IO.File.Delete(f); count++; }
                catch { }
            }

            LogStatusText.Text = count > 0
                ? $"已清除 {count} 个日志文件"
                : "无需清理";

            await Task.Delay(2000);
            LogStatusText.Text = string.Empty;
        }

        private void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmInputBox.Text = string.Empty;
            ConfirmErrorText.Text = string.Empty;
            ExecuteClearButton.IsEnabled = false;
            ConfirmOverlay.Visibility = Visibility.Visible;
            ConfirmInputBox.Focus();
        }

        private void CancelConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
        }

        private void ConfirmInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ExecuteClearButton.IsEnabled = ConfirmInputBox.Text == "我确认清除全部数据";
            ConfirmErrorText.Text = ExecuteClearButton.IsEnabled ? "" : "输入内容不匹配";
        }

        private async void ExecuteClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConfirmInputBox.Text != "我确认清除全部数据")
                return;

            ConfirmOverlay.Visibility = Visibility.Collapsed;

            ExportOverlay.Visibility = Visibility.Visible;
            ExportStatusText.Text = "正在清空数据...";
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "清除中...";

            try
            {
                Services.Logger.Info("[维护] 开始清空数据");

                if (DataContext is ViewModels.MainViewModel clearVm)
                    await clearVm.ReinitializeDatabaseAsync();

                var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
                if (System.IO.Directory.Exists(logDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(logDir, "*.log"))
                    {
                        try { System.IO.File.Delete(f); } catch { }
                    }
                }

                ExportStatusText.Text = "数据已全部清空！";
                ExportProgressBar.Value = ExportProgressBar.Maximum;
                ExportProgressText.Text = "✅ 完成";
                ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));

                await Task.Delay(1500);
                ExportOverlay.Visibility = Visibility.Collapsed;
                Services.Logger.Info("[维护] 数据清空完成");
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[维护] 清空数据异常", ex);
                ExportStatusText.Text = $"清空失败: {ex.Message}";
                ExportProgressText.Text = "❌ 失败";
                ExportProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                await Task.Delay(5000);
                ExportOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }
}
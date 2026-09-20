using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Services;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    /// <summary>
    /// 备份与恢复面板。
    /// 导出 = 目录选择（回收站不备份，卡内有提示）；<b>成功后清空保存位防手滑</b>（必须重新选目录才能再导出）。
    /// 导入 = 弹 <see cref="ImportModeDialog"/> 模态弹窗（新增导入 / 清空后导入，后者需文字确认）；
    ///       引擎 backup.import 自带 replace 语义（同一 UoW 清空 + 导入，原子），destructive 两阶段令牌内联。
    /// 所有结果提示走统一 <see cref="ConfirmDialog"/> 弹窗（禁原生 MessageBox）；
    /// 进度展示复用设置页根部的 ExportOverlay（经可视树向上查找，名称契约见 SettingsPage.xaml）。
    /// </summary>
    public partial class BackupPanel : UserControl
    {
        /// <summary>引擎客户端门面（由宿主 SettingsPage 经 Configure 窄注入，不再持有组合根）。</summary>
        public EngineClient Api { get; set; } = null!;

        /// <summary>整库重置委托（Shell 注入 MainViewModel.ReinitializeDatabaseAsync）：清空 / 整库重建。</summary>
        public Func<Task> ReinitializeAsync { get; set; } = () => Task.CompletedTask;

        /// <summary>导入成功后的 UI 刷新委托（Shell 注入 MainViewModel.RefreshAfterImportAsync）：只刷树/计数，不清数据。</summary>
        public Func<Task> RefreshAfterImportAsync { get; set; } = () => Task.CompletedTask;

        private string _exportDirectory = string.Empty;
        private string _importFilePath = string.Empty;

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

            if (Api == null) return;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, "正在导出备份...", 0, 0);
            BackupExportButton.IsEnabled = false;

            try
            {
                await Api.BackupExportAsync(outputPath);

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
                LpLog.Error("[备份导出] 异常", ex);
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
            if (Api == null || ReinitializeAsync == null) return;

            // S5/B-8：是否「清空后导入」只用局部变量贯穿本次流程，不再跨 await 持有实例可变状态
            var pendingReplaceImport = replaceMode;

            var overlay = FindOverlay();
            if (overlay == null) return;

            ShowOverlay(overlay, pendingReplaceImport ? "正在清空当前数据..." : "正在导入备份...", 0, 0);
            BackupImportButton.IsEnabled = false;

            try
            {
                UpdateOverlay(overlay, "正在导入备份...", 0, 0);
                // 引擎 backup.import：replace=true = 同一 UoW 清空（含回收站）后导入，原子；
                // destructive 两阶段令牌经 EngineConfirm 内联（UI 确认已由 ImportModeDialog 承担）。
                var result = await EngineConfirm.RunAsync(token => Api.BackupImportAsync(
                    filePath, replace: pendingReplaceImport, new CallOptions { ConfirmToken = token }));

                var folders = ReadCount(result.Data, "folders_created", out var foldersKnown);
                var links = ReadCount(result.Data, "links_created", out var linksKnown);

                // 刷新界面数据（replace 模式引擎已同步清空；两模式都要重载树/计数，不清数据）
                await RefreshAfterImportAsync();

                UpdateOverlay(overlay, "导入成功！", 1, 1);
                SetOverlayProgressColor(overlay, true);
                await Task.Delay(100);
                HideOverlay(overlay);

                // B-10：计数缺失时显示「未知」，绝不静默显示 0 条（导入成功却报 0 = 最差失败模式）
                ConfirmDialog.Show(
                    "导入成功",
                    $"统计信息：\n• 文件夹：{(foldersKnown ? folders.ToString() : "未知")} 个\n• 书签：{(linksKnown ? links.ToString() : "未知")} 条",
                    "确定", "import", "TintPanel");
            }
            catch (Exception ex)
            {
                LpLog.Error("[备份导入] 异常", ex);
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
            {
                // 每次开始都重置为深紫 + 进度归零（不吃上次完成态 1/1 满格与失败态颜色）
                bar.ActiveBrush = (Brush)Application.Current.FindResource("AccentBtn");
                bar.Value = 0;
                bar.Maximum = 100;
            }

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

        /// <summary>从引擎命令结果 JsonElement 读整数字段。缺失/非数字 = 引擎产出违约输入，
        /// 按 B-10 观测面规则必须留痕（此前静默返回 0 会让"导入成功却显示 0 条"）。</summary>
        private static int ReadCount(JsonElement? data, string property, out bool known)
        {
            if (data is { } d && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                known = true;
                return el.GetInt32();
            }
            known = false;
            LpLog.Error($"备份导入结果缺少计数字段 {property}（引擎 backup.import 应保证返回）", null);
            return 0;
        }
    }
}

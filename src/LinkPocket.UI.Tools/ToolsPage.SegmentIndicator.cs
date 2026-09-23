using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.Views

{
public partial class ToolsPage : UserControl
{
        // ===== 分段切换滑动指示器：两段等宽星号列，指示器在段 0，切到段 1 时把 TranslateTransform.X
        //       缓动滑过一个列宽（CubicEase Out，240ms，与全应用的柔和节奏一致）。
        //       构造初设时 ActualWidth 还是 0，首帧由 SegGrid_SizeChanged 直接贴齐，不做动画。 =====

        private void SegGrid_SizeChanged(object sender, SizeChangedEventArgs e)
            => MoveSegIndicator(animated: false);

        private void MoveSegIndicator(bool animated)
        {
            var half = SegGrid.ColumnDefinitions[0].ActualWidth;
            if (half <= 0) return;   // 尚未完成布局，等 SizeChanged 贴齐
            var target = SegExport.IsChecked == true ? half : 0;
            if (!animated)
            {
                SegIndicatorX.BeginAnimation(TranslateTransform.XProperty, null);
                SegIndicatorX.X = target;
                return;
            }
            SegIndicatorX.BeginAnimation(TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(240),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }

        // 左栏工具列表的滑动指示器已抽为可复用组件 Views/SideNavList（与设置页左栏共用），
        // 几何贴齐与滑动动画全部由组件内部负责，页面代码不再参与。

        /// <summary>清空两条流程的临时状态：每次进入工具都是干净的表单（与 ID 跳转表单同口径）。</summary>
        private void ResetBookmarkMessages()
        {
            ImportInspectChip.Visibility = Visibility.Collapsed;
            ExportResultChip.Visibility = Visibility.Collapsed;
            ImportProgressRow.Visibility = Visibility.Collapsed;
            ExportProgressRow.Visibility = Visibility.Collapsed;
            ExportRevealBtn.Visibility = Visibility.Collapsed;

            VmTools.LastExportPath = string.Empty;

            _importFilePath = string.Empty;
            _importInspection = null;
            ImportFileBox.Text = string.Empty;
            ImportFileBox.ToolTip = null;
            ImportRunBtn.IsEnabled = false;

            ExportDirBox.Text = string.Empty;
            ExportDirBox.ToolTip = null;
            ExportRunBtn.IsEnabled = false;
        }

        /// <summary>结果条语义：信息（浅紫）/ 成功（浅紫 + 勾）/ 警告失败（奶油黄，项目规范禁用红色）。</summary>
        private enum ChipState { Info, Success, Warn }

        /// <summary>
        /// 结果条：成功与信息走 PrimaryContainer 分区色，异常/警告一律 WarnBg 奶油黄
        /// （项目规范：删除与警告禁用红色，内容用深色保证可读）。
        /// 成功态用矢量勾（字形表未注册勾形图标，不引入未经渲染验证的字形）。
        /// </summary>
        private static void ShowChip(Border chip, M3Icon icon, Path check, TextBlock text,
            LocValue message, ChipState state)
        {
            var warn = state == ChipState.Warn;
            chip.SetResourceReference(Border.BackgroundProperty, "App.Support.Container");

            // 结果条的深色内容（保证在奶油黄 / PrimaryContainer 上都可读）。
            // 原先这一支写死了 Color.FromRgb(0x1C,0x1B,0x1F)（= 当时 OnSurface 的值）→ 换令牌，跟主题走。
            // T3 起异常态改走次强调容器（警告色退场）；届时这里的分支合并为单一取色。
            // ⚠️ 三个元素共用同一个资源键 → 逐个挂**资源引用**（一次性取画刷赋值会固化旧主题色）。
            const string foregroundKey = "App.Text.OnContainer";

            icon.Visibility = state == ChipState.Success ? Visibility.Collapsed : Visibility.Visible;
            check.Visibility = state == ChipState.Success ? Visibility.Visible : Visibility.Collapsed;
            icon.SetResourceReference(TextElement.ForegroundProperty, foregroundKey);
            check.SetResourceReference(Shape.StrokeProperty, foregroundKey);
            text.SetResourceReference(TextElement.ForegroundProperty, foregroundKey);
            text.SetText(message);
            chip.Visibility = Visibility.Visible;
        }

        // —— 导入 ——

        private string _importFilePath = string.Empty;

        private void ImportBrowse_Click(object sender, RoutedEventArgs e)
        {
            var title = Loc.T("tools.pickBookmarkFile");
            var filter = Loc.T("tools.filter.bookmarks");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;

            _importFilePath = dialog.FileName;
            // 域内只显示文件名（完整路径在同域 ToolTip 里），避免长路径把域撑成"半截字符"
            ImportFileBox.Text = System.IO.Path.GetFileName(dialog.FileName);
            ImportFileBox.ToolTip = dialog.FileName;
            _ = InspectImportFileAsync(dialog.FileName);
        }

        /// <summary>导入前只读预检（协议调用在 VM）：格式识别 + 条目统计（不写任何数据）。</summary>
        private async Task InspectImportFileAsync(string filePath)
        {
            ImportInspectChip.Visibility = Visibility.Collapsed;
            ImportRunBtn.IsEnabled = false;
            _importInspection = null;

            ImportProgressRow.Visibility = Visibility.Visible;
            ImportProgressText.SetText(Loc.K("tools.runningPrecheck"));

            try
            {
                var info = await VmTools.InspectBookmarkAsync(filePath);
                ImportProgressRow.Visibility = Visibility.Collapsed;

                if (!info.IsValid)
                {
                    LpLog.Warn($"bookmark preflight rejected the file: {info.Error}");
                    ShowChip(ImportInspectChip, ImportInspectIcon, ImportInspectCheck, ImportInspectText,
                        Loc.K("tools.preflight.unreadable"), ChipState.Warn);
                    return;
                }

                _importInspection = info;
                var parts = new List<LocValue>
                {
                    Loc.K("tools.preflight.format", info.Format),
                    Loc.K("tools.preflight.links", info.LinkCount),
                    Loc.K("tools.preflight.folders", info.FolderCount),
                    Loc.K("tools.preflight.depth", info.MaxDepth)
                };
                if (info.SkippedCount > 0)
                    parts.Add(Loc.K("tools.preflight.skipped", info.SkippedCount));
                if (info.Warnings.Count > 0)
                {
                    // 引擎的告警原文进日志（观测面），界面只报条数——上屏的话就是混语
                    LpLog.Warn($"bookmark preflight warnings: {string.Join(" | ", info.Warnings)}");
                    parts.Add(Loc.K("tools.preflight.warnings", info.Warnings.Count));
                }

                var summary = parts[0];
                for (var i = 1; i < parts.Count; i++) summary = Loc.K("common.dotJoin", summary, parts[i]);
                ShowChip(ImportInspectChip, ImportInspectIcon, ImportInspectCheck, ImportInspectText,
                    summary,
                    info.Warnings.Count > 0 ? ChipState.Warn : ChipState.Info);
                ImportRunBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LpLog.Error("bookmark preflight failed", ex);
                ImportProgressRow.Visibility = Visibility.Collapsed;
                ShowChip(ImportInspectChip, ImportInspectIcon, ImportInspectCheck, ImportInspectText,
                    Loc.K("tools.preflight.failed"), ChipState.Warn);
            }
        }

        private async void ImportRun_Click(object sender, RoutedEventArgs e)
        {
            var filePath = _importFilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !VmTools.TryBeginBookmarkFlow()) return;

            ImportRunBtn.IsEnabled = false;
            ImportBrowseBtn.IsEnabled = false;
            ImportProgressRow.Visibility = Visibility.Visible;
            ImportProgressText.SetText(Loc.K("tools.runningImport"));

            try
            {
                var count = await VmTools.ImportBookmarksAsync(filePath);
                ImportProgressRow.Visibility = Visibility.Collapsed;

                var detail = _importInspection is { } info
                    ? Loc.K("tools.preflight.summary", info.FolderCount, info.LinkCount)
                    : Loc.K("tools.count.total", count);
                ShowChip(ImportInspectChip, ImportInspectIcon, ImportInspectCheck, ImportInspectText,
                    Loc.K("tools.import.done", detail), ChipState.Success);

                // 成功即清空选择并锁定，避免二次点击造成重复导入
                _importFilePath = string.Empty;
                ImportFileBox.Text = string.Empty;
                ImportFileBox.ToolTip = null;
                ImportRunBtn.IsEnabled = false;
                _importInspection = null;
            }
            catch (Exception ex)
            {
                LpLog.Error("bookmark import failed", ex);
                ImportProgressRow.Visibility = Visibility.Collapsed;
                ShowChip(ImportInspectChip, ImportInspectIcon, ImportInspectCheck, ImportInspectText,
                    Loc.K("backup.importFailed"), ChipState.Warn);
                ImportRunBtn.IsEnabled = true;
            }
            finally
            {
                VmTools.EndBookmarkFlow();
                ImportBrowseBtn.IsEnabled = true;
            }
        }

        // —— 导出 ——

        private void ExportBrowse_Click(object sender, RoutedEventArgs e)
        {
            var title = Loc.T("tools.pickExportDir");
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (dialog.ShowDialog() != true) return;

            ExportDirBox.Text = dialog.FolderName;
            ExportDirBox.ToolTip = dialog.FolderName;
            ExportRunBtn.IsEnabled = true;
            ExportResultChip.Visibility = Visibility.Collapsed;
            ExportRevealBtn.Visibility = Visibility.Collapsed;
            VmTools.LastExportPath = string.Empty;
        }

        private async void ExportRun_Click(object sender, RoutedEventArgs e)
        {
            var directory = ExportDirBox.Text;
            if (string.IsNullOrWhiteSpace(directory) || !VmTools.TryBeginBookmarkFlow()) return;

            if (!System.IO.Directory.Exists(directory))
            {
                VmTools.EndBookmarkFlow();
                ShowChip(ExportResultChip, ExportResultIcon, ExportResultCheck, ExportResultText,
                    Loc.K("tools.export.dirMissing", directory), ChipState.Warn);
                return;
            }

            ExportRunBtn.IsEnabled = false;
            ExportBrowseBtn.IsEnabled = false;
            ExportResultChip.Visibility = Visibility.Collapsed;
            ExportRevealBtn.Visibility = Visibility.Collapsed;
            ExportProgressRow.Visibility = Visibility.Visible;
            ExportProgressText.SetText(Loc.K("tools.runningExport"));

            try
            {
                // 协议调用 + 产物自校验在 VM（用产物自身的数据报数，而不是"期望值"）
                var (outputPath, info) = await VmTools.ExportBookmarksAsync(directory);
                ExportProgressRow.Visibility = Visibility.Collapsed;

                if (!info.IsValid)
                {
                    LpLog.Warn($"bookmark export verification failed: {info.Error}");
                    ShowChip(ExportResultChip, ExportResultIcon, ExportResultCheck, ExportResultText,
                        Loc.K("tools.export.verifyFailed"), ChipState.Warn);
                    ExportRunBtn.IsEnabled = true;
                    return;
                }

                ExportRevealBtn.Visibility = Visibility.Visible;
                // 第二行只给文件名（完整路径就在上方域里，且可「打开所在文件夹」直达），避免长路径折行
                ShowChip(ExportResultChip, ExportResultIcon, ExportResultCheck, ExportResultText,
                    Loc.K("tools.export.doneWithFile",
                        Loc.K("tools.export.done", info.LinkCount, info.FolderCount, FormatBytes(info.FileBytes)),
                        System.IO.Path.GetFileName(outputPath)), ChipState.Success);
                ExportRunBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LpLog.Error("bookmark export failed", ex);
                ExportProgressRow.Visibility = Visibility.Collapsed;
                ShowChip(ExportResultChip, ExportResultIcon, ExportResultCheck, ExportResultText,
                    Loc.K("backup.exportFailed"), ChipState.Warn);
                ExportRunBtn.IsEnabled = true;
            }
            finally
            {
                VmTools.EndBookmarkFlow();
                ExportBrowseBtn.IsEnabled = true;
            }
        }

        private void ExportReveal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(VmTools.LastExportPath)) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{VmTools.LastExportPath}\"");
            }
            catch (Exception ex)
            {
                LpLog.Error("failed to open the export directory", ex);
            }
        }

        /// <summary>文件体积的可读形式。数字一律 <see cref="System.Globalization.CultureInfo.InvariantCulture"/>：
        /// 小数点是 <c>.</c>，不随语言变（否则同一个文件在德语机器上会写成 <c>1,5 MB</c>）。</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return System.FormattableString.Invariant($"{bytes / 1024.0:F1} KB");
            return System.FormattableString.Invariant($"{bytes / (1024.0 * 1024.0):F2} MB");
        }

}
}

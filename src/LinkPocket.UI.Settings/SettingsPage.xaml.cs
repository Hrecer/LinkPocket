using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.Views
{
    /// <summary>
    /// 设置页：存储管理（清日志 / 清空数据，危险区奶油黄警示）+ 备份与恢复（BackupPanel）。
    ///
    /// 变更记录（2026-09-16）：书签「导入 / 导出」两项已合并为**工具页**的一项工具
    /// （左栏「书签导入 / 导出」，界面见 <see cref="ToolsPage"/>）；
    /// 算法在后端 <c>Services/BookmarkImporter</c> / <c>Services/BookmarkExporter</c>，
    /// 经 <c>EngineClient</c> 暴露，设置页不再保留任何书签导入导出入口。
    /// 变更记录（2026-09-17）：「数据维护」更名「存储管理」并卡片化；危险色统一 WarnBg 奶油黄（禁红）。
    /// </summary>
    public partial class SettingsPage : UserControl
    {
        /// <summary>阶段 10 模块化：Shell 经 Configure 窄注入（引擎客户端 + 整库重置委托 + 导入后刷新委托），页面不认识组合根。</summary>
        public void Configure(EngineClient client, Func<bool, Task> reinitializeAsync, Func<Task> refreshAfterImportAsync)
        {
            Api = client;
            ReinitializeAsync = reinitializeAsync;
            BackupPanelControl.Api = client;
            BackupPanelControl.ReinitializeAsync = reinitializeAsync;
            BackupPanelControl.RefreshAfterImportAsync = refreshAfterImportAsync;
        }

        private EngineClient Api { get; set; } = null!;
        private Func<bool, Task> ReinitializeAsync { get; set; } = null!;

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
                // 与工具页同口径：进入设置页必须停在一个设置项上（默认第一项），
                // 绝不允许出现「选择一个设置项」空态；已选过则保持用户上次的选择
                if (SettingListBox.SelectedIndex < 0)
                    SettingListBox.SelectedIndex = 0;
                LogStatusText.Text = string.Empty;
            }
        }

        /// <summary>左栏只剩两项：0 = 存储管理，1 = 备份与恢复。</summary>
        private void SettingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MaintenancePanel.Visibility = Visibility.Collapsed;
            BackupPanelControl.Visibility = Visibility.Collapsed;

            if (SettingListBox.SelectedIndex == 0)
                MaintenancePanel.Visibility = Visibility.Visible;
            else if (SettingListBox.SelectedIndex == 1)
                BackupPanelControl.Visibility = Visibility.Visible;

            if (SettingListBox.SelectedIndex != 1)
                BackupPanelControl.ResetState();
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

                await ReinitializeAsync(false);

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
                ExportProgressText.Text = "完成";

                await Task.Delay(1500);
                ExportOverlay.Visibility = Visibility.Collapsed;
                Services.Logger.Info("[维护] 数据清空完成");
            }
            catch (Exception ex)
            {
                Services.Logger.Error("[维护] 清空数据异常", ex);
                ExportStatusText.Text = $"清空失败: {ex.Message}";
                ExportProgressText.Text = "失败";
                ExportProgressBar.ActiveBrush = (System.Windows.Media.Brush)Application.Current.FindResource("WarnBg");
                await Task.Delay(5000);
                ExportOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }
}

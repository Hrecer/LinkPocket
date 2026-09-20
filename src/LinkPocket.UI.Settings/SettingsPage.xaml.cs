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
        /// <summary>模块化：Shell 经 Configure 窄注入（引擎客户端 + 整库重置委托 + 导入后刷新委托），页面不认识组合根。</summary>
        public void Configure(EngineClient client, Func<Task> reinitializeAsync, Func<Task> refreshAfterImportAsync)
        {
            Api = client;
            ReinitializeAsync = reinitializeAsync;
            BackupPanelControl.Api = client;
            BackupPanelControl.ReinitializeAsync = reinitializeAsync;
            BackupPanelControl.RefreshAfterImportAsync = refreshAfterImportAsync;
        }

        private EngineClient Api { get; set; } = null!;
        private Func<Task> ReinitializeAsync { get; set; } = null!;

        public SettingsPage()
        {
            InitializeComponent();
            IsVisibleChanged += SettingsPage_IsVisibleChanged;
            ConfirmInputBox.TextChanged += ConfirmInputBox_TextChanged;

            // 快捷键宿主：键位全部来自总表（ShortcutCatalog），本页只做「动作 id → 命令」映射。
            // 设置页此前没有任何页面级键位；唯一一条 = 外观面板取色盘的 Esc（放弃取色）——
            // 它是"模态编辑态必须有放弃出口"的最低要求，写进总表而不是面板自持 KeyDown
            // （架构红线 ShortcutRulesTests：键位只许在 Input 子系统声明）。
            // 命令在**构造体内**建（字段初始化器不能引用 AppearancePanelControl 这类实例成员）。
            EscapePickerCommand = new LinkPocket.ViewModels.RelayCommand(
                () => AppearancePanelControl.ClosePicker(),
                () => AppearancePanelControl.IsPickerOpen);

            var commands = new LinkPocket.Input.ShortcutCommandMap()
                .Add(LinkPocket.Input.ShortcutAction.SettingsEscape, EscapePickerCommand);
            _shortcutHost = new LinkPocket.Input.ShortcutHost(
                LinkPocket.Input.ShortcutCatalog.Build(LinkPocket.Input.ShortcutPage.Settings, commands),
                () => LinkPocket.Input.ShortcutScope.Settings);
            _shortcutHost.Attach(this);
        }

        private LinkPocket.Input.ShortcutHost? _shortcutHost;

        /// <summary>Esc = 关闭取色盘（取色盘没开时不可用，等于这条键不存在）。</summary>
        private System.Windows.Input.ICommand EscapePickerCommand { get; }

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

        /// <summary>左栏三项：0 = 外观（决策 6 排第一），1 = 存储管理，2 = 备份与恢复。</summary>
        private void SettingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppearancePanelControl.Visibility = Visibility.Collapsed;
            MaintenancePanel.Visibility = Visibility.Collapsed;
            BackupPanelControl.Visibility = Visibility.Collapsed;

            if (SettingListBox.SelectedIndex == 0)
            {
                AppearancePanelControl.Visibility = Visibility.Visible;
                // 入口对齐：进入时把"当前已应用的外观"投影到控件上（不重算、不重置）
                AppearancePanelControl.Refresh();
            }
            else if (SettingListBox.SelectedIndex == 1)
                MaintenancePanel.Visibility = Visibility.Visible;
            else if (SettingListBox.SelectedIndex == 2)
                BackupPanelControl.Visibility = Visibility.Visible;

            if (SettingListBox.SelectedIndex != 2)
                BackupPanelControl.ResetState();
        }

        /// <summary>B-1：状态文案的"2 秒后清空"竞态护栏 —— 只清自己那次触发时的文案（新文案不被旧计时的清空覆盖）。</summary>
        private int _logStatusGen;

        private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var gen = ++_logStatusGen;
            var count = 0;
            try
            {
                count = TryClearLogFiles();
            }
            catch (Exception ex)
            {
                LpLog.Error("清空日志失败", ex);
                await ShowLogStatus("清空日志失败：目录不可访问或文件被占用", gen);
                return;
            }

            await ShowLogStatus(count > 0 ? $"已清除 {count} 个日志文件" : "无需清理", gen);
        }

        /// <summary>B-5：清空日志文件逻辑唯一入口（ClearLogsButton 与「清空数据」共用）。返回删除的文件数。
        /// 走日志管道的维护能力（先释放写句柄再删，否则长开句柄会让 File.Delete 静默失败）；未装配 = 0。</summary>
        private static int TryClearLogFiles() => LpLog.ClearLogFiles();

        private async Task ShowLogStatus(string text, int gen)
        {
            LogStatusText.Text = text;
            await Task.Delay(2000);
            if (gen == _logStatusGen)   // 期间有新状态则不清（B-1）
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

        private bool _clearInProgress;   // B-3：清空数据防重入（长操作期间连点二次触发）

        private async void ExecuteClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_clearInProgress) return;
            if (ConfirmInputBox.Text != "我确认清除全部数据")
                return;

            _clearInProgress = true;
            ConfirmOverlay.Visibility = Visibility.Collapsed;

            ExportOverlay.Visibility = Visibility.Visible;
            ExportStatusText.Text = "正在清空数据...";
            ExportProgressBar.Value = 0;
            // 颜色重置回深紫：上次失败态遗留的 WarnBg 不能带到本轮（铁律色语义）
            ExportProgressBar.ActiveBrush = (System.Windows.Media.Brush)Application.Current.FindResource(Theming.Tokens.AppTokens.AccentFill);
            ExportProgressText.Text = "清除中...";

            try
            {
                LpLog.Info("[维护] 开始清空数据");

                await ReinitializeAsync();

                TryClearLogFiles();   // B-5：与「清空日志」同一清理口径

                ExportStatusText.Text = "数据已全部清空！";
                ExportProgressBar.Value = ExportProgressBar.Maximum;
                ExportProgressText.Text = "完成";

                await Task.Delay(1500);
                ExportOverlay.Visibility = Visibility.Collapsed;
                LpLog.Info("[维护] 数据清空完成");
            }
            catch (Exception ex)
            {
                LpLog.Error("[维护] 清空数据异常", ex);
                ExportStatusText.Text = $"清空失败: {ex.Message}";
                ExportProgressText.Text = "失败";
                ExportProgressBar.ActiveBrush = (System.Windows.Media.Brush)Application.Current.FindResource(Theming.Tokens.AppTokens.SupportContainer);
                await Task.Delay(5000);
                ExportOverlay.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _clearInProgress = false;
            }
        }
    }
}

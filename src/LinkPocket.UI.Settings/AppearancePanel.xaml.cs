using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.ViewModels;
using Microsoft.Win32;

namespace LinkPocket.Views
{
    /// <summary>
    /// 「外观」面板（方案 §7）：主题卡 / 自选配色（4·5 色槽 + 取色盘）/ 字体。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么走 code-behind 而不是纯绑定</b>：面板的动作是"选一个色槽 → 开取色盘 → 回填一个槽"
    /// 这类**命令式流程**（带浮层与草稿生命周期），用绑定表达反而更绕；且本页既有页面
    /// （SettingsPage / BackupPanel）都是同一风格。**状态与动作在 VM，视图只做展示与转发**。
    /// </para>
    /// <para>
    /// <b>颜色的唯一来源仍是 Theming</b>：VM 不碰 Hct / TonalPalette（架构护栏卡住），
    /// 视图也不碰——它只把 VM 给的 <c>Color</c> 交给转换器画出来。
    /// </para>
    /// </remarks>
    public partial class AppearancePanel : UserControl
    {
        private AppearanceViewModel? _vm;
        private int _editingSlot = -1;

        public AppearancePanel()
        {
            InitializeComponent();
            Loaded += (_, _) => WireViewModel();
        }

        /// <summary>取色盘是否打开（宿主据此决定 Esc 命令的 CanExecute）。</summary>
        public bool IsPickerOpen => PickerOverlay.Visibility == Visibility.Visible;

        /// <summary>
        /// 装载字体候选（**唯一会枚举系统字体**的入口；下拉展开与探针都走它）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 异步是硬要求：枚举开销与机器上装的字体数量成正比，付在 UI 线程上就是"展开下拉卡一下"，
        /// 且随机器变差而放大（<c>FontCatalog.LoadAsync</c> 在后台线程跑）。
        /// </para>
        /// <para>
        /// 装载完把"当前字体"投影到下拉上 —— 否则下拉有列表但没有选中项，看起来像没生效。
        /// </para>
        /// </remarks>
        public async Task LoadFontCandidatesAsync()
        {
            await ViewModel.EnsureFontsLoadedAsync().ConfigureAwait(true);
            SyncFontCombos();
            UiFontCombo.SelectedItem = ViewModel.SelectedUiFont;
            MonoFontCombo.SelectedItem = ViewModel.SelectedMonoFont;
        }

        /// <summary>
        /// 关闭取色盘并放弃本次草稿（Esc / 点遮罩 / 取消按钮共用这一条出口）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 键位**不能**由本面板自持：全站键位只在 <c>ShortcutCatalog</c> 总表声明
        /// （架构红线 <c>ShortcutRulesTests</c>）。故本面板只"提供动作"，
        /// 由设置页把总表的 <c>settings.escape</c> 映射到这里。
        /// </remarks>
        public void ClosePicker()
        {
            PickerOverlay.Visibility = Visibility.Collapsed;
            _editingSlot = -1;
        }

        /// <summary>ViewModel（懒建：构造期不碰依赖，与全库"页面构造期禁碰懒建 VM"同口径）。</summary>
        public AppearanceViewModel ViewModel => _vm ??= new AppearanceViewModel();

        private void WireViewModel()
        {
            var vm = ViewModel;
            if (!ReferenceEquals(DataContext, vm))
            {
                DataContext = vm;
                ThemeCardList.ItemsSource = vm.ThemeCards;
                SlotList.ItemsSource = vm.Slots;
                UiFontCombo.ItemsSource = vm.UiFonts;
                MonoFontCombo.ItemsSource = vm.MonoFonts;

                // 取色盘的两条出口（确认 / 取消）在这里接线一次（控件自身不认识本面板）
                Picker.ColorConfirmed += Picker_ColorConfirmed;
                Picker.Cancelled += Picker_Cancelled;
            }

            SyncFontCombos();
            UiFontCombo.SelectedItem = vm.SelectedUiFont;
            MonoFontCombo.SelectedItem = vm.SelectedMonoFont;
        }

        /// <summary>进入面板时的入口对齐：把当前已应用的外观投影到控件上（不重算、不重置）。</summary>
        public void Refresh()
        {
            // 互斥归属 + 主题卡 + 色槽草稿一起对齐（草稿有未应用改动时不会被冲掉，见 VM 的 SyncFromAppliedTheme）
            ViewModel.SyncFromAppliedTheme();
            // 候选**不在这里装载**：枚举系统字体与机器上装的字体数量成正比，进页面就付是浪费；
            // 用户真的展开下拉时才装载（见 UiFontCombo_DropDownOpened）。
            SyncFontCombos();
            UiFontCombo.SelectedItem = ViewModel.SelectedUiFont;
            MonoFontCombo.SelectedItem = ViewModel.SelectedMonoFont;
        }

        /// <summary>用户展开字体下拉时才真正装载候选（唯一需要全量列表的时刻）。</summary>
        private async void UiFontCombo_DropDownOpened(object sender, EventArgs e) => await LoadFontCandidatesAsync();

        private async void MonoFontCombo_DropDownOpened(object sender, EventArgs e) => await LoadFontCandidatesAsync();

        private void SyncFontCombos()
        {
            // 诊断用：确保两个下拉的候选集已就位（ReloadFonts 可能已换过实例）
            if (!ReferenceEquals(UiFontCombo.ItemsSource, ViewModel.UiFonts))
                UiFontCombo.ItemsSource = ViewModel.UiFonts;
            if (!ReferenceEquals(MonoFontCombo.ItemsSource, ViewModel.MonoFonts))
                MonoFontCombo.ItemsSource = ViewModel.MonoFonts;
        }

        // ── 主题卡 ───────────────────────────────────────────────────────

        private void ThemeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ThemeCardViewModel card }) return;
            ViewModel.ApplyThemeCard(card);
        }

        // ── 色槽 + 取色盘 ────────────────────────────────────────────────

        private void Slot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ColorSlotViewModel slot }) return;
            _editingSlot = slot.Index;
            // 空槽也要有初值：取色盘的 HSV 三个分量总得有个起点 —— 用当前主题的强调色
            // （令牌派生，不是写死的字面量，也不是"黑色"这类会骗人的假值）。
            Picker.Open(slot.Color ?? ViewModel.PickerSeedColor);
            PickerOverlay.Visibility = Visibility.Visible;
            Picker.Focus();
        }

        private void Picker_ColorConfirmed(object? sender, Color color)
        {
            if (_editingSlot >= 0)
                ViewModel.SetSlotColor(_editingSlot, color);
            ClosePicker();
        }

        private void Picker_Cancelled(object? sender, EventArgs e) => ClosePicker();

        // ── 4/5 色 ───────────────────────────────────────────────────────
        // 段控件的选中索引经 XAML 双向绑到 VM 的 SlotCountIndex（→ SetSlotCount）——
        // 视图侧不再有"按槽数换按钮样式"的第二份状态（那是用户报障"分不清在选哪个"的根因）。

        /// <summary>「以当前主题为起点」：把当前主题的颜色**复制**进色槽（预设定义只读，不被改写）。</summary>
        private void StartFromThemeBtn_Click(object sender, RoutedEventArgs e) => ViewModel.StartFromCurrentTheme();

        private void ApplyDraftBtn_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyDraft();

        private async void ResetAppearanceBtn_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ResetToDefaultAsync();
            SyncFontCombos();
            UiFontCombo.SelectedItem = ViewModel.SelectedUiFont;
            MonoFontCombo.SelectedItem = ViewModel.SelectedMonoFont;
        }

        // ── 字体 ─────────────────────────────────────────────────────────

        private void UiFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedUiFont = UiFontCombo.SelectedItem as FontOptionViewModel;
            ShowFontInspection();
        }

        private void MonoFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedMonoFont = MonoFontCombo.SelectedItem as FontOptionViewModel;
            ShowFontInspection();
        }

        /// <summary>度量自检：只提示、不阻止应用（方案 §6.2）。</summary>
        private void ShowFontInspection()
        {
            var ui = ViewModel.SelectedUiFont;
            var message = ui is null ? string.Empty : ViewModel.InspectFont(ui);
            FontWarnText.Text = message;
            FontWarnBox.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void ImportFontBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择字体文件",
                Filter = "字体文件 (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",
                Multiselect = false,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            // 导入失败必须让**用户**看见：VM 把原因写进 Status，状态行（绑定 Status）会显示出来。
            // 只写日志不播报 = 用户点了「导入字体…」什么都没发生（本仓禁止的静默失败）。
            await ViewModel.ImportFontAsync(dialog.FileName);
            SyncFontCombos();
            UiFontCombo.SelectedItem = ViewModel.SelectedUiFont;
        }

        private async void DeleteFontBtn_Click(object sender, RoutedEventArgs e)
        {
            // 删除目标 = **界面字体**下拉里选中的那一项（它必须来自「自定义字体」来源才可删；
            // 系统字体在界面上根本不再显示这两个按钮）。
            if (ViewModel.SelectedUiFont is not { } font) return;
            await ViewModel.DeleteFontAsync(font);
            SyncFontCombos();
            UiFontCombo.SelectedItem = ViewModel.SelectedUiFont;
            MonoFontCombo.SelectedItem = ViewModel.SelectedMonoFont;
        }

        private void ApplyFontsBtn_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyFonts();
            SyncFontCombos();
        }
    }
}

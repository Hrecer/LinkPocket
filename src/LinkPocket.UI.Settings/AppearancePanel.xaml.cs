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
        /// 装载字体候选（**唯一会枚举系统字体**的入口；进面板预热、下拉展开兜底与探针都走它）。
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
            // 选中项**不在这里手工赋值**：下拉的 SelectedItem 已双向绑定 VM（`SelectedUiFont`），
            // 装载完成时 VM 会重新投影。
            // ⚠️ 手工赋值会把"候选装载之前显示当前字体"那条投影覆盖成 null
            //    （下拉框在装载前本是空白）—— 别再写回来。
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

                // 取色盘的三条出口（确认 / 清除 / 取消）在这里接线一次（控件自身不认识本面板）
                Picker.ColorConfirmed += Picker_ColorConfirmed;
                Picker.Cleared += Picker_Cleared;
                Picker.Cancelled += Picker_Cancelled;

                // 字体来源一变就重同步下拉：候选集与选中项在 VM 里都已就位，
                // 但下拉控件自己可能停在"上一来源的空列表"造成的空白态 —— 这里显式重挂一次，
                // 不必手动再展开。幂等：值没变时是一次空写。
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(AppearanceViewModel.FontSource)
                        or nameof(AppearanceViewModel.SelectedUiFont))
                        SyncFontCombos();
                };
            }

            SyncFontCombos();
            // 选中项由 XAML 的双向绑定投影，不手工赋值（见 LoadFontCandidatesAsync 的注释）
        }

        /// <summary>进入面板时的入口对齐：把当前已应用的外观投影到控件上（不重算、不重置）。</summary>
        public void Refresh()
        {
            // 互斥归属 + 主题卡 + 色槽草稿一起对齐（草稿有未应用改动时不会被冲掉，见 VM 的 SyncFromAppliedTheme）
            ViewModel.SyncFromAppliedTheme();
            // 字体候选**后台预热**：候选原先只在**下拉展开那一刻**才装载，而装载会把候选集合整体重写一遍 ——
            // 与"已经弹出的弹层"抢时序（首次展开看到的就是还没就位的列表）。改在进面板时后台装载，
            // 让首次展开时列表**已就位**；下面 DropDownOpened 那两条兜底保留（装载未完成 / 失败重试时仍会触发）。
            // 枚举在后台线程（`FontCatalog.LoadAsync`），不占 UI 线程。
            _ = ViewModel.EnsureFontsLoadedAsync();
            // 候选**不在装载前重投影**：当前字体由 ProjectCurrentFonts 的占位项立刻显示（见 VM）。
            SyncFontCombos();
        }

        /// <summary>展开字体下拉时的兜底装载（候选在进面板时已**后台预热**；这里只兜"还没装载完 / 上次失败"两种情形）。</summary>
        private async void UiFontCombo_DropDownOpened(object sender, EventArgs e) => await LoadFontCandidatesAsync();

        private void SyncFontCombos()
        {
            // 诊断用：确保下拉的候选集已就位（ReloadFonts 可能已换过实例）。
            // 等宽字体下拉**已删除**，这里只剩界面字体一个。
            if (!ReferenceEquals(UiFontCombo.ItemsSource, ViewModel.UiFonts))
                UiFontCombo.ItemsSource = ViewModel.UiFonts;

            var current = ViewModel.SelectedUiFont;
            // 按**下标**挂回，不按对象：候选清空再补齐的那一瞬，控件会停在"SelectedItem 这个对象还在、
            // 下标已经没了"的状态，而 `DisplayMemberPath` 的选择框此时画的是空串 → 框空白，
            // 且之后候选补齐也不会自愈（切字体来源后框空白就是这个态）。
            // 条件写成"下标没对上"才自然幂等：写值会经双向绑定回到 VM 再回到这里，
            // 以"对象不等"为条件时这条回环不会收敛（实测栈溢出）。
            var index = current is null ? -1 : UiFontCombo.Items.IndexOf(current);
            if (index >= 0 && UiFontCombo.SelectedIndex != index) UiFontCombo.SelectedIndex = index;
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

        /// <summary>取色盘的「清除」= 把该槽清回**空槽**（不是设成黑色），并关闭取色盘。</summary>
        private void Picker_Cleared(object? sender, EventArgs e)
        {
            if (_editingSlot >= 0)
                ViewModel.ClearSlot(_editingSlot);
            ClosePicker();
        }

        private void Picker_Cancelled(object? sender, EventArgs e) => ClosePicker();

        // ── 4/5 色 ───────────────────────────────────────────────────────
        // 段控件的选中索引经 XAML 双向绑到 VM 的 SlotCountIndex（→ SetSlotCount）——
        // 视图侧不再有"按槽数换按钮样式"的第二份状态（那会导致"分不清在选哪个"）。

        /// <summary>「以当前主题为起点」：把当前主题的颜色**复制**进色槽（预设定义只读，不被改写）。</summary>
        private void StartFromThemeBtn_Click(object sender, RoutedEventArgs e) => ViewModel.StartFromCurrentTheme();

        /// <summary>「清空颜色」：把整个草稿清回空槽（唯一一条"清空全部"路径；单槽清除在取色盘里）。</summary>
        private void ClearDraftBtn_Click(object sender, RoutedEventArgs e) => ViewModel.ClearDraft();

        private async void ResetAppearanceBtn_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ResetToDefaultAsync();
            SyncFontCombos();
        }

        // ── 字体 ─────────────────────────────────────────────────────────

        private void UiFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedUiFont = UiFontCombo.SelectedItem as FontOptionViewModel;
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

            // 导入失败必须在界面上可见：VM 把原因写进 Status，状态行（绑定 Status）会显示出来。
            // 只写日志不播报 = 点了「导入字体…」什么都没发生（本仓禁止的静默失败）。
            await ViewModel.ImportFontAsync(dialog.FileName);
            SyncFontCombos();
        }

        private async void DeleteFontBtn_Click(object sender, RoutedEventArgs e)
        {
            // 删除目标 = **界面字体**下拉里选中的那一项（它必须来自「自定义字体」来源才可删；
            // 系统字体在界面上根本不再显示这两个按钮）。
            if (ViewModel.SelectedUiFont is not { } font) return;
            await ViewModel.DeleteFontAsync(font);
            SyncFontCombos();
        }

        private void ApplyFontsBtn_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyFonts();
            SyncFontCombos();
        }

        /// <summary>「恢复默认字体」：界面/等宽一起回默认族并落盘（不动主题与配色）。</summary>
        private async void ResetFontsBtn_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ResetFontsAsync();
            SyncFontCombos();
        }
    }
}

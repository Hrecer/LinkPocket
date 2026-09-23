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
    /// <summary>
    /// 工具页（v2 完全重写）：左栏工具列表 + 右主区（共享数据表 / 表单），重复组明细为整页二级面板。
    /// 两条硬性架构约定：
    /// 1. 「跳转」一律通过 Services 层组件 <see cref="IContentLocator"/>（组合根的 Locator）执行——
    ///    本页不直连任何界面方法，跳转语义（进入目标目录并选中该行）由组件统一承担；
    /// 2. 列表一律复用共享 <see cref="SortableDataTable"/>，不手绘卡片。
    /// MVVM：查重/ID 跳转/书签导入导出的业务逻辑在 <see cref="ToolsViewModel"/>
    /// （引擎客户端调用与业务规则都在 VM），本视图只做表格装配、状态渲染与文件对话框。
    /// 依赖来源：XAML 声明的页面无法构造注入，由 Shell（MainWindow）在构造时下发组合根（Host）。
    /// </summary>
    public partial class ToolsPage : UserControl
    {
        // —— 模块化：页面不认识组合根/MainViewModel，依赖由 Shell 经 Configure 窄注入 ——
        private EngineClient _api = null!;
        private IContentLocator? _locator;
        private INavigationService? _navigation;
        private Func<string?, Task<LocValue>> _resolveLinkPath = _ => Task.FromResult(Loc.K("tools.pathRoot"));
        private Func<Task> _refreshFolderTree = () => Task.CompletedTask;

        /// <summary>
        /// Shell 在构造时注入：引擎客户端、定位组件、导航端口（打开浏览页详情页）、路径解析与目录树刷新委托。
        /// 「打开详情」走 <see cref="INavigationService.OpenLinkInBrowser"/>——与搜索页 / 智能列表结果页
        /// **同一条路径**（三页的 Enter 都是"打开详情页"）；「跳转（进目录 + 选中行）」
        /// 走 <see cref="IContentLocator"/>（ID 跳转工具 + 明细顶部/右栏入口），两者互不替代。
        /// </summary>
        public void Configure(EngineClient api, IContentLocator? locator, INavigationService? navigation,
            Func<string?, Task<LocValue>> resolveLinkPath, Func<Task> refreshFolderTree)
        {
            _api = api;
            _locator = locator;
            _navigation = navigation;
            _resolveLinkPath = resolveLinkPath;
            _refreshFolderTree = refreshFolderTree;

            // ⚠️ 明细选中/右栏接线必须在这里（Configure 之后）——构造函数里访问 VmTools 会
            // 用 null 的 _api 提前创建 VM（懒建只建一次），之后所有引擎调用都会 NRE。
            DetailSidebar.DataContext = _detailSidebar;
            // 明细右栏动作面的命令接线：面板上「详情 / 打开 / 铅笔 / 垃圾桶」四个按钮必须在此接好，
            // 未接线时绑 null 命令 → 集体禁用。
            // 口径与搜索页一致：动作面命令一律复用本页既有能力，绝不另写一套。
            _detailSidebar.OpenCommand = OpenDetailInBrowserCommand;         // 「详情」= 打开浏览页详情页
            _detailSidebar.RenameCommand = OpenDetailInBrowserCommand;       // 铅笔槽同「详情」（搜索页同口径）
            _detailSidebar.OpenWebsiteCommand = OpenDetailWebsiteCommand;    // 「打开」= 系统默认浏览器
            _detailSidebar.JumpCommand = JumpDetailCommand;                  // 「跳转」= 进目录 + 选中行（定位组件）
            // 只读对比页的动作面收窄：不显示「编辑」与「删除」——
            // 删除入口在头部「删除重复项」（按勾选、"至少保留一条"），编辑在只读对比页无意义。
            _detailSidebar.HideEditAndDeleteActions();
            VmTools.DetailSelection.Changed += ApplyDetailSelectionProjection;
        }

        private sealed class ToolItem
        {
            public string Id { get; init; } = string.Empty;
            public LocValue Name { get; init; }
            /// <summary>必须是 LpIcons 已注册的字形，否则渲染为空白占位。</summary>
            public string Icon { get; init; } = string.Empty;
        }

        private readonly List<ToolItem> _tools = new()
        {
            new() { Id = "dedup", Name = Loc.K("tools.tab.dedup"), Icon = "content-duplicate" },
            new() { Id = "idjump", Name = Loc.K("tools.tab.idJump"), Icon = "fingerprint" },
            new() { Id = "bookmarks", Name = Loc.K("tools.tab.transfer"), Icon = "bookmark-outline" },
        };

        private static readonly LocValue DedupTitle = Loc.K("tools.tab.dedup");
        private static readonly LocValue DedupSubtitle = Loc.K("tools.card.dedupDesc");
        private static readonly LocValue IdJumpTitle = Loc.K("tools.tab.idJump");
        private static readonly LocValue IdJumpSubtitle = Loc.K("tools.card.idJumpDesc");
        private static readonly LocValue BookmarkTitle = Loc.K("tools.tab.transfer");
        private static readonly LocValue BookmarkSubtitle = Loc.K("tools.card.transferDesc");

        // —— 工具页 ViewModel（懒建：需要 Configure 注入就绪） ——
        private ToolsViewModel? _toolsVm;
        private ToolsViewModel VmTools
        {
            get
            {
                if (_toolsVm != null) return _toolsVm;
                return _toolsVm = new ToolsViewModel(_api, _locator, _resolveLinkPath, _refreshFolderTree);
            }
        }

        // 主表行集（视图镜像 VM.Groups：渲染检查/渲染共用；赋值只发生在 RunDedup/Clear 两处）
        private List<DedupGroupRow> _groups = new();

        private BookmarkFileInspectionDto? _importInspection;
        private Button? _dedupActionBtn;
        private TextBlock? _dedupActionText;
        private M3Icon? _dedupActionIcon;
        private Button? _dedupClearBtn;

        public ToolsPage()
        {
            InitializeComponent();
            SetupPaneTable();
            SetupDetailTable();
            SetBookmarkMode(importing: true);   // 书签工具默认停在「导入」方向
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) ResetIdJumpForm();
            };

            // 去重明细的选中出口一：点空白（BlankClick 挂在明细页 / 主面板区域上——行容器自带 Tag=DataRow，
            // 点行不算空白；只有真正的页面空白才清选中）
            // 命令端在视图收口 = "清选中 + 焦点收回页内"（与浏览页 ClearPageSelection/ActivatePane 同口径）。
            // 注：选中核心/右栏接线在 Configure（本页构造时引擎尚未注入，不得提前触发 VmTools 懒建）
            ClearDetailSelectionCommand = new RelayCommand(() => VmTools.DetailSelection.Clear());
            var clearSelectionAndFocus = new RelayCommand(() =>
            {
                ClearDetailSelectionCommand.Execute(null);
                PageFocus.Restore(this);
            });
            BlankClick.SetCommand(DetailPanel, clearSelectionAndFocus);
            BlankClick.SetCommand(MainPanel, clearSelectionAndFocus);   // 主面板空白同样可点（清残余选中 + 收焦点）
            OpenDetailWebsiteCommand = new RelayCommand(OpenDetailWebsite, () => VmTools.DetailSelection.HasAny);
            // Enter / 明细「打开」= 打开**浏览页的链接详情页**（与搜索页 OpenDetailCommand / 智能列表
            // OpenInBrowserCommand 同一条路径与同一个端口；三页 Enter 都是"打开详情页"）
            OpenDetailInBrowserCommand = new RelayCommand(OpenDetailInBrowser, () => VmTools.DetailSelection.HasAny);
            // 「跳转」= 进浏览页对应目录并选中该行（定位组件 IContentLocator；与「详情」互不替代）
            // —— 顶部 URL 组药丸与右栏图标钮挂的是**同一条命令（同一实例）**，绝不各写一份。
            JumpDetailCommand = new RelayCommand(() => _ = JumpDetailAsync(), () => VmTools.DetailSelection.HasAny);

            // 快捷键：键位在 ShortcutCatalog（ID 输入框内 Enter 执行跳转 = 控件锚定；
            // 去重明细 = 只读集：↑/↓/End/Esc/Enter 打开详情页/F5 重查）。页面只做「动作 id → 命令」映射。
            var commands = new ShortcutCommandMap()
                .Add(ShortcutAction.ToolsIdJump, new RelayCommand(() => _ = JumpFromInputAsync()))
                .Add(ShortcutAction.ToolsEscape, ClearDetailSelectionCommand)
                .Add(ShortcutAction.ToolsDetailUp, new RelayCommand<object?>(p => MoveDetailSelection(ParseDetailDirection(p))))
                .Add(ShortcutAction.ToolsDetailDown, new RelayCommand<object?>(p => MoveDetailSelection(ParseDetailDirection(p))))
                .Add(ShortcutAction.ToolsDetailSelectLast, new RelayCommand(SelectLastDetailRow))
                .Add(ShortcutAction.ToolsDetailOpen, OpenDetailInBrowserCommand)
                .Add(ShortcutAction.ToolsDetailRefresh, new RelayCommand(() => _ = RefreshDetailAsync()));
            _shortcutHost = new ShortcutHost(ShortcutCatalog.Build(ShortcutPage.Tools, commands), () => ShortcutScope.Tools);
            _shortcutHost.Attach(this);
            _shortcutHost.AttachControls(ShortcutPage.Tools, this, commands);
        }

        /// <summary>清除去重明细的行选中（点空白 / Esc 的同一命令；无选中时无操作）。</summary>
        public ICommand ClearDetailSelectionCommand { get; }

        /// <summary>明细右栏「打开网站」：默认浏览器打开并记一次访问（与搜索页右栏同口径）。</summary>
        public ICommand OpenDetailWebsiteCommand { get; }

        /// <summary>明细 Enter /「打开」：打开浏览页的链接详情页（与搜索页/智能列表同一条路径）。</summary>
        public ICommand OpenDetailInBrowserCommand { get; }

        /// <summary>明细「跳转」（顶部 URL 组药丸 / 右栏图标钮）：进浏览页对应目录并选中该行（定位组件）。</summary>
        public ICommand JumpDetailCommand { get; }

        /// <summary>明细右栏数据模型（共享 SearchDetailsViewModel：链接 → 信息行 + 复制）。</summary>
        private readonly SearchDetailsViewModel _detailSidebar = new();

        private ShortcutHost? _shortcutHost;

        // ============================================================
        // —— 生命周期与工具切换 ——
        // ============================================================

        private void ToolsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ToolListbox.ItemsSource = _tools;
            if (ToolListbox.SelectedIndex < 0) ToolListbox.SelectedIndex = 0;
        }

        /// <summary>
        /// 外部数据变更转发入口（Shell 订阅 MainViewModel.OnToolsDataChanged 后调用；
        /// 页面不再直接订阅 MainViewModel）。
        /// 数据变更（外部增删改）后自动重跑查重，避免展示过期结果。
        /// 返回 Task 而非 async void：调用方是 Shell 的转发 lambda，异常在这里兜住并留痕
        /// （async void 抛出的异常会直接落到 UI 线程的未处理异常上）。
        /// </summary>
        public async Task OnExternalDataChangedAsync()
        {
            try
            {
                if (VmTools.HasRunDedup && DetailPanel.Visibility != Visibility.Visible
                    && (ToolListbox.SelectedItem as ToolItem)?.Id == "dedup")
                {
                    await RunDedupAsync();
                }
            }
            catch (Exception ex)
            {
                LpLog.Error("dedup rescan after an external data change failed (page stays as-is)", ex);
            }
        }

        /// <summary>
        /// 进入工具页（Shell 经 MainViewModel.OnNavigatedToTools 调用）：**入口对齐**。
        /// 防抖刷新只送达"事件发生时的活跃页"——在别的页面改完数据再切回来时，去重结果
        /// （主表或明细）可能已经陈旧。这里按视图状态补齐：
        /// · 列表视图 → 直接重跑查重；
        /// · 明细视图 → 重跑后按 URL 重组当前组（组已不再重复 → 退回主表，主表即最新）。
        /// 非去重工具 / 从未跑过查重 → 不做事。
        /// 返回 Task 的理由同 <see cref="OnExternalDataChangedAsync"/>。
        /// </summary>
        public async Task OnNavigatedToAsync()
        {
            try
            {
                if ((ToolListbox.SelectedItem as ToolItem)?.Id != "dedup" || !VmTools.HasRunDedup) return;

                var inDetail = DetailPanel.Visibility == Visibility.Visible;
                await RunDedupAsync();
                if (inDetail) ReconcileOpenDetail();
            }
            catch (Exception ex)
            {
                LpLog.Error("dedup rescan on entering the tools page failed (page stays as-is)", ex);
            }
        }

        /// <summary>重扫后按 URL 重组当前明细组（组已不再重复 → 退回主表，主表即最新）。
        /// 重进明细时**保留仍在新结果里的选中**（只剔除消失的项），行重建后统一重投。</summary>
        private void ReconcileOpenDetail()
        {
            var openUrl = VmTools.CurrentGroupUrl;
            if (string.IsNullOrEmpty(openUrl)) return;
            var row = _groups.FirstOrDefault(g =>
                string.Equals(g.Url, openUrl, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                GoBackToList();      // 组已不再重复：退回即见最新主表
            else
                EnterDetail(row, preserveSelection: true);   // 组仍在：重进明细 + 保留仍存在的选中
        }

        /// <summary>F5 重新查重（明细视图）：重扫 + 按 URL 重组当前组（与入口对齐同一条链路）。
        /// 按**导航加载口径**亮加载遮罩，重进明细后播行入场动画。</summary>
        private async Task RefreshDetailAsync()
        {
            if (DetailPanel.Visibility != Visibility.Visible) return;
            await RunDedupAsync(navigating: true);
            ReconcileOpenDetail();
            RowEntrance.Play(DetailTable.RowsList);   // 用户发起的重查：明细表播行入场（UIKit 唯一实现）
            PageFocus.Restore(this);
        }

        // ============================================================
        // —— 明细选中：投影 + 只读键位（↑/↓/End/Enter/F5；核心 = 共享 ListSelection） ——
        // ============================================================

        /// <summary>明细选中的**唯一投影点**：集合 → 行绘制 + 右栏（只读信息 + 打开网站 + 跳转）+ 顶部跳转药丸可用性。</summary>
        private void ApplyDetailSelectionProjection()
        {
            var sel = VmTools.DetailSelection;
            var row = sel.HasAny ? _detailLinks.FirstOrDefault(l => sel.Contains(l.LinkId)) : null;
            DetailTable.ApplySelection(row == null ? Array.Empty<object>() : new object[] { row });
            _detailSidebar.UpdateFrom(row == null ? null : LinkItem.FromDto(row),
                row == null ? LocValue.Empty : VmTools.ResolvePath(row));
            // 顶部「跳转」药丸与选中行同源（无选中 = 没有跳转目标）；不在投影点之外另设刷新时机
            DetailJumpBtn.IsEnabled = row != null && _locator != null;
            CommandRefresh.Request();
        }

        /// <summary>命令参数的方向字面量（↑ = -1 / ↓ = +1）。</summary>
        private static int ParseDetailDirection(object? p)
        {
            var s = (p as string is string str ? str : p?.ToString()) ?? string.Empty;
            return s.Contains("up") ? -1 : s.Contains("down") ? 1 : 0;
        }

        private void MoveDetailSelection(int delta)
        {
            var order = DetailTable.OrderedItems().OfType<LinkDto>().Select(l => l.LinkId).ToList();
            var target = VmTools.DetailSelection.Move(delta, order);
            ScrollDetailTo(target);
        }

        private void SelectLastDetailRow()
        {
            var order = DetailTable.OrderedItems().OfType<LinkDto>().Select(l => l.LinkId).ToList();
            ScrollDetailTo(VmTools.DetailSelection.SelectLast(order));
        }

        private void ScrollDetailTo(string? linkId)
        {
            if (linkId == null) return;
            var row = _detailLinks.FirstOrDefault(l => l.LinkId == linkId);
            if (row != null) DetailTable.ScrollItemIntoView(row);
        }

        /// <summary>
        /// 明细 Enter /「打开」：打开**浏览页的链接详情页**——与搜索页 <c>OpenDetailCommand</c>、
        /// 智能列表 <c>OpenInBrowserCommand</c> 走同一个端口、同一条路径（三页 Enter 一致）。
        /// 「跳转（进目录 + 选中行）」是另一条语义（见 <see cref="JumpDetailAsync"/>），键位不承担。
        /// </summary>
        private void OpenDetailInBrowser()
        {
            var id = VmTools.DetailSelection.Ids.FirstOrDefault();
            if (string.IsNullOrEmpty(id)) return;
            if (_navigation == null)
            {
                LpLog.Error("failed to open detail: the navigation port is unavailable", null);   // 观测面：失败留痕，绝不静默
                return;
            }
            _navigation.OpenLinkInBrowser(id);
        }

        /// <summary>
        /// 明细「跳转」（顶部 URL 组药丸 / 右栏图标钮）：**进浏览页对应目录并选中该行**——
        /// 与「详情」（上方）互不替代。统一走 <see cref="ToolsViewModel.JumpAsync"/>
        /// （定位组件 <see cref="IContentLocator"/>，与「ID 跳转」工具同一条流水线），本页不自带定位算法。
        /// 失败按定位结果如实提示（弹窗 = 全站统一的告警面），绝不静默。
        /// </summary>
        private async Task JumpDetailAsync()
        {
            var id = VmTools.DetailSelection.Ids.FirstOrDefault();
            if (string.IsNullOrEmpty(id)) return;

            var result = await VmTools.JumpAsync(id);
            if (result.IsSuccess) return;

            ConfirmDialog.Show(Loc.T("common.jump"), (result.Message ?? result.Status switch
            {
                LocateStatus.NotFound => Loc.K("locate.notFoundLink"),
                LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
                LocateStatus.Failed => Loc.K("locate.failedRetry"),
                _ => Loc.K("locate.incomplete"),
            }).Resolve(), Loc.T("common.ok"), "alert-circle-outline");
        }

        private void DetailJump_Click(object sender, RoutedEventArgs e) => _ = JumpDetailAsync();

        /// <summary>明细右栏「打开网站」：默认浏览器打开并记一次访问（与搜索页右栏同口径）。</summary>
        private void OpenDetailWebsite()
        {
            var sel = VmTools.DetailSelection;
            var row = sel.HasAny ? _detailLinks.FirstOrDefault(l => sel.Contains(l.LinkId)) : null;
            if (row == null || string.IsNullOrWhiteSpace(row.Url)) return;
            Services.LinkLauncher.Open(row.Url);
            _ = RecordDetailVisitAsync(row.LinkId);
        }

        private async Task RecordDetailVisitAsync(string linkId)
        {
            try { await _api.LinkVisitRecordAsync(linkId); }
            catch { /* 记账失败不打断（与搜索页右栏同口径） */ }
        }

        private void ToolListbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ToolListbox.SelectedItem is not ToolItem tool) return;
            VmTools.ClearDedup();
            _groups = new List<DedupGroupRow>();
            GoBackToList();
            ShowTool(tool.Id);
        }

        private void ShowTool(string toolId)
        {
            HeaderActions.Children.Clear();
            _dedupActionBtn = null;
            _dedupActionText = null;
            _dedupActionIcon = null;
            _dedupClearBtn = null;

            // 三个宿主互斥显示，先统一收起再按工具打开（避免出现"空白的第三态"）
            PaneTableHost.Visibility = Visibility.Collapsed;
            PaneFormHost.Visibility = Visibility.Collapsed;
            PaneBookmarkHost.Visibility = Visibility.Collapsed;

            switch (toolId)
            {
                case "idjump":
                    PaneTitle.SetText(IdJumpTitle);
                    PaneSubtitle.SetText(IdJumpSubtitle);
                    PaneFormHost.Visibility = Visibility.Visible;
                    ResetIdJumpForm();
                    IdInput.Focus();
                    break;

                case "bookmarks":
                    PaneTitle.SetText(BookmarkTitle);
                    PaneSubtitle.SetText(BookmarkSubtitle);
                    PaneBookmarkHost.Visibility = Visibility.Visible;
                    ResetBookmarkMessages();
                    SetBookmarkMode(importing: true);
                    break;

                default: // dedup
                    PaneTitle.SetText(DedupTitle);
                    PaneSubtitle.SetText(DedupSubtitle);
                    PaneTableHost.Visibility = Visibility.Visible;
                    BuildDedupHeaderActions();
                    ShowDedupPlaceholder();
                    break;
            }
        }

        /// <summary>去重工具的操作组：主操作药丸（开始/重新查重）+ 清除结果。</summary>
        private void BuildDedupHeaderActions()
        {
            _dedupActionIcon = new M3Icon { Kind = "content-duplicate", Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            _dedupActionText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            _dedupActionText.SetFitText("tools.dedup.start");
            _dedupActionBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("PrimaryPillButton"),
                // ⚠️ 药丸样式自身不含 MinHeight/Padding：高度必须显式给（与其它页药丸统一的 32），
                // 否则垂直 Padding=0 会把按钮压扁成一条。
                Height = 32,
                // ⚠️ 宽度是**冻结几何**（约束 B：切语言宽高逐像素不变），取值口径 = **中文基线**：
                //    Width = 文字宽(中文, 基准字号) + 图标 + 图标外边距 + 壳内距 + 模板内部件内距（实测 4）
                //          = 「开始查重/重新查重」4 字 × 13pt = 52 + 16 + 6 + 14×2 + 4 = 106。
                //    英文（Find duplicates / Scan again）放不下时由 `LocFit` 缩**英文的**字号让位 ——
                //    绝不为英文放大几何（见 `UI-SPEC.md` §3 与 `WARNINGS` 116）。
                Width = 106,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dedupActionIcon, _dedupActionText } }
            };
            _dedupActionBtn.Click += async (_, _) => await RunDedupAsync();
            HeaderActions.Children.Add(_dedupActionBtn);

            var clearLabel = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            clearLabel.SetFitText("tools.dedup.clear");
            _dedupClearBtn = new Button
            {
                Height = 32,   // 同上：药丸样式无高度默认值，必须显式给
                // 冻结宽度 = **中文基线**：Width = 文字宽(中文, 基准字号) + 图标 + 图标外边距 + 壳内距 + 模板内部件内距（实测 3）
                //          = 「清除结果」4 字 × 12.5pt = 50 + 14 + 5 + 12×2 + 3 = 96。
                // 英文（Clear results）放不下时缩**英文的**字号让位，几何不变（`WARNINGS` 116）。
                Width = 96,
                Padding = new Thickness(12, 0, 12, 0),
                IsEnabled = false,
                Cursor = Cursors.Hand,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new M3Icon { Kind = "close-circle-outline", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                        clearLabel
                    }
                },
                Style = (Style)Application.Current.FindResource("TonalButton")
            };
            _dedupClearBtn.Click += (_, _) => ClearDedupResults();
            HeaderActions.Children.Add(_dedupClearBtn);
        }

        private void ShowDedupPlaceholder()
        {
            PaneTable.ItemsSource = null;
            PaneTable.EmptyContent = BuildState("content-duplicate", Loc.K("tools.dedup.emptyTitle"),
                Loc.K("tools.dedup.emptyHint"));
            PaneSubtitle.SetText(DedupSubtitle);
        }

        private void ClearDedupResults()
        {
            VmTools.ClearDedup();
            _groups = new List<DedupGroupRow>();
            GoBackToList();
            if (_dedupActionIcon != null) _dedupActionIcon.Kind = "content-duplicate";
            if (_dedupActionText != null) _dedupActionText.SetText(Loc.K("tools.dedup.start"));
            if (_dedupClearBtn != null) _dedupClearBtn.IsEnabled = false;
            ShowDedupPlaceholder();
        }

        // ============================================================
        // —— 空态（MD3E：大圆角徽章 + 引导文案，与其它页面同规格） ——
        // ============================================================

        private static FrameworkElement BuildState(string iconKind, LocValue title, LocValue subtitle)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 56, 0, 0),
                IsHitTestVisible = false
            };

            var badge = new Border
            {
                Width = 96, Height = 96, CornerRadius = new CornerRadius(32),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.SetResourceReference(Border.BackgroundProperty, Theming.Tokens.AppTokens.SurfacePanel);
            var badgeIcon = new M3Icon
            {
                Kind = iconKind, Width = 40, Height = 40,
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.OnContainer");
            badge.Child = badgeIcon;
            panel.Children.Add(badge);

            var titleText = new TextBlock
            {
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0)
            };
            titleText.SetText(title);
            titleText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.OnContainer");
            panel.Children.Add(titleText);
            if (subtitle is { IsEmpty: false })
            {
                var subtitleText = new TextBlock
                {
                    FontSize = 12,
                    Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    MaxWidth = 420,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
                };
                subtitleText.SetText(subtitle);
                subtitleText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
                panel.Children.Add(subtitleText);
            }
            return panel;
        }
    }
}

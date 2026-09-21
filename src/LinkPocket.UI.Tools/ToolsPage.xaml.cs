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

        // 主表行集（视图镜像 VM.Groups：探针/渲染共用；赋值只发生在 RunDedup/Clear 两处）
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
        /// </summary>
        public async void OnExternalDataChanged()
        {
            if (VmTools.HasRunDedup && DetailPanel.Visibility != Visibility.Visible
                && (ToolListbox.SelectedItem as ToolItem)?.Id == "dedup")
            {
                await RunDedupAsync();
            }
        }

        /// <summary>
        /// 进入工具页（Shell 经 MainViewModel.OnNavigatedToTools 调用）：**入口对齐**。
        /// 防抖刷新只送达"事件发生时的活跃页"——在别的页面改完数据再切回来时，去重结果
        /// （主表或明细）可能已经陈旧。这里按视图状态补齐：
        /// · 列表视图 → 直接重跑查重；
        /// · 明细视图 → 重跑后按 URL 重组当前组（组已不再重复 → 退回主表，主表即最新）。
        /// 非去重工具 / 从未跑过查重 → 不做事。
        /// </summary>
        public async void OnNavigatedTo()
        {
            if ((ToolListbox.SelectedItem as ToolItem)?.Id != "dedup" || !VmTools.HasRunDedup) return;

            var inDetail = DetailPanel.Visibility == Visibility.Visible;
            await RunDedupAsync();
            if (inDetail) ReconcileOpenDetail();
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
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(row.Url) { UseShellExecute = true });
            }
            catch { /* 无法打开时保持静默 */ }
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
            _dedupActionText.SetText(Loc.K("tools.dedup.start"));
            _dedupActionBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("PrimaryPillButton"),
                // ⚠️ 药丸样式自身不含 MinHeight/Padding：高度必须显式给（与其它页药丸统一的 32），
                // 否则垂直 Padding=0 会把按钮压扁成一条。
                Height = 32,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dedupActionIcon, _dedupActionText } }
            };
            _dedupActionBtn.Click += async (_, _) => await RunDedupAsync();
            HeaderActions.Children.Add(_dedupActionBtn);

            var clearLabel = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            clearLabel.SetText(Loc.K("tools.dedup.clear"));
            _dedupClearBtn = new Button
            {
                Height = 32,   // 同上：药丸样式无高度默认值，必须显式给
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
        // —— 去重：主表（重复组） ——
        // ============================================================

        private void SetupPaneTable()
        {
            PaneTable.Columns = new[]
            {
                new DataTableColumn
                {
                    Field = "url", LabelKey = "ui.noun.duplicateUrl", Width = -3,
                    SortKey = r => (IComparable)((DedupGroupRow)r).Url,
                    CellFactory = r =>
                    {
                        var cell = new TextBlock
                        {
                            Text = ((DedupGroupRow)r).Url,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        // 字体/文字色一律走**资源引用**：一次性取值（FindResource 后赋值）会在
                        // 换主题或换字体后停在旧值上 —— 与表头底色同一根因。
                        cell.SetResourceReference(TextElement.FontFamilyProperty, Theming.Tokens.AppTokens.FontMono);
                        cell.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
                        return cell;
                    }
                },
                new DataTableColumn
                {
                    Field = "count", LabelKey = "ui.noun.duplicateCount", Width = 90,
                    SortKey = r => (IComparable)((DedupGroupRow)r).Count,
                    CellFactory = r =>
                    {
                        var countText = new TextBlock
                        {
                            Text = $"×{((DedupGroupRow)r).Count}",
                            FontSize = 12, FontWeight = FontWeights.SemiBold
                        };
                        countText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.OnContainer");
                        var chip = new Border
                        {
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(8, 2, 8, 2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = countText
                        };
                        chip.SetResourceReference(Border.BackgroundProperty, "App.Accent.Container");
                        return chip;
                    }
                },
                new DataTableColumn
                {
                    Field = "locations", LabelKey = "ui.noun.locatedIn", Width = -2,
                    SortKey = r => (IComparable)((DedupGroupRow)r).LocationsSummary.Resolve(),
                    CellFactory = r => TextCell(((DedupGroupRow)r).LocationsSummary)
                },
            };

            PaneTable.RowClick += (_, item) => EnterDetail((DedupGroupRow)item);
            PaneTable.RowDoubleClick += (_, item) => EnterDetail((DedupGroupRow)item);
            ShowDedupPlaceholder();
        }

        /// <summary>
        /// 查重主流程：业务在 <see cref="ToolsViewModel.RunDedupAsync"/>，本方法只负责
        /// 加载/空态/结果四种视觉状态的切换与操作按钮文案（探针经本方法反射驱动）。
        /// </summary>
        private async Task RunDedupAsync(bool navigating = false)
        {
            if (navigating) DetailBusyOverlay.IsBusy = true;   // 只有用户发起的重查才亮遮罩
            try
            {
                await RunDedupCoreAsync();
            }
            finally
            {
                if (navigating) DetailBusyOverlay.IsBusy = false;
            }
        }

        private async Task RunDedupCoreAsync()
        {
            // ⚠️ 扫描期间**不清表**（保留旧结果，内容未变时下方直接跳过重设）：清空 + 重新填充 =
            // 切页/重扫时的整表重建白烧 + 视觉闪空（低性能设备切页偶发卡顿）。
            PaneTable.EmptyContent = BuildState("refresh", Loc.K("tools.dedup.scanningTitle"), Loc.K("tools.dedup.scanningHint"));
            PaneSubtitle.SetText(DedupSubtitle);

            List<DedupGroupRow> groups;
            try
            {
                groups = await VmTools.RunDedupAsync();
            }
            catch (Exception ex)
            {
                LpLog.Error("duplicate scan failed", ex);
                PaneTable.EmptyContent = BuildState("alert-circle-outline", Loc.K("tools.dedup.readFailedTitle"), Loc.K("err.unexpected"));
                return;
            }

            var previous = _groups;   // 旧结果（视图镜像）：下面据此判断"要不要重设表格"
            _groups = groups;
            if (_dedupActionIcon != null) _dedupActionIcon.Kind = "refresh";
            if (_dedupActionText != null) _dedupActionText.SetText(Loc.K("tools.dedup.again"));
            if (_dedupClearBtn != null) _dedupClearBtn.IsEnabled = true;

            if (groups.Count == 0)
            {
                if (previous.Count > 0) PaneTable.ItemsSource = null;   // 结果全消失 → 清表让空态可见
                PaneTable.EmptyContent = BuildState("content-duplicate", Loc.K("tools.dedup.noneTitle"),
                    Loc.K("tools.dedup.noneHint"));
                PaneSubtitle.SetText(Loc.K("tools.dedup.none"));
                return;
            }

            // 内容未变（重扫/切回常见）→ **不重设 ItemsSource**：工厂模式重设 = 整表重建（同步主线程）
            if (!DedupGroupRow.SameSequence(previous, groups)) PaneTable.ItemsSource = groups;
            PaneTable.EmptyContent = null!;
            PaneSubtitle.SetText(Loc.K("tools.dedup.foundSubtitle", groups.Count, groups.Sum(g => g.Count)));
        }

        // ============================================================
        // —— 去重：明细（组内各条） ——
        // ============================================================

        private void SetupDetailTable()
        {
            DetailTable.Columns = new[]
            {
                new DataTableColumn
                {
                    Field = "check", LabelKey = "", Width = 44,
                    CellFactory = BuildCheckCell
                },
                new DataTableColumn
                {
                    Field = "title", LabelKey = "ui.noun.name", Width = -1,
                    SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkDto)r).Title) ? ((LinkDto)r).Url : ((LinkDto)r).Title),
                    CellFactory = r => BuildNameCell((LinkDto)r)
                },
                new DataTableColumn
                {
                    // 与搜索页/智能列表同口径：路径最宽，右侧时间列压缩到刚好够用
                    Field = "path", LabelKey = "ui.noun.location", Width = -3,
                    SortKey = r => (IComparable)VmTools.ResolvePath((LinkDto)r).Resolve(),
                    CellFactory = r =>
                    {
                        return TextCell(VmTools.ResolvePath((LinkDto)r));
                    }
                },
                new DataTableColumn
                {
                    Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 130,
                    SortKey = r => (IComparable)((LinkDto)r).UpdatedAt,
                    CellFactory = r => TextCell(UiClock.Text(((LinkDto)r).UpdatedAt.ToLocalTime()))
                },
                new DataTableColumn
                {
                    Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 130,
                    SortKey = r => (IComparable)(((LinkDto)r).LastVisitedAt ?? DateTime.MinValue),
                    CellFactory = r => ((LinkDto)r).LastVisitedAt is { } visited
                        ? TextCell(UiClock.Text(visited.ToLocalTime()))
                        : TextCell(Loc.K("clock.never"))
                },
                new DataTableColumn
                {
                    Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 84,
                    SortKey = r => (IComparable)((LinkDto)r).VisitCount,
                    CellFactory = r => TextCell(Loc.K("count.viewsN", ((LinkDto)r).VisitCount))
                },
                // 重复组明细**没有**「操作」列 / 行内跳转按钮：
                // 对比页是"看差异"的只读视图；「跳转」（顶部 URL 组药丸 / 右栏图标钮）走定位组件 IContentLocator
                // ——与「ID 跳转」工具同一套语义（进目录 + 选中行），本页不自带定位算法。
            };

            // 外部托管选中（SelectionEnabled=False）：单选中由 ToolsViewModel.DetailSelection（共享核心）承载，
            // 行绘制 + 右栏统一经 ApplyDetailSelectionProjection 一处投影
            DetailTable.RowClick += (_, item) => VmTools.DetailSelection.SelectSingle(((LinkDto)item).LinkId);
        }

        /// <summary>当前明细行的数据镜像（EnterDetail 赋值；选中投影 / ↑↓ 顺序 / 右栏都读它）。</summary>
        private List<LinkDto> _detailLinks = new();

        /// <summary>
        /// 展开明细：组状态记录在 VM（EnterGroup），本方法只做视觉切换。
        /// <paramref name="preserveSelection"/> = true 时**保留仍在新结果里的选中**（F5 重查 / 入口对齐重进）；
        /// 换组进入时为 false（清选中）。
        /// </summary>
        private void EnterDetail(DedupGroupRow row, bool preserveSelection = false)
        {
            var previous = preserveSelection ? VmTools.DetailSelection.Ids.ToList() : null;

            VmTools.EnterGroup(row);   // 进组即清选中（换组语义）；需要保留的由下面按新结果重新落回
            _detailLinks = row.Links;

            DetailUrlText.Text = row.Url;
            DetailHintText.SetText(Loc.K("tools.dedup.groupHint", row.Count));
            DetailTable.ItemsSource = null;
            DetailTable.ItemsSource = row.Links;
            DetailTable.EmptyContent = null!;

            if (previous is { Count: > 0 })
                VmTools.DetailSelection.Set(previous.Where(id => row.Links.Any(l => l.LinkId == id)));

            ApplyDetailSelectionProjection();   // 行重建后同步行绘制与右栏（保留的选中在此重投）
            UpdateDeleteState();
            MainPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            // 焦点收回**页面根**（原先 Focus 明细面板容器：容器拿到键盘焦点后会画一条原生焦点虚线框，
            // 表现为明细页出现黑虚线）。焦点在页内 = 快捷键照常路由（ShortcutHost 不变式）。
            PageFocus.Restore(this);
        }

        private void GoBackToList()
        {
            VmTools.LeaveGroup();
            _detailLinks = new List<LinkDto>();
            DetailTable.ItemsSource = null;
            DetailPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e) => GoBackToList();

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(VmTools.CurrentGroupUrl))
            {
                try { Clipboard.SetText(VmTools.CurrentGroupUrl); } catch { }
            }
        }

        /// <summary>勾选单元：MD3 圆形勾选（选中 = Primary 实心 + 白勾，未选 = 描边圆）。守卫规则在 VM。</summary>
        private FrameworkElement BuildCheckCell(object data)
        {
            var link = (LinkDto)data;
            var checkedNow = VmTools.CheckedIds.Contains(link.LinkId);

            var outline = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.6),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            outline.SetResourceReference(Border.BorderBrushProperty, "App.Text.Secondary");
            var fill = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Visibility = checkedNow ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Path
                {
                    Data = Geometry.Parse("M1,4.6 L3.6,7.1 L8,1.8"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            fill.SetResourceReference(Border.BackgroundProperty, "App.Accent.Fill");

            var host = new Grid { Width = 22, Height = 22 };
            host.Children.Add(outline);
            host.Children.Add(fill);

            var deleteTip = Loc.T("tools.dedup.deleteChecked");
            var button = new Button
            {
                Content = host,
                Width = 30, Height = 26,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Style = (Style)FindResource("RowIconButton"),
                ToolTip = deleteTip
            };
            button.Click += async (_, _) =>
            {
                if (!VmTools.ToggleChecked(link.LinkId))
                {
                    await FlashSelectionInfo(Loc.K("tools.dedup.keepOne"));
                    return;
                }

                var nowChecked = VmTools.CheckedIds.Contains(link.LinkId);
                fill.Visibility = nowChecked ? Visibility.Visible : Visibility.Collapsed;
                UpdateDeleteState();
            };

            return button;
        }

        private FrameworkElement BuildNameCell(LinkDto link)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var iconGrid = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var faviconBmp = FaviconService.LoadFromCache(link.FaviconUrl);
            var faviconImg = new Image
            {
                Source = faviconBmp,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
            if (faviconBmp == null) faviconImg.Visibility = Visibility.Collapsed;

            var earthIcon = new M3Icon
            {
                Kind = "earth", Width = 16, Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            earthIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Muted");
            if (faviconBmp != null) earthIcon.Visibility = Visibility.Collapsed;
            iconGrid.Children.Add(faviconImg);
            iconGrid.Children.Add(earthIcon);

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl) && faviconBmp == null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FaviconService.PrefetchAndCacheAsync(link.FaviconUrl);
                        var cached = FaviconService.LoadFromCache(link.FaviconUrl);
                        if (cached != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                faviconImg.Source = cached;
                                faviconImg.Visibility = Visibility.Visible;
                                earthIcon.Visibility = Visibility.Collapsed;
                            });
                        }
                    }
                    catch { }
                });
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // 标题是用户数据；没有标题时画「无标题」（文案值，跟语言走）
            if (string.IsNullOrWhiteSpace(link.Title)) nameText.SetText(Loc.K("tools.untitled"));
            else nameText.Text = link.Title;
            nameText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
            textStack.Children.Add(nameText);
            var urlText = new TextBlock
            {
                Text = link.Url,
                FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            urlText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            textStack.Children.Add(urlText);

            panel.Children.Add(iconGrid);
            panel.Children.Add(textStack);
            return panel;
        }

        /// <summary>
        /// 数据单元格（用户数据：URL / 标题 / 时间戳）。
        /// ⚠️ 走 <see cref="LocValue.Literal"/> 而不是 <c>LocValue.Of</c>：后者把这串当**文案键**查表，
        /// 查不到就画出 <c>⟨…⟩</c> 缺键哨兵——时间戳与 URL 不是键，那样渲染出来的日期是错的。
        /// </summary>
        private TextBlock TextCell(string text) => TextCell(LocValue.Literal(text));

        /// <summary>
        /// 两个长度形态的单元格（日期/计数这类结构化列：放不下时换短式，不缩字号）。
        /// 参数用全限定名：UIKit 的代码写文字门面 <c>LinkPocket.Views.LocText</c> 与本类型同名，
        /// 而本文件两个命名空间都 using 了。
        /// </summary>
        private TextBlock TextCell(LinkPocket.I18n.LocText text)
            => LocFitResolver.BuildCell(text);

        /// <summary>文案单元格（键 + 参数；语言一变自己重算）。</summary>
        private TextBlock TextCell(LocValue text)
        {
            var cell = new TextBlock
            {
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            cell.SetText(text);
            cell.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            return cell;
        }

        private void UpdateDeleteState()
        {
            var count = VmTools.CheckedIds.Count;
            DeleteSelectedBtn.IsEnabled = count > 0;
            SelectionInfoText.SetText(count > 0 ? Loc.K("tools.checkedCount", count) : LocValue.Empty);
        }

        /// <summary>临时提示（不打断操作）：显示一句短提示后恢复勾选计数。</summary>
        private async Task FlashSelectionInfo(LocValue message)
        {
            SelectionInfoText.SetText(message);
            await Task.Delay(1600);
            if (DetailPanel.Visibility == Visibility.Visible) UpdateDeleteState();
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.CheckedIds.Count == 0) return;

            var count = VmTools.CheckedIds.Count;
            if (!ConfirmDialog.Show(Loc.T("tools.dedup.deleteTitle"), Loc.T("tools.dedup.deleteConfirm", count), Loc.T("common.delete"), "delete-outline"))
                return;

            try
            {
                // 业务在 VM：逐条移入回收站 → 刷新目录树计数 → 重算当前组
                var rest = await VmTools.DeleteCheckedAsync();
                if (rest != null)
                {
                    DetailHintText.SetText(Loc.K("tools.dedup.groupHint", rest.Count));
                    DetailTable.ItemsSource = null;
                    DetailTable.ItemsSource = rest;
                    UpdateDeleteState();
                }
                else
                {
                    GoBackToList();
                    await RunDedupAsync();
                }
            }
            catch (Exception ex)
            {
                LpLog.Error("duplicate deletion failed", ex);
                await FlashSelectionInfo(Loc.K("tools.dedup.deleteFailed"));
            }
        }

        // ============================================================
        // —— ID 跳转（统一走 IContentLocator 组件；定位在 ToolsViewModel.JumpAsync） ——
        // ============================================================

        private void ResetIdJumpForm()
        {
            IdInput.Text = string.Empty;
            HideJumpHint();
        }

        private void HideJumpHint()
        {
            JumpHintChip.Visibility = Visibility.Collapsed;
            JumpHintText.Text = string.Empty;
        }

        private void ShowJumpHint(LocValue message)
        {
            JumpHintText.SetText(message);
            JumpHintChip.Visibility = Visibility.Visible;
        }

        private void Jump_Click(object sender, RoutedEventArgs e) => _ = JumpFromInputAsync();

        private async Task JumpFromInputAsync()
        {
            HideJumpHint();
            var id = IdInput.Text.Trim();
            if (string.IsNullOrEmpty(id))
            {
                ShowJumpHint(Loc.K("tools.id.prompt"));
                return;
            }

            var result = await JumpToIdAsync(id);
            if (result.IsSuccess)
            {
                // 已切到浏览页并选中目标：清空输入，避免下次进来还残留旧 ID
                ResetIdJumpForm();
            }
        }

        /// <summary>跳转统一入口：定位在 <see cref="ToolsViewModel.JumpAsync"/>（组件），本方法只负责提示渲染。</summary>
        private async Task<LocateResult> JumpToIdAsync(string id)
        {
            var result = await VmTools.JumpAsync(id);
            if (!result.IsSuccess && DetailPanel.Visibility != Visibility.Visible)
            {
                ShowJumpHint(result.Message ?? result.Status switch
                {
                    LocateStatus.NotFound => Loc.K("locate.notFoundId"),
                    LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
                    LocateStatus.Failed => Loc.K("locate.failedRetry"),
                    _ => Loc.K("locate.incomplete"),
                });
            }
            return result;
        }

        // ============================================================
        // —— 书签导入 / 导出（协议调用在 ToolsViewModel；本页只做选文件/选目录、预检展示、结果展示） ——
        // ============================================================

        private void SegImport_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.BookmarkBusy) { SyncBookmarkSegments(); return; }
            SetBookmarkMode(importing: true);
        }

        private void SegExport_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.BookmarkBusy) { SyncBookmarkSegments(); return; }
            SetBookmarkMode(importing: false);
        }

        private void SetBookmarkMode(bool importing)
        {
            SegImport.IsChecked = importing;
            SegExport.IsChecked = !importing;
            BookmarkImportCard.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            BookmarkExportCard.Visibility = importing ? Visibility.Collapsed : Visibility.Visible;
            MoveSegIndicator(animated: true);
        }

        private void SyncBookmarkSegments()
        {
            var importing = BookmarkImportCard.Visibility == Visibility.Visible;
            SegImport.IsChecked = importing;
            SegExport.IsChecked = !importing;
            MoveSegIndicator(animated: false);
        }

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

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
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

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LinkPocket.Input;
using LinkPocket.ViewModels;
using LinkPocket.I18n;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 资源管理器式浏览页（P4）。视图只负责渲染与鼠标交互转发，
/// 业务逻辑全部在 BrowserViewModel（数据经协议、行状态在行 VM 上）。
/// </summary>
public partial class BrowserView : UserControl
{

    /// <summary>已装配的 VM（DataContext 换绑时先解绑旧的——`-=` 只能解当前绑定的实例）。</summary>
    private BrowserViewModel? _wiredVm;

    /// <summary>快捷键宿主（键位表注册 + 栏作用域分发；见 LinkPocket.Input.ShortcutHost）。</summary>
    private ShortcutHost? _shortcutHost;

    /// <summary>快捷键活跃作用域：按**栏归属**（点击主栏/左栏即切换）——"↑/↓ 两栏语义不同"的仲裁依据。</summary>
    private ShortcutScope ActiveScope => ViewModel?.ActivePane == BrowserPane.Tree
        ? ShortcutScope.BrowserTree
        : ShortcutScope.BrowserMain;

    /// <summary>
    /// 本页「动作 id → 命令」映射（**键位不在本文件**：全站键位只声明在 <see cref="ShortcutCatalog"/>；
    /// 本页只负责把总表里的动作接到自己的命令上）。漏接线会在装配时抛（启动即暴露）。
    /// </summary>
    private static ShortcutCommandMap BuildShortcutCommands(BrowserViewModel vm) => new ShortcutCommandMap()
        .Add(ShortcutAction.BrowserCut, vm.CutCommand)
        .Add(ShortcutAction.BrowserCopy, vm.CopyCommand)
        .Add(ShortcutAction.BrowserPaste, vm.PasteCommand)
        .Add(ShortcutAction.BrowserSelectAll, vm.SelectAllCommand)
        .Add(ShortcutAction.BrowserDelete, vm.DeleteSelectionCommand)
        .Add(ShortcutAction.BrowserRename, vm.RenameSelectionCommand)
        .Add(ShortcutAction.BrowserOpen, vm.OpenSelectionCommand)
        .Add(ShortcutAction.BrowserGoBack, vm.GoBackCommand)
        .Add(ShortcutAction.BrowserGoUp, vm.GoUpCommand)
        .Add(ShortcutAction.BrowserGoForward, vm.GoForwardCommand)
        .Add(ShortcutAction.BrowserRefresh, vm.RefreshCommand)
        .Add(ShortcutAction.BrowserFocusPath, vm.EnterPathEditCommand)
        .Add(ShortcutAction.BrowserExpandTreeToCurrent, vm.ExpandTreeToCurrentCommand)
        .Add(ShortcutAction.BrowserNewFolder, vm.NewFolderCommand)
        .Add(ShortcutAction.BrowserCopyPath, vm.CopyPathCommand)
        .Add(ShortcutAction.BrowserUndo, vm.UndoCommand)
        .Add(ShortcutAction.BrowserRedo, vm.RedoCommand)
        .Add(ShortcutAction.BrowserContextMenu, vm.ShowContextMenuCommand)
        .Add(ShortcutAction.BrowserMoveUp, vm.MoveSelectionCommand)
        .Add(ShortcutAction.BrowserMoveDown, vm.MoveSelectionCommand)
        .Add(ShortcutAction.BrowserSelectLast, vm.SelectLastCommand)
        .Add(ShortcutAction.BrowserTreeUp, vm.MoveTreeSelectionCommand)
        .Add(ShortcutAction.BrowserTreeDown, vm.MoveTreeSelectionCommand)
        .Add(ShortcutAction.BrowserTreeCollapse, vm.ToggleTreeExpandCommand)
        .Add(ShortcutAction.BrowserTreeExpand, vm.ToggleTreeExpandCommand)
        .Add(ShortcutAction.BrowserEscape, vm.EscapeCommand)
        .Add(ShortcutAction.BrowserNavigateToSearch, vm.NavigateToSearchCommand);

    public BrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // ⚠️ MainWindow 先设自身 DataContext（MainViewModel）→ 继承级联会先触发本事件，
            // 此时 ViewModel 还不是 BrowserViewModel；必须跳过并等待真正的一次，
            // 「装载过就置守卫」只能在成功路径上做（否则守卫被中间态污染，列定义永远装不进去）。
            if (ViewModel == null) return;
            if (ReferenceEquals(_wiredVm, ViewModel)) return;   // 同一 VM 重复触发：已装配

            // 换绑到新 VM 前，先解绑旧 VM 的订阅（否则旧 VM 的变更仍会驱动本视图，且订阅线性增长）
            if (_wiredVm != null)
            {
                _wiredVm.FocusRowRequested -= OnFocusRowRequested;
                _wiredVm.PaneActivated -= OnPaneActivated;
                _wiredVm.RefreshCompleted -= OnRefreshCompleted;
                _wiredVm.ContextMenuRequested -= OnContextMenuRequested;
            }
            _wiredVm = ViewModel;

            ViewModel.FocusRowRequested += OnFocusRowRequested;
            ViewModel.PaneActivated += OnPaneActivated;
            ViewModel.RefreshCompleted += OnRefreshCompleted;
            ViewModel.ContextMenuRequested += OnContextMenuRequested;
            WireMainTableOnce();

            // 快捷键子系统：键位表 = ShortcutCatalog（全站唯一事实源——本页不再声明任何键位，
            // 只提供「动作 id → 命令」映射）；活跃作用域按"栏归属"（ActivePane）解析。
            _shortcutHost?.Detach();
            _shortcutHost = new ShortcutHost(
                ShortcutCatalog.Build(ShortcutPage.Browser, BuildShortcutCommands(ViewModel)), () => ActiveScope);
            _shortcutHost.Attach(this);
            // 右键「刷新」用**同一个命令对象**（F5 那条），不做第二条刷新路径
            if (ViewModel is { } browserVm)
            {
                PageRefresh.Register(this, browserVm.RefreshCommand);
                // 分页续载：滚动接近底部 → 自动追加下一页（VM 内有页号/在飞/切换三重短路）
                MainTable.ScrollNearBottom += (_, _) => _ = browserVm.RequestNextPageAsync();
            }
        };

        // 页面被切到前台（全局导航切页）→ 键盘焦点收进本页：
        // 快捷键（ShortcutHost 挂在页面根）只在"焦点在页面内"时才被路由到——主栏行是不可聚焦 Border，
        // 点行不会自己带走焦点；不主动收焦点就会出现"快捷键时灵时不灵"（曾实测：切页后 Ctrl+C 无效）。
        IsVisibleChanged += (_, _) => { if (IsVisible) PageFocus.Take(this); };
    }

    // 焦点不变式已收口到 UIKit `Views.PageFocus`（**唯一实现**，浏览页/回收站/搜索页/智能列表/去重明细共用）：
    // · `PageFocus.Take(this)`    = 主动把焦点收进页根（栏激活 / 页变可见）
    // · `PageFocus.Restore(this)` = 刷新链结束 / 点空白后守住"焦点在页内"（三条不抢：页不可见 / 应用不在前台 / 正在编辑文本）

    /// <summary>
    /// 类级处理器：**任何**右键菜单打开 → 收掉就地改名的编辑态；菜单关闭 → 落地挂起的改名提交。
    ///
    /// <para>为什么用类级（静态注册）而不是页面级 <c>ContextMenuOpening</c>：后者只在**鼠标右键消息**路径触发，
    /// 覆盖不到 Shift+F10 / 菜单键（那条路径由本视图 <c>IsOpen = true</c> 打开）——渲染检查即因此漏检；
    /// 而 <c>Opened/Closed</c> 覆盖所有打开路径（右键、键盘、程序直设），一处监听即全。</para>
    /// <para>关闭时才提交：提交会写库 → 事件刷新重建行 → 承载菜单的行被销毁 → 菜单被连带关掉（"菜单一闪就没了"）。</para>
    /// </summary>
    static BrowserView()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnAnyContextMenuOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler(OnAnyContextMenuClosed));
    }

    private static void OnAnyContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // 右键拖拽手势期间：右键抬起还会触发一次系统菜单（WM_CONTEXTMENU），必须压掉——
        // 否则会在本视图的「复制到此处 / 移动到此处 / 取消」菜单之外再弹一个行/树菜单。
        // 本视图弹出的那张菜单是例外（它正是该手势的产物），放行后继续走下面的通用收口。
        if (FindHostView(sender) is { } host && host._rightDragGesture)
        {
            if (ReferenceEquals(sender, host._rightDragMenu)) host._rightDragMenu = null;
            else if (sender is ContextMenu stray) { stray.IsOpen = false; return; }
        }

        if (FindHostViewModel(sender) is not { } vm) return;
        // 豁免改名编辑框自身的右键菜单（TextBox 自带的剪切/复制/粘贴）：在编辑框里右键不结束改名
        if (sender is ContextMenu menu && InlineNameEditor.IsWithin(menu.PlacementTarget)) return;
        vm.CommitActiveRename();
    }

    /// <summary>
    /// 从菜单反查浏览页**视图**（右键拖拽的菜单抑制用）：菜单的 PlacementTarget 必在可视树上，
    /// 由它上溯到承载它的 <see cref="BrowserView"/>。反查不到（其它页面的菜单）→ null = 不参与抑制。
    /// </summary>
    private static BrowserView? FindHostView(object? sender)
    {
        if (sender is not ContextMenu menu) return null;
        var node = menu.PlacementTarget as DependencyObject;
        while (node != null)
        {
            if (node is BrowserView view) return view;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static void OnAnyContextMenuClosed(object sender, RoutedEventArgs e)
        => FindHostViewModel(sender)?.FlushDeferredCommit();

    /// <summary>
    /// 从菜单反查浏览页 VM：行菜单 DataContext = 行 VM（<c>Host</c>）；树菜单 = 节点（<c>Host</c>）；
    /// 列表空白菜单 = 页面 VM。其它页面的菜单反查为 null → 本处理器无操作。
    /// </summary>
    private static BrowserViewModel? FindHostViewModel(object? sender)
        => sender is not ContextMenu menu
            ? null
            : menu.DataContext switch
            {
                BrowserViewModel vm => vm,
                BrowserRowViewModel row => row.Host,
                FolderNode node => node.Host,
                _ => null
            };

    /// <summary>
    /// Shift+F10 / 菜单键：为当前选中行弹右键菜单（Windows 口径：键盘打开与右键同一张菜单）。
    /// 菜单挂在行模板的 Border 上（ContextMenu 半离线，只能从行容器取），故先按选中行找到容器再打开。
    /// </summary>
    private void OnContextMenuRequested(object? sender, EventArgs e)
    {
        if (ViewModel == null) return;
        var row = ViewModel.SelectedRows.FirstOrDefault();
        if (row == null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (MainTable.RowsList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return;
            var border = FindTaggedBorder(container);
            if (border?.ContextMenu is not { } menu) return;
            ViewModel.SetContextRow(row);          // 删除文案按Loc.T("browser.dialog.whatWillBeDeleted")算
            menu.PlacementTarget = border;
            menu.IsOpen = true;
        }));
    }

    /// <summary>行容器（模板模式）内部承载右键菜单的 RowBorder（Tag=BrowserRow）。</summary>
    private static Border? FindTaggedBorder(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && "BrowserRow".Equals(b.Tag as string)) return b;
            if (FindTaggedBorder(child) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>某栏被激活（点击主栏/左栏）→ 焦点归位到本页，页面级快捷键随即可用。</summary>
    private void OnPaneActivated(object? sender, BrowserPane pane) => PageFocus.Take(this);

    /// <summary>共享表是否已装载列定义（仅在成功装载后置位，见构造函数中的注释）。</summary>
    private bool _mainTableWired;

    /// <summary>
    /// 主栏共享数据表（views:SortableDataTable，与搜索结果表同一份实现）：
    /// 列定义 = 数据（Field/Label/Width），排序走 SortChanged 事件转 VM（服务端排序），
    /// 行 = ItemTemplate 模板模式（本页 XAML），列宽单一数据源在控件 ColumnWidths。
    /// </summary>
    private void WireMainTableOnce()
    {
        if (_mainTableWired || ViewModel == null) return;
        MainTable.SortField = ViewModel.SortBy;   // 关键装配语句先行：中途抛异常不应把 flag 置位导致永不重试
        MainTable.SortAscending = ViewModel.SortOrder != "desc";
        _mainTableWired = true;
        MainTable.Columns = new[]
        {
            new DataTableColumn { Field = "title", LabelKey = "ui.noun.name", Width = -1 },
            new DataTableColumn { Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 140 },
            new DataTableColumn { Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 140 },
            new DataTableColumn { Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 80 },
            new DataTableColumn { Field = "created_at", LabelKey = "ui.noun.createdAt", Width = 140 },
        };
        MainTable.SortChanged += (_, e) => ViewModel.ApplySort(e.Field, e.Ascending);
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    // —— 定位跳转：把刚选中的行滚入视口 ——
    // 主栏是共享数据表（UI 虚拟化）：视口外的行还没有容器，必须先估算偏移滚过去，
    // 等布局完成容器落地后再 BringIntoView 精确对齐，否则"跳转过去了但看不见"。

    private void OnFocusRowRequested(object? sender, BrowserRowViewModel row) => ScrollRowIntoView(row);

    private void ScrollRowIntoView(BrowserRowViewModel row)
    {
        if (ViewModel == null) return;

        // 唯一实现 = 共享数据表的 ScrollItemIntoView（模板模式与工厂模式同一套：
        // 行未实现时按索引 + 平均行高估算滚动，容器实现后再让它自己对齐）。
        // ⚠️ 这里原先自带一套"从行列表**向上**找 ScrollViewer + 估算"的实现——列表虚拟化生效后，
        //    ScrollViewer 由行列表的**控件模板**生成（是行列表的**子孙**而不是祖先），
        //    那条向上查找必然返回 null ⇒ 跳转/定位静默地不滚动（探针实测：等待滚动 5 秒超时）。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => MainTable.ScrollItemIntoView(row)));
    }

    // 行错峰入场已收口到 UIKit `Views.RowEntrance.Play(rows)`（**唯一实现**，与回收站/搜索页/智能列表/去重明细共用）：
    // 只在**用户发起的刷新**（导航加载：打开文件夹 / 跳转 / 返回 / F5）后播放——触发条件由 VM 的
    // RefreshCompleted 明确给出；后台刷新（300ms 防抖、排序、跳转定位到当前目录…）一律静默
    //（曾按"Rows 集合有无变更"触发，导致移动之后的刷新也播动画）。

    /// <summary>刷新链结束：只有导航加载才播行入场动画；并守住"焦点在页内"不变式。</summary>
    private void OnRefreshCompleted(object? sender, bool wasNavigation)
    {
        if (wasNavigation) RowEntrance.Play(MainTable.RowsList);
        // 刷新会重建树/面包屑/列表：被聚焦的容器可能已被销毁、焦点掉到窗口（页外）。
        // 延到布局之后执行（容器重建完成再判焦点归属），保证"进入文件夹后 Ctrl+V 立即可用"。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => PageFocus.Restore(this)));
    }

    // （列头与列宽拖拽已由共享数据表控件 SortableDataTable 内部驱动：
    //   表头按 Columns 生成，拖拽只改控件的 ColumnWidths 单一数据源。）

    // —— 行点击路由（读修饰键：无=单选，Ctrl=翻转，Shift=区间）——
    // Windows 语义复刻（deferred selection，契约见 BEHAVIOR-CONTRACT §1.3）：
    // · 按下未选中行（无修饰键）→ **立即**单选（视觉即时反馈，且紧接着可以拖拽该行）；
    // · 按下已选中行 → 不动选中（多选拖拽要拖整个集合），收敛/翻转语义留到抬起；
    // · 抬起只处理"与按下属于同一次手势"的点击完成（见 Up 的归属校验），
    //   双击打开文件夹时列表会重建——第二击的按下发生在旧列表、抬起落在新列表上，
    //   该抬起绝不能被当作新列表行的单击（否则 = "双击进入后同位置行被误选"）。

    /// <summary>
    /// 抬起 = 点击完成：对"同一次手势"重放选择语义（无修饰键 = 收敛为该项；Ctrl = 翻转；Shift = 区间）。
    /// 归属校验三条件（缺一即丢弃，绝不作用于抬起处新命中的行）：
    /// ① 抬起与按下命中同一行对象；② 期间未进入拖拽；③ 按下是单击（ClickCount ≤ 1）。
    /// </summary>
    private void RowBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        var pressed = _pressRow;
        var mods = _pressModifiers;
        var clicks = _pressClickCount;
        _pressRow = null;

        if (ViewModel == null || row == null) return;
        if (!ReferenceEquals(row, pressed) || _dragStarted || clicks > 1) return;

        // Windows 口径（慢双击改名）两条件，缺一即不改名 —— 用户实测"老是误触改名、进不去文件夹"
        // 的根因就是缺了第二条：① 按下时它已是唯一选中；② **不是对同一行的快速连击**
        // （间隔 < 系统双击时间 = 用户想双击打开 → 改名让位）。第二条与 ClickCount 无关：
        // 选择/刷新会让行容器重建，重建后 WPF 的 ClickCount 会从 1 重新计数，于是"想双击进入"
        // 的第二击被当成单击，直接进了改名。
        // 注：Windows 还要求点在**名称**上；本项目未做这一条——见 WARNINGS 161（探针的合成事件
        // 送不到深层命中元素，无法验证该判据，宁可不做也不让改名整体失效）。
        if (_pressWasSoleSelection && !_pressRapidRepeat
            && mods == ModifierKeys.None && !ViewModel.IsRenaming)
        {
            ViewModel.BeginRenameRow(row);
            return;
        }

        ViewModel.SelectRowWithModifiers(row, mods);
    }

    /// <summary>
    /// 右键命中的行未选中时，先按 Explorer 语义改为单选该行；
    /// 无论是否改选中，都要把命中行告知 VM —— 删除文案要按"这一次会删掉什么"算
    /// （命中文件夹显示其内链接数，命中多选中的行显示选中项数）。
    /// </summary>
    private void RowBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 右键拖拽手势期间不弹行菜单（右键抬起会触发一次 WM_CONTEXTMENU）：
        // 本次手势的产物是「复制到此处 / 移动到此处 / 取消」，也不该顺手改选中。
        if (_rightDragGesture)
        {
            e.Handled = true;
            return;
        }

        if (sender is not FrameworkElement element
            || element.DataContext is not BrowserRowViewModel row
            || ViewModel == null) return;
        if (!row.IsSelected) ViewModel.SelectRowWithModifiers(row, ModifierKeys.None);
        ViewModel.SetContextRow(row);
        // 行菜单是**单实例共享**的（见 XAML `BrowserRowMenu`：此前每行容器各实例化一整套菜单，
        // 实测每行容器 ~2ms 布局）：这里显式把它换成"这次命中的行"—— 不依赖 PlacementTarget 的继承，
        // 也绝不让上一次打开的 DataContext 留着（否则菜单里的命令作用在上一行上）。
        if (element.ContextMenu is { } menu) menu.DataContext = row;
    }

        // —— 面包屑地址栏（Views/BreadcrumbBar）事件转接：编辑态与候选导航仍由 BrowserViewModel 驱动 ——

        /// <summary>点胶囊空白 = 进入路径编辑（Windows 11 口径；命令自带 CanExecute：编辑/改名中不重入）。</summary>
        private void Breadcrumb_EditRequested(object? sender, EventArgs e)
            => ViewModel?.EnterPathEditCommand.Execute(null);

        /// <summary>拖拽经过面包屑段（Windows 11 口径：路径段可接收拖来的文件）。
        /// 段都是文件夹（含「全部书签」根段 = 移到根）→ 一律是落点候选；**成环不在此判定**——
        /// 照常高亮 + 提示，松手后由传输流水线统一拒绝弹窗（与主栏行 / 树节点完全同口径）。</summary>
        private void Breadcrumb_CrumbDragOver(object? sender, CrumbDragEventArgs e)
        {
            try
            {
                if (ViewModel == null) return;
                var (id, name) = CrumbTargetOf(e.Segment);
                var mode = CurrentDropMode();
                ApplyDropTarget(new BrowserDropTarget(id, BrowserPane.Breadcrumb, name, mode));
                Breadcrumb.SetDropHighlight(e.Segment);
                e.Args.Effects = DragSupport.EffectFor(mode);
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        /// <summary>拖拽离开面包屑段：熄灭落点高亮与提示（覆盖式状态，离开清零）。</summary>
        private void Breadcrumb_CrumbDragLeave(object? sender, CrumbDragEventArgs e)
            => ClearDropTarget();

        /// <summary>落到面包屑段上：**只记"待执行意图"**（Drop 回调在 OLE 拖拽循环内，执行在
        /// <c>DoDragDrop</c> 返回之后由 <see cref="StartDrag"/> 统一做——与主栏行 / 树节点同一条收尾）。</summary>
        private void Breadcrumb_CrumbDrop(object? sender, CrumbDragEventArgs e)
        {
            try
            {
                var payload = e.Args.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
                if (payload == null || ViewModel == null) return;
                var (id, _) = CrumbTargetOf(e.Segment);
                _pendingDrop = (payload.Items, id, ViewModel.DropTargetMode);
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        /// <summary>段对象 → (文件夹 ID, 显示名)。段可能来自 VM 的 <see cref="BrowserCrumbViewModel"/>
        /// （FolderId）或控件的 <see cref="BreadcrumbSegment"/>（Id）——控件不认识业务类型，这里归一。</summary>
        private static (string? Id, string Name) CrumbTargetOf(object? segment) => segment switch
        {
            BrowserCrumbViewModel c => (c.FolderId, c.Name),
            LinkPocket.Views.BreadcrumbSegment s => (s.Id, s.Name),
            _ => (null, string.Empty)
        };

        private void Breadcrumb_CandidateMoveRequested(object? sender, CandidateMoveEventArgs e)
        {
            if (ViewModel == null) return;
            ViewModel.MoveCandidate(e.Delta);
        }

        private void Breadcrumb_EditFocusLost(object? sender, EventArgs e)
        {
            // 焦点移出路径框 → 退出编辑态（候选 ListBox Focusable=False，点击候选不会触发）
            if (ViewModel is { IsPathEditing: true })
                ViewModel.CancelPathEditCommand.Execute(null);
        }

        private void Breadcrumb_EditPopupClosed(object? sender, EventArgs e)
        {
            // 点击候选项以外区域关闭 Popup → 退出编辑态（与 Esc 一致）
            if (ViewModel is { IsPathEditing: true })
                ViewModel.CancelPathEditCommand.Execute(null);
        }

        private void Breadcrumb_CandidateChosen(object? sender, string? name)
        {
            if (!string.IsNullOrEmpty(name)) ViewModel?.ChooseCandidate(name);
        }

}

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LinkPocket.Input;
using LinkPocket.ViewModels;

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
                _wiredVm.PropertyChanged -= OnViewModelPropertyChanged;
                _wiredVm.FocusRowRequested -= OnFocusRowRequested;
                _wiredVm.PaneActivated -= OnPaneActivated;
                _wiredVm.RefreshCompleted -= OnRefreshCompleted;
                _wiredVm.ContextMenuRequested -= OnContextMenuRequested;
            }
            _wiredVm = ViewModel;

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.FocusRowRequested += OnFocusRowRequested;
            ViewModel.PaneActivated += OnPaneActivated;
            ViewModel.RefreshCompleted += OnRefreshCompleted;
            ViewModel.ContextMenuRequested += OnContextMenuRequested;
            WireMainTableOnce();

            // 快捷键子系统（Phase 1）：键位表 = BrowserShortcuts（唯一事实源，已从 XAML InputBindings 迁入），
            // 活跃作用域按"栏归属"（ActivePane）解析——点击主栏/左栏即切换，不再依赖"焦点碰巧在页面里"。
            _shortcutHost?.Detach();
            _shortcutHost = new ShortcutHost(BrowserShortcuts.CreateRegistry(ViewModel), () => ActiveScope);
            _shortcutHost.Attach(this);
        };

        // 页面被切到前台（全局导航切页）→ 键盘焦点收进本页：
        // 快捷键（ShortcutHost 挂在页面根）只在"焦点在页面内"时才被路由到——主栏行是不可聚焦 Border，
        // 点行不会自己带走焦点；不主动收焦点就会出现"快捷键时灵时不灵"（曾实测：切页后 Ctrl+C 无效）。
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusPage(); };
    }

    /// <summary>把键盘焦点收进页面根（Focusable=True；无焦点视觉框，见 XAML FocusVisualStyle=null）。</summary>
    private void FocusPage()
    {
        if (IsLoaded && IsVisible) Keyboard.Focus(this);
    }

    /// <summary>
    /// **焦点不变式**：页面可见且应用在前台时，键盘焦点必须在页内。
    /// 快捷键（ShortcutHost）挂在页面根、按焦点路由——焦点一旦掉出页面，整页快捷键静默失效。
    /// 而刷新会重建树 / 面包屑 / 列表：被聚焦的容器（树节点 TreeViewItem、面包屑按钮）随 `Clear()`
    /// 被移出可视树，**WPF 此时把焦点交给窗口（页外）**——于是"进入文件夹后 Ctrl+V 没反应，
    /// 必须点一下列表空白才恢复"（用户 2026-09-19 报障，探针 ③ 实测焦点从 TreeViewItem → MainWindow）。
    /// 修复 = 页面自己守住这条不变式（重建后把焦点收回），不再依赖"焦点碰巧在页内"。
    /// 三条不抢：页不可见（切到别的页）/ 应用不在前台（切走了或弹窗打开）/ 正在编辑文本。
    /// </summary>
    private void EnsurePageFocus()
    {
        if (!IsLoaded || !IsVisible) return;
        if (Window.GetWindow(this)?.IsActive != true) return;
        if (IsFocusWithinPage()) return;
        if (ShortcutHost.IsTextInputFocused()) return;   // 编辑中（地址栏）绝不抢
        Keyboard.Focus(this);
    }

    /// <summary>当前键盘焦点是否落在本页（含后代）。</summary>
    private bool IsFocusWithinPage()
    {
        var d = Keyboard.FocusedElement as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, this)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>
    /// 类级处理器：**任何**右键菜单打开 → 收掉就地改名的编辑态；菜单关闭 → 落地挂起的改名提交。
    ///
    /// <para>为什么用类级（静态注册）而不是页面级 <c>ContextMenuOpening</c>：后者只在**鼠标右键消息**路径触发，
    /// 覆盖不到 Shift+F10 / 菜单键（那条路径是我们自己 <c>IsOpen = true</c> 打开的）——实测探针即因此漏检；
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
        // 否则会在我们自己的「复制到此处 / 移动到此处 / 取消」菜单之外再弹一个行/树菜单。
        // 我们自己的那张菜单是例外（它正是这次手势的产物），放行后继续走下面的通用收口。
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
            ViewModel.SetContextRow(row);          // 删除文案按"这一次会删掉什么"算
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
    private void OnPaneActivated(object? sender, BrowserPane pane) => FocusPage();

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
            new DataTableColumn { Field = "title", Label = "名称", Width = -1 },
            new DataTableColumn { Field = "updated_at", Label = "最后更新", Width = 140 },
            new DataTableColumn { Field = "last_visited_at", Label = "最后查看", Width = 140 },
            new DataTableColumn { Field = "visit_count", Label = "查看次数", Width = 80 },
            new DataTableColumn { Field = "created_at", Label = "创建时间", Width = 140 },
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
        var list = MainTable.RowsList;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement realized)
            {
                realized.BringIntoView();
                return;
            }

            var scroller = FindAncestorScrollViewer(list);
            var index = ViewModel.Rows.IndexOf(row);
            if (scroller == null || index < 0) return;

            var rowHeight = EstimateRowHeight(list);
            scroller.ScrollToVerticalOffset(Math.Max(0, index * rowHeight - scroller.ViewportHeight / 3));

            // 容器实现后精确对齐（第二段）
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement afterScroll)
                    afterScroll.BringIntoView();
            }));
        }));
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child)
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current != null)
        {
            if (current is ScrollViewer sv) return sv;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>行高估算：优先取已实现容器的实测高度，否则用共享表的常规行高兜底。</summary>
    private static double EstimateRowHeight(ItemsControl list)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 1)
            return first.ActualHeight;
        return 36;
    }

    // —— 行错峰入场（MD3E）：**只在打开文件夹（导航加载）时**淡入 + 轻微上移，弹簧曲线 ——
    // 触发条件由 VM 的 RefreshCompleted 明确给出（该次刷新链是不是导航加载）：
    // 后台刷新（写操作后的 300ms 防抖、排序、跳转定位到当前目录…）一律静默——
    // 曾按"Rows 集合有无变更"触发，导致移动/粘贴后的那次刷新也重播入场动画（用户实测报障）。

    /// <summary>刷新链结束：只有导航加载才播行入场动画；并守住"焦点在页内"不变式（见 EnsurePageFocus）。</summary>
    private void OnRefreshCompleted(object? sender, bool wasNavigation)
    {
        if (wasNavigation) QueueRowEntrance();
        // 本轮刷新重建过树/面包屑/列表：被聚焦的容器可能已被销毁、焦点掉到窗口（页外）。
        // 延到布局之后执行（容器重建完成再判焦点归属），保证"进入文件夹后 Ctrl+V 立即可用"。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsurePageFocus));
    }

    private void QueueRowEntrance()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // 辅助功能：减少动态效果
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var rows = MainTable.RowsList;
            var idx = 0;
            for (var i = 0; i < rows.Items.Count && idx < 12; i++)
            {
                if (rows.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                {
                    PlayRowEntrance(fe, idx);
                    idx++;
                }
            }
        }));
    }

    private void PlayRowEntrance(FrameworkElement el, int index)
    {
        var tt = new TranslateTransform(0, 10);
        el.RenderTransform = tt;
        el.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 30);
        var oy = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }, BeginTime = begin };
        var oo = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin };

        // 结束后清除动画层并落终值（FillBehavior.Stop 会回退到本地值 0，行会永远透明）
        EventHandler done = (_, _) =>
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        };
        oo.Completed += done;
        oy.Completed += done;

        tt.BeginAnimation(TranslateTransform.YProperty, oy);
        el.BeginAnimation(UIElement.OpacityProperty, oo);
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

        // Windows 口径（慢双击改名）：对**按下时已是唯一选中**的行再次单击（单击 / 无修饰键 / 未拖拽）
        // → 进入就地改名。首次单击只是选中（按下即反馈），第二次单击才改名；
        // 双击的第二击 ClickCount = 2 已被上面的 `clicks > 1` 挡掉，二者互不干扰。
        if (_pressWasSoleSelection && mods == ModifierKeys.None && !ViewModel.IsRenaming)
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

        if ((sender as FrameworkElement)?.DataContext is not BrowserRowViewModel row || ViewModel == null) return;
        if (!row.IsSelected) ViewModel.SelectRowWithModifiers(row, ModifierKeys.None);
        ViewModel.SetContextRow(row);
    }

        // —— 面包屑地址栏（Views/BreadcrumbBar）事件转接：编辑态与候选导航仍由 BrowserViewModel 驱动 ——

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

    // —— 拖拽浮层 + 落点提示（Windows 11 手感）——

    /// <summary>本次拖拽的浮层（半透明行快照 + 移动/复制提示）；拖拽期间存在，结束即摘除。</summary>
    private DragVisualAdorner? _dragVisual;

    /// <summary>本次拖拽是否以 Esc 取消（OLE 在 <c>QueryContinueDrag</c> 里如实告知）。
    /// **取消不是失败**：收尾时据此跳过成环判定，绝不弹窗。</summary>
    private bool _dragCancelledByEscape;

    /// <summary>挂出浮层并开始跟踪指针位置（<c>GiveFeedback</c> 在拖拽期间持续触发——
    /// 拖拽时 WPF 不再派发 MouseMove，只能这样跟手）。</summary>
    private void ShowDragVisual(IReadOnlyList<DragItem> items)
    {
        _dragVisual = DragVisualAdorner.Attach(this);
        _dragCancelledByEscape = false;
        if (_dragVisual == null) return;
        _dragVisual.Show(items);
        _dragVisual.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
        // 拖拽源在本页内 → GiveFeedback / QueryContinueDrag 会冒泡到本页；处理器按方法组注册，成对移除（同一实例语义）
        AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnGiveFeedback));
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
    }

    /// <summary>摘除浮层（拖拽结束 / 拖拽被取消）。</summary>
    private void HideDragVisual()
    {
        if (_dragVisual == null) return;
        RemoveHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnGiveFeedback));
        RemoveHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
        _dragVisual.Detach();
        _dragVisual = null;
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragVisual == null || !GetCursorPos(out var screen)) return;
        // 屏幕物理像素 → 本页坐标（PointFromScreen 已处理 DPI 缩放）
        _dragVisual.UpdatePosition(PointFromScreen(new Point(screen.X, screen.Y)));
    }

    /// <summary>
    /// 拖拽中鼠标/键盘状态变化（**修饰键每次变化都会触发**——拖拽期间鼠标不动时 OLE 不再派发 DragOver，
    /// 这里是唯一能拿到"Ctrl 刚被按下/松开"的时机）：
    /// <list type="bullet">
    /// <item>记下 Esc 取消（收尾据此不弹成环窗）；</item>
    /// <item>把当前修饰键映射成落点模式写回 VM 的落点状态——**松手执行的动作与提示条说的永远一致**。
    /// （光标由 OLE 在 DragOver 时按同一规则设置，纯改键不成动鼠标时可能滞后一次，见 WARNINGS。）</item>
    /// </list>
    /// </summary>
    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _dragCancelledByEscape = true;
        ViewModel?.SetDropTargetMode(
            BrowserViewModel.ResolveDropMode((e.KeyStates & DragDropKeyStates.ControlKey) != 0));
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    /// <summary>写入落点（视图侧唯一出口）：VM 负责高亮投影，浮层负责提示文案——两者永远同步。</summary>
    private void ApplyDropTarget(BrowserDropTarget? target)
    {
        ViewModel?.SetDropTarget(target);
        _dragVisual?.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
    }

    /// <summary>清空落点（视图侧唯一出口）：离开可落点 / 拖拽结束时调用，避免残留高亮与残留提示。</summary>
    private void ClearDropTarget()
    {
        ViewModel?.ClearDropTarget();
        _dragVisual?.UpdateHint(string.Empty);
    }

    // —— 行拖拽（参考 Windows 资源管理器：按下 → 移动超过阈值 → 进入拖拽）——

    private Point _rowDragStart;

    /// <summary>本次按下命中的行（手势归属凭据；抬起时校验"同一次手势"用）。</summary>
    private BrowserRowViewModel? _pressRow;

    /// <summary>按下时的修饰键：抬起沿用按下时刻的值（中途变键不改变本次点击语义）。</summary>
    private ModifierKeys _pressModifiers;

    /// <summary>按下时的点击计数：≥2 = 双击手势的第二击，绝不承载选择语义（第二击只作打开）。</summary>
    private int _pressClickCount;

    /// <summary>本次手势是否已进入拖拽（拖拽结束的抬起不得再补做选择收敛）。</summary>
    private bool _dragStarted;

    /// <summary>右键拖拽：按下起点 + 命中的行（与左键各记一套，两种按钮的拖拽互不干扰）。</summary>
    private Point _rowRightDragStart;
    private BrowserRowViewModel? _rightPressRow;

    /// <summary>右键拖拽手势进行中：期间压掉行/树的右键菜单——右键抬起还会触发一次
    /// <c>WM_CONTEXTMENU</c>，不压就会在我们自己的「复制到此处 / 移动到此处」菜单之外再弹一个。
    /// 手势结束（我们的菜单关闭、或没弹出菜单时的本轮消息收尾）即复位。</summary>
    private bool _rightDragGesture;

    /// <summary>本次右键拖拽自己弹的菜单：类级 <c>Opened</c> 处理器据此**放行**（不能把自己的菜单也压掉）。</summary>
    private ContextMenu? _rightDragMenu;

    /// <summary>
    /// 本次拖拽松手时的**落点意图**（Drop 处理器只记录、**不执行**）。
    ///
    /// <para>为什么必须先记后执行：OLE 的 Drop 回调发生在拖拽模态循环**内部**——在那里直接执行会在
    /// "拖拽还没结束"时就弹窗 / 写库（实测：规范弹窗弹出来了、拖拽浮层还挂在屏幕上；
    /// 右键拖拽更糟——松开即被执行，用户还没点菜单东西就搬走了）。</para>
    ///
    /// <para>统一口径：Drop 只写这张"待执行单"，真正执行在 <see cref="StartDrag"/> 里
    /// （循环退出之后、浮层摘除之后）——**左键按它执行、右键拖拽忽略它**（改由菜单选择决定）。</para>
    /// </summary>
    private (IReadOnlyList<DragItem> Items, string? TargetId, TransferMode Mode)? _pendingDrop;

    /// <summary>按下时该行是否**已是唯一选中**（Windows 慢双击改名的判定依据：第一次单击选中，第二次单击改名）。</summary>
    private bool _pressWasSoleSelection;

    /// <summary>拖拽数据：本次拖动集合（<see cref="DragItem"/> 快照，与行/树 VM 解耦——
    /// 主栏行与树节点都能构造，放置端按 Id 通用）。</summary>
    public record BrowserDragPayload(IReadOnlyList<DragItem> Items);

    /// <summary>按下：记下手势凭据；无修饰键按未选中行 = 立即单选（Windows 按下即反馈）。</summary>
    private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowDragStart = e.GetPosition(this);
        _dragStarted = false;
        _pressRow = null;                      // 先清凭据：下面任何早退都不得留下上一次的手势
        _pressWasSoleSelection = false;
        _pressModifiers = Keyboard.Modifiers;
        _pressClickCount = e.ClickCount;

        // 就地改名编辑框内的鼠标操作（定位光标 / 选词 / 双击选词）归编辑框自己：
        // 不参与行选择、不进入拖拽、也不承载双击打开（否则双击编辑框会把目录打开）。
        if (InlineNameEditor.IsWithin(e.OriginalSource as DependencyObject)) return;

        _pressRow = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        if (ViewModel == null || _pressRow == null) return;
        ViewModel.ActivatePane(BrowserPane.Main);   // 点主栏 = 该栏获得键盘语义归属（焦点随之收进页面）
        // 记下"按下时它已是唯一选中"——抬起据此判定"再次单击同一项"（Windows 慢双击改名）
        _pressWasSoleSelection = _pressRow.IsSelected && ViewModel.SelectionCount == 1;
        if (_pressModifiers == ModifierKeys.None && !_pressRow.IsSelected)
            ViewModel.SelectRowWithModifiers(_pressRow, ModifierKeys.None);
    }

    /// <summary>
    /// 右键按下：只记拖拽起点与命中行（**右键不改选中**——选中语义只由左键单击与键盘决定；
    /// 也不在此提交改名，那由菜单打开时的类级处理器统一做）。
    /// </summary>
    private void RowBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowRightDragStart = e.GetPosition(this);
        _rightPressRow = InlineNameEditor.IsWithin(e.OriginalSource as DependencyObject)
            ? null
            : (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        _rightDragGesture = false;   // 新手势开始：上一次的抑制窗口到此为止
        _rightDragMenu = null;
    }

    /// <summary>
    /// 行上移动：按**按下的按钮**分派——左键超过阈值 = 左键拖拽，右键超过阈值 = 右键拖拽。
    /// 两者都只做"是否进入拖拽"的判定，真正的起手式与收尾在 <see cref="StartDrag"/>（一条路径，两处不各写一份）。
    /// </summary>
    private void RowBorder_MouseMove(object sender, MouseEventArgs e)
    {
        // 右键拖拽（Windows 口径：右键拖到目标松手 → 弹「复制到此处 / 移动到此处 / 取消」）
        if (e.RightButton == MouseButtonState.Pressed)
        {
            if (_rightPressRow == null) return;
            if (!BeyondDragThreshold(e.GetPosition(this), _rowRightDragStart)) return;
            var rightRow = _rightPressRow;
            _rightPressRow = null;   // 本次手势只发起一次
            StartRowDrag((DependencyObject)sender, rightRow, rightButton: true);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_pressRow == null) return;   // 按下不在行主体（如落在改名编辑框内）→ 不进入行拖拽
        if (!BeyondDragThreshold(e.GetPosition(this), _rowDragStart)) return;
        _dragStarted = true;             // 拖拽结束的抬起不得再补做选择收敛
        StartRowDrag((DependencyObject)sender, _pressRow, rightButton: false);
    }

    /// <summary>移动是否超过系统拖拽阈值（**唯一实现**：行左键 / 行右键 / 树节点共用同一口径）。</summary>
    private static bool BeyondDragThreshold(Point pos, Point start)
        => Math.Abs(pos.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
           Math.Abs(pos.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>当前修饰键 → 落点模式（**唯一**：Ctrl = 复制；各行/节点 DragOver 的提示与光标都读它）。</summary>
    private static TransferMode CurrentDropMode()
        => BrowserViewModel.ResolveDropMode((Keyboard.Modifiers & ModifierKeys.Control) != 0);

    /// <summary>模式 → OLE 效果（**唯一映射**：Copy 时 Windows 会画带加号的光标）。</summary>
    private static DragDropEffects EffectFor(TransferMode mode)
        => mode == TransferMode.Copy ? DragDropEffects.Copy : DragDropEffects.Move;

    /// <summary>
    /// 发起一次主栏行拖拽：载荷与选中语义收敛在 VM（<c>PrepareDragFromRow</c>——拖未选中行先单选、
    /// 拖已选中行拖整个集合），视图只负责浮层、OLE 循环与收尾。
    /// </summary>
    private void StartRowDrag(DependencyObject source, BrowserRowViewModel row, bool rightButton)
    {
        if (ViewModel == null) return;
        var items = ViewModel.PrepareDragFromRow(row);
        if (items.Count == 0) return;
        StartDrag(source, items, rightButton);
    }

    /// <summary>
    /// 一次拖拽的完整生命周期（行左键 / 行右键 / 树节点**共用**）：浮层 → OLE 循环 → 收尾。
    ///
    /// <para>允许的效果是 <c>Move | Copy</c>：只给 Move 的话 OLE 会把 DragOver 里设的 Copy **夹成 None**，
    /// 复制光标永远出不来（"按 Ctrl 拖动 = 复制"的前提）。</para>
    ///
    /// <para>收尾在**拖拽循环退出之后**才做（顺序很关键）：先摘浮层、再清落点，然后
    /// ① 左键 → 执行 Drop 记下的意图（**成环由传输流水线统一拒绝并弹规范弹窗**——与粘贴同一条路径）；
    /// ② 右键 → 弹「复制到「X」/ 移动到「X」/ 取消」，**用户不选就不搬任何东西**（Windows 口径）。</para>
    ///
    /// <para>Esc 取消 = OLE 不派发 Drop → 待执行单为空 → 什么都不做（**结构性保证**：
    /// 再也没有"途经记账"那类判据可以出错）。</para>
    /// </summary>
    private void StartDrag(DependencyObject source, IReadOnlyList<DragItem> items, bool rightButton)
    {
        _rightDragGesture = rightButton;
        _pendingDrop = null;
        ShowDragVisual(items);
        DragDrop.DoDragDrop(source, new DataObject(new BrowserDragPayload(items)),
            DragDropEffects.Move | DragDropEffects.Copy);
        HideDragVisual();

        // 落点状态（含模式）在清空**之前**读走：它就是"松手时用户看到的那个动作"的唯一事实来源
        //（模式由 DragOver 与 QueryContinueDrag 共同维护，这里绝不第二次判定 Ctrl）。
        var target = ViewModel?.DropTarget;
        var mode = target?.Mode ?? TransferMode.Move;
        var drop = _pendingDrop;
        _pendingDrop = null;
        ClearDropTarget();

        if (rightButton)
        {
            // Esc 取消 = 什么都没发生（Windows 同口径）：右键拖拽取消同样不弹菜单。
            if (_dragCancelledByEscape)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
                return;
            }
            // 右键拖拽**不执行** drop（用户还没选）；执行由菜单项决定（同一条传输流水线）。
            ShowRightDragDropMenu(items, target, mode);
            return;
        }

        if (drop is { } pending)
            _ = ViewModel?.DropItemsAsync(pending.Items, pending.TargetId, pending.Mode);
    }

    /// <summary>
    /// 右键拖拽松手菜单（Windows 口径）：在松手位置弹出「复制到此处 / 移动到此处 / 取消」。
    /// 目标 = **松手时的落点状态**（DragOver 写的唯一事实来源，含"列表空白 = 当前目录"）；
    /// 落点为空（非法目标 / 窗口外）= 不给菜单——没有可选项，也就没有"此处"。
    /// 菜单打开期间**保留落点高亮**（用户据此确认"此处"是哪里），菜单关闭时熄灭。
    /// </summary>
    private void ShowRightDragDropMenu(IReadOnlyList<DragItem> items, BrowserDropTarget? target, TransferMode mode)
    {
        if (ViewModel == null || target == null)
        {
            // 没弹出自己的菜单：抑制窗口延续到本轮消息处理收尾（右键抬起可能还会再触发一次系统菜单）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
            return;
        }

        var menu = new ContextMenu { DataContext = ViewModel };   // 与其它菜单同口径：打开即收口改名态
        menu.Resources.Add(typeof(MenuItem), (Style)FindResource("LpMenuItem"));

        foreach (var action in BuildRightDragMenuItems(items, target.FolderId, target.Name)) menu.Items.Add(action);
        menu.Items.Add(new Separator());
        var cancel = new MenuItem { Header = "取消" };
        cancel.Click += (_, _) => menu.IsOpen = false;
        menu.Items.Add(cancel);

        menu.Closed += (_, _) =>
        {
            ClearDropTarget();          // 手势彻底结束：落点高亮与提示一起熄灭
            _rightDragGesture = false;
            _rightDragMenu = null;
        };

        _rightDragMenu = menu;
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 右键拖拽菜单里的**动作项**（纯构造，不涉及弹出——探针据此直调断言内容）：
    /// 点它 = 用同一条传输流水线执行（不另开写库路径）。
    /// 顺序与 Windows 一致（复制在前），文案把"此处"直接写成目标名（不靠高亮去猜）。
    /// </summary>
    private List<MenuItem> BuildRightDragMenuItems(IReadOnlyList<DragItem> items, string? targetId, string targetName)
    {
        var copy = new MenuItem { Header = $"复制到「{targetName}」" };
        copy.Click += (_, _) => _ = ViewModel?.DropItemsAsync(items, targetId, TransferMode.Copy);

        var move = new MenuItem { Header = $"移动到「{targetName}」" };
        move.Click += (_, _) => _ = ViewModel?.DropItemsAsync(items, targetId, TransferMode.Move);

        return new List<MenuItem> { copy, move };
    }

    // 拖拽收尾（成环弹窗 + 右键菜单 + 执行）统一在 `StartDrag` 内：它同时服务主栏行与树节点。
    // 视图**不再**自带成环判定：成环（拖到自己/自己的子文件夹）与其它非法情形统一由传输流水线拒绝并弹窗
    // ——与"剪切粘贴"共用同一个弹窗，用户看到的口径只有一套。

    /// <summary>
    /// 落点候选判定（**唯一实现**，主栏行与树节点共用）：**主栏文件夹行 / 树非链接节点**才作落点。
    /// 链接行与树上的链接叶子**不是**落点（拖到书签上什么都不发生，与 Explorer 一致）。
    ///
    /// <para>⚠️ 成环（自身 / 自身后代）**不在这里判定**——落点一视同仁地高亮 + 显示提示，
    /// 松手之后由执行层（传输流水线）统一拒绝并弹规范弹窗；这是用户要求的统一口径
    /// （过去"悬停禁用光标 + 无提示"与"粘贴弹窗"是两套，现统一为都弹窗）。</para>
    /// </summary>
    private static bool IsDropPositionCandidate(object? dataContext)
        => dataContext is BrowserRowViewModel { IsFolder: true } or FolderNode { IsLink: false };

    private void RowBorder_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;

            // 拖拽中只表达"落在哪个文件夹"（高亮 + 「移动到/复制到 X」提示 + 对应光标），**不判成环**：
            // 落点是不是自己/自己的后代，由执行层在**松手之后**统一拒绝并弹窗。
            // 模式（Ctrl = 复制）随修饰键走同一条落点状态：提示文案与光标永远说的是同一件事。
            var ok = IsDropPositionCandidate(row);
            var mode = CurrentDropMode();
            ApplyDropTarget(ok ? new BrowserDropTarget(row!.Id, BrowserPane.Main, row.Name, mode) : null);
            e.Effects = ok ? EffectFor(mode) : DragDropEffects.None;
        }
        finally
        {
            e.Handled = true;   // 无论沿途是否异常，本事件归属拖拽流程（防冒泡到其它落点）
        }
    }

    /// <summary>拖拽离开行：熄灭落点高亮。落点是**覆盖式**状态（铁律 9），离开必须清零；
    /// 随即 DragOver 会重设真正的新落点，所以行间移动只会看到高亮"跟着指针走"。</summary>
    private void RowBorder_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    /// <summary>落在行上：**只记意图不执行**（执行在 <see cref="StartDrag"/> 里、拖拽循环退出之后）。</summary>
    private void RowBorder_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
            if (payload != null && IsDropPositionCandidate(row))
                _pendingDrop = (payload.Items, row!.Id, ViewModel?.DropTargetMode ?? TransferMode.Move);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // —— 列表卡空白落点（Windows 口径：拖到文件夹内容区空白 = 落在**当前所在文件夹**）——

    /// <summary>
    /// 拖到列表空白：落点 = **当前所在文件夹**（没有目标项，因此不高亮任何行，只给提示）。
    /// 行上的 DragOver 会 <c>e.Handled = true</c>，所以指针在行上时不会走到这里（行优先、语义更具体）。
    /// </summary>
    private void ListCard_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            if (payload == null || ViewModel == null) return;
            var mode = CurrentDropMode();
            ApplyDropTarget(new BrowserDropTarget(
                ViewModel.CurrentFolderId, BrowserPane.Main, ViewModel.CurrentFolderDisplayName, mode));
            e.Effects = EffectFor(mode);
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>拖拽离开列表卡：熄灭落点提示（覆盖式状态，离开清零）。</summary>
    private void ListCard_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    /// <summary>
    /// 落在列表空白 = 落进**当前所在文件夹**（Windows 口径）。按住 Ctrl 时就是"在本页做一个副本"：
    /// 文件夹副本由引擎按同层唯一命名规范编号（「名 (2)」）；移动模式下已在目标目录的项由传输流水线
    /// 自动记为"已在目标位置"（无操作，非错误），与 Explorer 一致。
    ///
    /// <para>"把当前文件夹拖到它自己的空白上"这类成环也走同一条路：**只记意图**，
    /// 由传输流水线在拖拽结束之后统一拒绝并弹规范弹窗（与粘贴同一个弹窗）。</para>
    /// </summary>
    private void ListCard_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            if (payload != null && ViewModel != null)
                _pendingDrop = (payload.Items, ViewModel.CurrentFolderId, ViewModel.DropTargetMode);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // —— 树节点拖拽源/拖放/选中（FolderTreePanel 事件转发）——

    /// <summary>
    /// 树节点**拖拽源**（左键与右键都由面板上报，据 <c>e.RightButton</c> 分派）：载荷与选中语义完全复用主栏那一套
    /// （VM <see cref="BrowserViewModel.PrepareDragFromNode"/>）——拖未选中节点先单选该节点、拖已选中节点拖动整个选中集合
    /// （树选中同样落在唯一选中集合里）。「全部书签」虚根不是实体 → 载荷为空 → 不发起拖拽。
    /// 起手式与收尾与主栏**同一个入口** <see cref="StartDrag"/>（浮层 / OLE 循环 / 右键菜单 / 成环判定都不各写一份）。
    /// </summary>
    private void FolderTreePanel_NodeDragStartRequested(object? sender, TreeItemDragStartEventArgs e)
    {
        if (ViewModel == null || e.Node is not FolderNode node || e.Source == null) return;
        var items = ViewModel.PrepareDragFromNode(node);
        if (items.Count == 0) return;
        StartDrag(e.Source, items, e.RightButton);
    }

    /// <summary>树节点拖拽经过：命中节点是真实文件夹才作落点（链接叶子不是移动目标——**拖到书签上什么也不发生**）。
    /// 与主栏同口径：合法 = 光标 + 落点高亮 + 「移动到/复制到 X」提示；**不判成环**（成环由执行层统一拒绝并弹窗）。
    /// 「全部书签」虚根 FolderId 为 null = 根目录，是合法落点（移到/复制到根）。</summary>
    private void FolderTreePanel_NodeDragOver(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var node = e.Node as FolderNode;
            var ok = IsDropPositionCandidate(node);
            var mode = CurrentDropMode();
            ApplyDropTarget(ok ? new BrowserDropTarget(node!.FolderId, BrowserPane.Tree, node.Name, mode) : null);
            e.Args.Effects = ok ? EffectFor(mode) : DragDropEffects.None;
        }
        finally
        {
            e.Args.Handled = true;
        }
    }

    /// <summary>拖拽离开树节点：熄灭落点高亮（覆盖式状态，离开清零；新落点由随后的 DragOver 覆盖写入）。</summary>
    private void FolderTreePanel_NodeDragLeave(object? sender, TreeItemDragEventArgs e) => ClearDropTarget();

    /// <summary>落在树节点：**只记意图不执行**（执行在 <see cref="StartDrag"/> 里、拖拽循环退出之后）。
    /// 模式取自落点状态（Ctrl = 复制），与提示条说的是同一个值。</summary>
    private void FolderTreePanel_NodeDrop(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var payload = e.Args.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var node = e.Node as FolderNode;
            if (payload != null && IsDropPositionCandidate(node))
                _pendingDrop = (payload.Items, node!.FolderId, ViewModel?.DropTargetMode ?? TransferMode.Move);
        }
        finally
        {
            e.Args.Handled = true;
        }
    }

    /// <summary>点击树节点行主体统一交给 VM（数据驱动选中）：
    /// 文件夹 → 选中并进入；链接叶子 → 主区定位选中该行；「全部书签」虚拟根 → 进入根目录（不写选中）。
    /// 树高亮由 FolderNode.IsSelected 从 VM 唯一选中集合派生，无需在此记录目标或操作容器。</summary>
    private void FolderTreePanel_NodeSelected(object? sender, object? node)
    {
        if (ViewModel == null || node is not FolderNode fn) return;
        ViewModel.ActivatePane(BrowserPane.Tree);   // 点左栏 = 该栏获得键盘语义归属（焦点随之收进页面）
        _ = ViewModel.SelectTreeNodeAsync(fn);
    }

    /// <summary>点击文件夹树空白：清空选中（主栏 + 树一起取消，唯一事实来源清空）。</summary>
    private void FolderTreePanel_BackgroundClicked(object? sender, EventArgs e)
    {
        ViewModel?.ActivatePane(BrowserPane.Tree);
        if (ViewModel != null) ViewModel.ClearSelection();
    }

    /// <summary>VM 属性变化：仅路径编辑态需要视图介入（聚焦全选）；树选中同步已内聚在 VM 数据驱动。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.IsPathEditing))
        {
            if (ViewModel is { IsPathEditing: true })
            {
                // 编辑框在 BreadcrumbBar 模板内（Collapsd↔Visible 切换由控件触发器负责），找到后聚焦全选
                var editBox = FindDescendant<TextBox>(Breadcrumb);
                editBox?.Focus();
                editBox?.SelectAll();
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var sub = FindDescendant<T>(child);
            if (sub != null) return sub;
        }
        return null;
    }

    // 卡内按下的手势凭据（见 ListCard_MouseLeftButtonUp 的归属校验）。
    private bool _cardPressEmpty;
    private int _cardPressClickCount;

    /// <summary>卡内按下（隧道先于行）：记录"按下是否落在非行区域"+ 点击计数。</summary>
    private void ListCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender))?.VisualHit;
        _cardPressEmpty = hit == null || !IsBrowserRow(hit);
        _cardPressClickCount = e.ClickCount;
    }

    /// <summary>
    /// 点击列表卡空白处清除选中（命中行内元素时不处理，由行命令负责）。
    /// 语义边界 = 列表卡本身：处理器挂在卡面上，不挂内容区外层 Grid——
    /// 外层 Grid 同时包含目录树面板，树行点击（选中/进入）会冒泡到外层并被当成"空白"清掉
    /// （实测缺陷：点树里的链接叶子/文件夹，两栏都不显示选中）。清选中归各区域自己：
    /// 树面板内部自管（TreeBackgroundClicked），列表卡在此自管，详情栏/分隔线不在本卡内、天然不受影响。
    /// **归属校验**：只有"按下也在卡内非行区域"的单击才算点空白——
    /// 双击打开文件夹时列表会重建，第二击的抬起可能落在新列表空白处（按下却在旧列表行上），
    /// 那种抬起绝不能当作"点空白清选中"（否则刚打开目录就把选中清没了）。
    /// </summary>
    private void ListCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.ActivatePane(BrowserPane.Main);
        if (!_cardPressEmpty || _cardPressClickCount > 1) return;

        var hitTest = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender));
        if (hitTest?.VisualHit == null || IsBrowserRow(hitTest.VisualHit))
            return;

        ViewModel?.ClearSelection();
    }

    private static bool IsBrowserRow(DependencyObject element)
    {
        while (element != null)
        {
            if (element is Border border && "BrowserRow".Equals(border.Tag as string))
                return true;
            if (element is Visual)
                element = VisualTreeHelper.GetParent(element);
            else
                break;
        }
        return false;
    }
}

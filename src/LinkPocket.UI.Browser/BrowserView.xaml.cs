using System;
using System.ComponentModel;
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

            ViewModel.Prompt ??= (title, defaultValue) => InputDialog.Show(title, defaultValue);   // 实例注入：无头/多窗口下不与其它页共享
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
        ViewModel.SelectRowWithModifiers(row, mods);
    }

    /// <summary>
    /// 右键命中的行未选中时，先按 Explorer 语义改为单选该行；
    /// 无论是否改选中，都要把命中行告知 VM —— 删除文案要按"这一次会删掉什么"算
    /// （命中文件夹显示其内链接数，命中多选中的行显示选中项数）。
    /// </summary>
    private void RowBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
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

    /// <summary>拖拽数据：选中集合（拖未选中的行时为其临时单项集合）。</summary>
    public record BrowserDragPayload(IReadOnlyList<BrowserRowViewModel> Rows);

    /// <summary>按下：记下手势凭据；无修饰键按未选中行 = 立即单选（Windows 按下即反馈）。</summary>
    private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowDragStart = e.GetPosition(this);
        _dragStarted = false;
        _pressRow = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        _pressModifiers = Keyboard.Modifiers;
        _pressClickCount = e.ClickCount;

        if (ViewModel == null || _pressRow == null) return;
        ViewModel.ActivatePane(BrowserPane.Main);   // 点主栏 = 该栏获得键盘语义归属（焦点随之收进页面）
        if (_pressModifiers == ModifierKeys.None && !_pressRow.IsSelected)
            ViewModel.SelectRowWithModifiers(_pressRow, ModifierKeys.None);
    }

    private void RowBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _rowDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _rowDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStarted = true;

        if ((sender as FrameworkElement)?.DataContext is not BrowserRowViewModel row || ViewModel == null) return;

        // 拖未选中的行 → 先单选该行（Explorer 语义）；拖已选中的行 → 拖动整个选中集合
        if (!row.IsSelected)
            ViewModel.SelectRowWithModifiers(row, ModifierKeys.None);
        var items = ViewModel.SelectedRows.ToList();
        if (items.Count == 0) return;

        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(new BrowserDragPayload(items)),
            DragDropEffects.Move);
    }

    /// <summary>目标合法性：目标行/节点不在拖动集合内，且没有任何被拖文件夹包含目标（防环）。</summary>
    private bool IsDropValid(BrowserDragPayload? payload, string? targetFolderId)
    {
        if (payload == null || ViewModel == null) return false;
        // 根节点「全部书签」（folderId == null）= 移到根目录，是合法目标（与 NodeDrop 注释一致）；
        // 防环只在目标是真实文件夹时才有意义（根没有「被移入自身」的概念）。
        foreach (var item in payload.Rows)
        {
            if (item.Id == targetFolderId) return false;
            if (item.IsFolder && targetFolderId != null && ViewModel.IsSelfOrDescendant(item.Id, targetFolderId)) return false;
        }
        return true;
    }

    private void RowBorder_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;

            var ok = row is { IsFolder: true } && IsDropValid(payload, row.Id);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        }
        finally
        {
            e.Handled = true;   // 无论沿途是否异常，本事件归属拖拽流程（防冒泡到其它落点）
        }
    }

    private void RowBorder_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
            if (payload != null && row is { IsFolder: true } && IsDropValid(payload, row.Id))
                _ = ViewModel?.MoveItemsAsync(payload.Rows.Select(r => (r.Id, r.IsFolder)), row.Id);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // —— 树节点拖放/选中（FolderTreePanel 事件转发）——

    /// <summary>树节点拖拽经过：命中节点是真实文件夹且不在拖动集合内（防环）才接受；链接叶子不是移动目标。</summary>
    private void FolderTreePanel_NodeDragOver(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var payload = e.Args.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var node = e.Node as FolderNode;
            var ok = node != null && !node.IsLink && IsDropValid(payload, node.FolderId);
            e.Args.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        }
        finally
        {
            e.Args.Handled = true;
        }
    }

    /// <summary>树节点落放：移入对应文件夹（根节点「全部书签」= 移到根）；链接叶子不接受落放。</summary>
    private void FolderTreePanel_NodeDrop(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var payload = e.Args.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var node = e.Node as FolderNode;
            if (payload != null && node != null && !node.IsLink && IsDropValid(payload, node.FolderId))
                _ = ViewModel?.MoveItemsAsync(payload.Rows.Select(r => (r.Id, r.IsFolder)), node.FolderId);
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

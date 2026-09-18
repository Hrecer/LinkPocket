using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
            }
            _rowsHook?.Detach();
            _rowsHook = null;
            _wiredVm = ViewModel;

            ViewModel.Prompt ??= (title, defaultValue) => InputDialog.Show(title, defaultValue);   // 实例注入：无头/多窗口下不与其它页共享
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.FocusRowRequested += OnFocusRowRequested;
            HookRowsCollection(ViewModel);
            WireMainTableOnce();
        };
    }

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

    // —— 行错峰入场（MD3E）：目录装载/刷新后淡入 + 轻微上移，弹簧曲线 ——
    private ObservableCollectionHook? _rowsHook;

    private void HookRowsCollection(BrowserViewModel? vm)
    {
        _rowsHook?.Detach();
        _rowsHook = null;
        if (vm == null) return;
        _rowsHook = new ObservableCollectionHook(vm.Rows, QueueRowEntrance);
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

    /// <summary>订阅行集合变更的轻量钩子（DataContext 换绑时自动迁移/解除）。
    /// 刷新重建列表 = 一次 Reset + N 次 Add，若每次变更都直接入队动画回调，单次刷新会积压
    /// 数十次同帧 Dispatcher 回调；这里聚合为"同帧只入队一次"。</summary>
    private sealed class ObservableCollectionHook
    {
        private readonly INotifyCollectionChanged _source;
        private readonly Action _onChange;
        private bool _queued;

        public ObservableCollectionHook(INotifyCollectionChanged source, Action onChange)
        {
            _source = source;
            _onChange = onChange;
            _source.CollectionChanged += OnChanged;
        }

        private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Reset && e.Action != NotifyCollectionChangedAction.Add)
                return;
            if (_queued) return;   // 同帧内已在等待队列：合并后续变更为一次回调
            _queued = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { _queued = false; _onChange(); }));
        }

        public void Detach()
        {
            _source.CollectionChanged -= OnChanged;
            _queued = false;
        }
    }

    // （列头与列宽拖拽已由共享数据表控件 SortableDataTable 内部驱动：
    //   表头按 Columns 生成，拖拽只改控件的 ColumnWidths 单一数据源。）

    // —— 行点击路由（读修饰键：无=单选，Ctrl=翻转，Shift=区间）——

    private void RowBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserRowViewModel row && ViewModel != null)
            ViewModel.SelectRowWithModifiers(row, Keyboard.Modifiers);
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

    /// <summary>拖拽数据：选中集合（拖未选中的行时为其临时单项集合）。</summary>
    public record BrowserDragPayload(IReadOnlyList<BrowserRowViewModel> Rows);

    private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _rowDragStart = e.GetPosition(this);

    private void RowBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _rowDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _rowDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

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

    /// <summary>点击树节点统一交给 VM（数据驱动选中）：文件夹/根 → 导航进目录；根级链接叶子 → 主区定位选中该行。
    /// 树高亮由 FolderNode.IsSelected 数据回写并经 VM.ApplyTreeSelection 重放，无需在此记录目标或操作容器。</summary>
    private void FolderTreePanel_NodeSelected(object? sender, object? node)
    {
        if (ViewModel == null || node is not FolderNode fn) return;
        ViewModel.SelectTreeNode(fn);
    }

    /// <summary>点击文件夹树空白：清空选中（主栏 + 树一起取消，唯一事实来源清空）。</summary>
    private void FolderTreePanel_BackgroundClicked(object? sender, EventArgs e)
    {
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

    /// <summary>点击空白处清除选中（命中行内元素时不处理，由行命令负责）。</summary>
    private void ContentArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var hitTest = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender));
        if (hitTest?.VisualHit == null || IsBrowserRow(hitTest.VisualHit))
            return;

        // 点击右侧详情栏 / 分隔线不清除选中（侧栏内可能有需要保持选中的交互与拖拽）
        if (IsDescendantOf(hitTest.VisualHit, DetailsPanel) || IsDescendantOf(hitTest.VisualHit, DetailSplitter))
            return;

        ViewModel?.ClearSelection();
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        while (element != null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
                element = VisualTreeHelper.GetParent(element);
            else
                element = System.Windows.LogicalTreeHelper.GetParent(element);
        }
        return false;
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

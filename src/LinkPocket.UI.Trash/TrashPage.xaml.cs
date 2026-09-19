using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views
{
    /// <summary>
    /// 回收站页（与浏览页同构的"回收站浏览器"）：导航行（导航键 + 面包屑地址栏 + 永久删除）
    /// + 左栏导航树（含链接叶子）+ 中栏共享数据表（模板行 + 同一套行皮肤）+ 右栏只读详情栏。
    /// 交互机械与浏览页同源（唯一选中集合 / 覆盖式落点 / 拖拽浮层 / 输入子系统），语义只读：
    /// 无还原、无撤销/重做、无编辑、无剪贴板；站内搬移只经拖拽（trash.move）。
    /// </summary>
    public partial class TrashPage : UserControl
    {
        public TrashPage()
        {
            InitializeComponent();
            Focusable = true;

            DataContextChanged += (_, _) =>
            {
                if (ViewModel == null) return;
                if (ReferenceEquals(_wiredVm, ViewModel)) return;

                if (_wiredVm != null)
                {
                    _wiredVm.PaneActivated -= OnPaneActivated;
                    _wiredVm.RefreshCompleted -= OnRefreshCompleted;
                    _wiredVm.ContextMenuRequested -= OnContextMenuRequested;
                    _wiredVm.FocusRowRequested -= OnFocusRowRequested;
                    _wiredVm.ShowLinkDetailRequested -= OnShowLinkDetailRequested;
                }
                _wiredVm = ViewModel;

                ViewModel.PaneActivated += OnPaneActivated;
                ViewModel.RefreshCompleted += OnRefreshCompleted;
                ViewModel.ContextMenuRequested += OnContextMenuRequested;
                ViewModel.FocusRowRequested += OnFocusRowRequested;
                ViewModel.ShowLinkDetailRequested += OnShowLinkDetailRequested;
                WireTrashTable();

                _shortcutHost?.Detach();
                _shortcutHost = new ShortcutHost(TrashShortcuts.CreateRegistry(ViewModel), () => ActiveScope);
                _shortcutHost.Attach(this);
            };

            // 切到本页（全局导航切页）→ 键盘焦点收进本页（快捷键按焦点路由）
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    FocusPage();
                    UpdateSidebar();
                }
            };

            TrashSidebar.DataContext = _sidebar;
            TrashTable.RowClick += (_, item) =>
            {
                if (ViewModel is { } vm && item is TrashRowViewModel row)
                {
                    vm.SetSelection(new[] { row.Id });   // 单击 = 单选（与浏览页非修饰键口径一致）
                    vm.SetContextRow(row);
                }
            };
            TrashTable.RowDoubleClick += (_, item) =>
            {
                if (item is TrashRowViewModel row) ViewModel?.OpenRowCommand.Execute(row);
            };
        }

        /// <summary>Shell 端口：切到回收站页时装载（导航加载口径——亮遮罩 + 行入场动画，并同步只读详情栏）。</summary>
        public async Task RefreshAsync()
        {
            if (ViewModel is { } vm) await vm.LoadAsync(navigating: true);
            UpdateSidebar();
        }

        private TrashViewModel? _wiredVm;
        private ShortcutHost? _shortcutHost;

        private TrashViewModel? ViewModel => DataContext as TrashViewModel;

        private ShortcutScope ActiveScope => ViewModel?.ActivePane == TrashPane.Tree
            ? ShortcutScope.TrashTree
            : ShortcutScope.TrashMain;

        // ================= 焦点不变式（与浏览页同口径） =================

        private void FocusPage()
        {
            if (IsLoaded && IsVisible) Keyboard.Focus(this);
        }

        /// <summary>页面可见且应用在前台时，键盘焦点必须在页内（刷新重建会把焦点交给窗口 → 快捷键静默失效）。</summary>
        private void EnsurePageFocus()
        {
            if (!IsLoaded || !IsVisible) return;
            if (Window.GetWindow(this)?.IsActive != true) return;
            if (IsFocusWithinPage()) return;
            if (ShortcutHost.IsTextInputFocused()) return;   // 地址栏编辑中绝不抢
            Keyboard.Focus(this);
        }

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

        private void OnPaneActivated(object? sender, TrashPane pane) => FocusPage();

        /// <summary>刷新链结束：导航加载才播行入场动画；并守住"焦点在页内"不变式。</summary>
        private void OnRefreshCompleted(object? sender, bool wasNavigation)
        {
            if (wasNavigation) QueueRowEntrance();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                EnsurePageFocus();
                UpdateSidebar();
                // 详情页展示的条目若已被永久删除 → 关闭覆盖层，不留残留旧数据
                if (LinkDetailOverlay.Visibility == Visibility.Visible && _detailRow != null && ViewModel is { } vm)
                {
                    if (!vm.AllLinks.Any(l => l.Id == _detailRow.Id) && !vm.Tree.Any(n => n.Id == _detailRow.Id))
                        CloseLinkDetail();
                }
            }));
        }

        // ================= 主栏：表装配 / 选中 / 滚动 =================

        private bool _tableWired;

        private void WireTrashTable()
        {
            if (_tableWired || ViewModel == null) return;
            TrashTable.SortField = ViewModel.SortField;
            TrashTable.SortAscending = ViewModel.SortAscending;
            _tableWired = true;
            TrashTable.Columns = new[]
            {
                new DataTableColumn { Field = "name", Label = "名称", Width = -1 },
                new DataTableColumn { Field = "type", Label = "类型", Width = 90 },
                new DataTableColumn { Field = "origin_path", Label = "原位置", Width = -2 },
                new DataTableColumn { Field = "deleted_at", Label = "删除时间", Width = 150 },
            };
            TrashTable.SortChanged += (_, e) => ViewModel?.ApplySort(e.Field, e.Ascending);
        }

        private void OnFocusRowRequested(object? sender, TrashRowViewModel row) => ScrollRowIntoView(row);

        private void ScrollRowIntoView(TrashRowViewModel row)
        {
            var list = TrashTable.RowsList;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement realized)
                {
                    realized.BringIntoView();
                    return;
                }

                var scroller = FindAncestorScrollViewer(list);
                var index = ViewModel?.Rows.IndexOf(row) ?? -1;
                if (scroller == null || index < 0) return;
                var rowHeight = EstimateRowHeight(list);
                scroller.ScrollToVerticalOffset(Math.Max(0, index * rowHeight - scroller.ViewportHeight / 3));
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

        private static double EstimateRowHeight(ItemsControl list)
            => list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 1
                ? first.ActualHeight
                : 36;

        // ================= 行错峰入场（只在导航加载时；与浏览页同口径） =================

        private void QueueRowEntrance()
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var rows = TrashTable.RowsList;
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

        // ================= 行手势（与浏览页同一套归属校验） =================

        private Point _rowDragStart;
        private TrashRowViewModel? _pressRow;
        private ModifierKeys _pressModifiers;
        private int _pressClickCount;
        private bool _dragStarted;

        private Point _rowRightDragStart;
        private TrashRowViewModel? _rightPressRow;
        private bool _rightDragGesture;
        private ContextMenu? _rightDragMenu;

        /// <summary>拖拽松手时的待执行意图（Drop 只记，不执行——执行在 OLE 循环退出之后）。</summary>
        private (IReadOnlyList<DragItem> Items, string? TargetId)? _pendingDrop;

        private bool _dragCancelledByEscape;

        private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _rowDragStart = e.GetPosition(this);
            _dragStarted = false;
            _pressRow = null;
            _pressModifiers = Keyboard.Modifiers;
            _pressClickCount = e.ClickCount;

            _pressRow = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
            if (ViewModel == null || _pressRow == null) return;
            ViewModel.ActivatePane(TrashPane.Main);
            if (_pressModifiers == ModifierKeys.None && !ViewModel.IsSelectedId(_pressRow.Id))
                ViewModel.SetSelection(new[] { _pressRow.Id });   // 按下即反馈（与浏览页口径一致）
        }

        private void RowBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _rowRightDragStart = e.GetPosition(this);
            _rightPressRow = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
            _rightDragGesture = false;
            _rightDragMenu = null;
        }

        private void RowBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.RightButton == MouseButtonState.Pressed)
            {
                if (_rightPressRow == null) return;
                if (!DragSupport.BeyondThreshold(e.GetPosition(this), _rowRightDragStart)) return;
                var rightRow = _rightPressRow;
                _rightPressRow = null;
                StartRowDrag((DependencyObject)sender, rightRow, rightButton: true);
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_pressRow == null) return;
            if (!DragSupport.BeyondThreshold(e.GetPosition(this), _rowDragStart)) return;
            _dragStarted = true;
            StartRowDrag((DependencyObject)sender, _pressRow, rightButton: false);
        }

        /// <summary>抬起 = 点击完成：与按下同一次手势才重放选择语义（双击第二击不承载选择——与浏览页同守卫）。</summary>
        private void RowBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
            var pressed = _pressRow;
            var mods = _pressModifiers;
            var clicks = _pressClickCount;
            _pressRow = null;

            if (ViewModel == null || row == null) return;
            if (!ReferenceEquals(row, pressed) || _dragStarted || clicks > 1) return;
            ViewModel.SelectRowWithModifiers(row, mods);
            ViewModel.SetContextRow(row);
        }

        private void RowBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_rightDragGesture)
            {
                e.Handled = true;   // 右键拖拽手势期间不弹行菜单（本次手势产物是我们自己的菜单）
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is not TrashRowViewModel row || ViewModel == null) return;
            if (!ViewModel.IsSelectedId(row.Id)) ViewModel.SetSelection(new[] { row.Id });
            ViewModel.SetContextRow(row);
        }

        // ================= 列表卡：空白点击清选中 + 空白落点（= 当前所在单元） =================

        private bool _cardPressEmpty;
        private int _cardPressClickCount;

        private void ListCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender))?.VisualHit;
            _cardPressEmpty = hit == null || !IsTrashRow(hit);
            _cardPressClickCount = e.ClickCount;
        }

        private void ListCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ViewModel?.ActivatePane(TrashPane.Main);
            if (!_cardPressEmpty || _cardPressClickCount > 1) return;
            var hitTest = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender));
            if (hitTest?.VisualHit == null || IsTrashRow(hitTest.VisualHit)) return;
            ViewModel?.ClearSelection();
        }

        private static bool IsTrashRow(DependencyObject element)
        {
            while (element != null)
            {
                if (element is Border border && "TrashRow".Equals(border.Tag as string)) return true;
                if (element is Visual) element = VisualTreeHelper.GetParent(element);
                else break;
            }
            return false;
        }

        // ================= 拖拽（源 = 行 + 树节点；落点 = 单元行 / 树节点 / 面包屑段 / 列表空白） =================

        private DragVisualAdorner? _dragVisual;

        private void ShowDragVisual(IReadOnlyList<DragItem> items)
        {
            _dragVisual = DragVisualAdorner.Attach(this);
            _dragCancelledByEscape = false;
            if (_dragVisual == null) return;
            _dragVisual.Show(items);
            _dragVisual.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
            AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnGiveFeedback));
            AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
        }

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
            _dragVisual.UpdatePosition(PointFromScreen(new Point(screen.X, screen.Y)));
        }

        /// <summary>拖拽中记录 Esc 取消（右键拖拽收尾据此不弹菜单；左键取消则结构性无意图）。</summary>
        private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed) _dragCancelledByEscape = true;
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
        private void ApplyDropTarget(TrashDropTarget? target)
        {
            ViewModel?.SetDropTarget(target);
            _dragVisual?.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
        }

        private void ClearDropTarget()
        {
            ViewModel?.ClearDropTarget();
            _dragVisual?.UpdateHint(string.Empty);
            Breadcrumb.SetDropHighlight(null);
        }

        private void StartRowDrag(DependencyObject source, TrashRowViewModel row, bool rightButton)
        {
            if (ViewModel == null) return;
            var items = ViewModel.PrepareDragFromRow(row);
            if (items.Count == 0) return;
            StartDrag(source, items, rightButton);
        }

        /// <summary>一次拖拽的完整生命周期（行 / 树节点共用）：浮层 → OLE 循环 → 收尾。
        /// 回收站**只允许移动**（不接 Ctrl 复制）：允许效果只有 Move。</summary>
        private void StartDrag(DependencyObject source, IReadOnlyList<DragItem> items, bool rightButton)
        {
            _rightDragGesture = rightButton;
            _pendingDrop = null;
            ShowDragVisual(items);
            DragDrop.DoDragDrop(source, new DataObject(new TrashDragPayload(items)), DragDropEffects.Move);
            HideDragVisual();

            var target = ViewModel?.DropTarget;
            var drop = _pendingDrop;
            _pendingDrop = null;
            ClearDropTarget();

            if (rightButton)
            {
                if (_dragCancelledByEscape)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
                    return;
                }
                ShowRightDragDropMenu(items, target);   // 松手不搬东西：用户选了才执行
                return;
            }

            if (drop is { } pending)
                _ = ViewModel?.DropItemsAsync(pending.Items, pending.TargetId);
        }

        /// <summary>右键拖拽松手菜单（回收站版）：只「移动到「X」」+ 取消（无复制——站内搬移不产生副本）。</summary>
        private void ShowRightDragDropMenu(IReadOnlyList<DragItem> items, TrashDropTarget? target)
        {
            if (ViewModel == null || target == null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
                return;
            }

            var menu = new ContextMenu { DataContext = ViewModel };
            menu.Resources.Add(typeof(MenuItem), (Style)FindResource("LpMenuItem"));

            var move = new MenuItem { Header = $"移动到「{target.Name}」" };
            move.Click += (_, _) => _ = ViewModel.DropItemsAsync(items, target.UnitId);
            menu.Items.Add(move);

            var cancel = new MenuItem { Header = "取消" };
            cancel.Click += (_, _) => menu.IsOpen = false;
            menu.Items.Add(cancel);

            menu.Closed += (_, _) =>
            {
                ClearDropTarget();
                _rightDragGesture = false;
                _rightDragMenu = null;
            };

            _rightDragMenu = menu;
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>右键拖拽菜单里的动作项（纯构造，探针据此直调断言内容）。</summary>
        private List<MenuItem> BuildRightDragMenuItems(IReadOnlyList<DragItem> items, string? targetUnitId, string targetName)
        {
            var move = new MenuItem { Header = $"移动到「{targetName}」" };
            move.Click += (_, _) => _ = ViewModel?.DropItemsAsync(items, targetUnitId);
            return new List<MenuItem> { move };
        }

        /// <summary>拖动中的载荷（与浏览页 browser 载荷同形；类型不同即可区分来源）。</summary>
        public record TrashDragPayload(IReadOnlyList<DragItem> Items);

        /// <summary>落点候选（唯一判定）：主栏**单元行** / 树**单元节点**（链接叶子与虚根不是落点）。</summary>
        private static bool IsDropPositionCandidate(object? dataContext)
            => dataContext is TrashRowViewModel { IsFolder: true } or TrashNode { IsLink: false, IsRoot: false };

        private void RowBorder_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                var row = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
                var ok = IsDropPositionCandidate(row);
                ApplyDropTarget(ok ? new TrashDropTarget(row!.Id, TrashPane.Main, row.Name) : null);
                e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            }
            finally
            {
                e.Handled = true;
            }
        }

        private void RowBorder_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

        private void RowBorder_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var payload = e.Data.GetData(typeof(TrashDragPayload)) as TrashDragPayload;
                var row = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
                if (payload != null && IsDropPositionCandidate(row))
                    _pendingDrop = (payload.Items, row!.Id);
            }
            finally
            {
                e.Handled = true;
            }
        }

        private void ListCard_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                var payload = e.Data.GetData(typeof(TrashDragPayload)) as TrashDragPayload;
                if (payload == null || ViewModel == null) return;
                ApplyDropTarget(new TrashDropTarget(ViewModel.CurrentUnitId, TrashPane.Main, ViewModel.CurrentUnitDisplayName));
                e.Effects = DragDropEffects.Move;
            }
            finally
            {
                e.Handled = true;
            }
        }

        private void ListCard_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

        private void ListCard_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var payload = e.Data.GetData(typeof(TrashDragPayload)) as TrashDragPayload;
                if (payload != null && ViewModel != null)
                    _pendingDrop = (payload.Items, ViewModel.CurrentUnitId);
            }
            finally
            {
                e.Handled = true;
            }
        }

        // ================= 树（FolderTreePanel 事件转发） =================

        private void FolderTreePanel_NodeSelected(object? sender, object? node)
        {
            if (ViewModel == null || node is not TrashNode n) return;
            ViewModel.ActivatePane(TrashPane.Tree);
            _ = ViewModel.SelectTreeNodeAsync(n);
        }

        private void FolderTreePanel_BackgroundClicked(object? sender, EventArgs e)
        {
            ViewModel?.ActivatePane(TrashPane.Tree);
            ViewModel?.ClearSelection();
        }

        private void FolderTreePanel_NodeDragStartRequested(object? sender, TreeItemDragStartEventArgs e)
        {
            if (ViewModel == null || e.Node is not TrashNode node || e.Source == null) return;
            var items = ViewModel.PrepareDragFromNode(node);
            if (items.Count == 0) return;
            StartDrag(e.Source, items, e.RightButton);
        }

        private void FolderTreePanel_NodeDragOver(object? sender, TreeItemDragEventArgs e)
        {
            try
            {
                var node = e.Node as TrashNode;
                var ok = IsDropPositionCandidate(node);
                ApplyDropTarget(ok ? new TrashDropTarget(node!.Id, TrashPane.Tree, node.Name) : null);
                e.Args.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        private void FolderTreePanel_NodeDragLeave(object? sender, TreeItemDragEventArgs e) => ClearDropTarget();

        private void FolderTreePanel_NodeDrop(object? sender, TreeItemDragEventArgs e)
        {
            try
            {
                var payload = e.Args.Data.GetData(typeof(TrashDragPayload)) as TrashDragPayload;
                var node = e.Node as TrashNode;
                if (payload != null && IsDropPositionCandidate(node))
                    _pendingDrop = (payload.Items, node!.Id);
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        // ================= 面包屑（编辑态 + 第三落点区） =================

        private void Breadcrumb_EditRequested(object? sender, EventArgs e)
            => ViewModel?.EnterPathEditCommand.Execute(null);

        private void Breadcrumb_EditFocusLost(object? sender, EventArgs e)
        {
            if (ViewModel is { IsPathEditing: true })
                ViewModel.CancelPathEditCommand.Execute(null);
        }

        private void Breadcrumb_EditPopupClosed(object? sender, EventArgs e)
        {
            if (ViewModel is { IsPathEditing: true })
                ViewModel.CancelPathEditCommand.Execute(null);
        }

        private void Breadcrumb_CandidateMoveRequested(object? sender, CandidateMoveEventArgs e)
            => ViewModel?.MoveCandidate(e.Delta);

        private void Breadcrumb_CandidateChosen(object? sender, string? name)
        {
            if (!string.IsNullOrEmpty(name)) ViewModel?.ChooseCandidate(name);
        }

        private void Breadcrumb_CrumbDragOver(object? sender, CrumbDragEventArgs e)
        {
            try
            {
                if (ViewModel == null) return;
                var (id, name) = CrumbTargetOf(e.Segment);
                var ok = id != null || e.Segment is TrashCrumbViewModel { UnitId: null };   // 根段 = 回收站根（合法落点）
                ApplyDropTarget(ok ? new TrashDropTarget(id, TrashPane.Breadcrumb, name) : null);
                Breadcrumb.SetDropHighlight(e.Segment);
                e.Args.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        private void Breadcrumb_CrumbDragLeave(object? sender, CrumbDragEventArgs e) => ClearDropTarget();

        private void Breadcrumb_CrumbDrop(object? sender, CrumbDragEventArgs e)
        {
            try
            {
                var payload = e.Args.Data.GetData(typeof(TrashDragPayload)) as TrashDragPayload;
                if (payload == null || ViewModel == null) return;
                var (id, _) = CrumbTargetOf(e.Segment);
                _pendingDrop = (payload.Items, id);
            }
            finally
            {
                e.Args.Handled = true;
            }
        }

        private static (string? Id, string Name) CrumbTargetOf(object? segment)
            => segment is TrashCrumbViewModel c ? (c.UnitId, c.Name) : (null, string.Empty);

        // ================= Shift+F10 / 菜单键：当前选中行的右键菜单 =================

        private void OnContextMenuRequested(object? sender, EventArgs e)
        {
            var row = ViewModel?.SelectedRows.FirstOrDefault();
            if (row == null) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (TrashTable.RowsList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return;
                var border = FindTaggedBorder(container);
                if (border?.ContextMenu is not { } menu) return;
                menu.PlacementTarget = border;
                menu.IsOpen = true;
            }));
        }

        private static Border? FindTaggedBorder(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border b && "TrashRow".Equals(b.Tag as string)) return b;
                if (FindTaggedBorder(child) is { } hit) return hit;
            }
            return null;
        }

        // ================= 只读详情页（链接） =================

        private TrashRowViewModel? _detailRow;

        private void OnShowLinkDetailRequested(object? sender, TrashRowViewModel row)
        {
            _detailRow = row;
            DetailName.Text = row.Name;
            DetailUrl.Text = row.Url ?? string.Empty;
            DetailOrigin.Text = row.OriginText;
            DetailDeletedAt.Text = row.DeletedText;
            DetailId.Text = row.Id;

            var favicon = FaviconService.LoadFromCache(row.FaviconUrl);
            if (favicon == null && !string.IsNullOrWhiteSpace(row.FaviconUrl))
            {
                var faviconUrl = row.FaviconUrl;
                _ = Task.Run(async () =>
                {
                    try { await FaviconStore.EnsureCachedAsync(faviconUrl); } catch { }
                    return FaviconService.LoadFromCache(faviconUrl);
                }).ContinueWith(t => Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_detailRow?.Id != row.Id) return;   // 期间已切到别的条目
                    var bmp = t.Result;
                    DetailFavicon.Source = bmp;
                    DetailFavicon.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
                    DetailFaviconFallback.Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed;
                }), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            else
            {
                DetailFavicon.Source = favicon;
                DetailFavicon.Visibility = favicon != null ? Visibility.Visible : Visibility.Collapsed;
                DetailFaviconFallback.Visibility = favicon == null ? Visibility.Visible : Visibility.Collapsed;
            }

            LinkDetailOverlay.Visibility = Visibility.Visible;
        }

        private void CloseLinkDetail()
        {
            LinkDetailOverlay.Visibility = Visibility.Collapsed;
            _detailRow = null;
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e) => CloseLinkDetail();

        private void DetailCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_detailRow?.Url)) Clipboard.SetText(_detailRow.Url);
            }
            catch { /* 剪贴板被占用时不阻断 */ }
        }

        // ================= 右键侧栏 / 永久删除按钮 =================

        private readonly TrashSidebarModel _sidebar = new();

        private void UpdateSidebar()
        {
            var vm = ViewModel;
            if (vm == null)
            {
                _sidebar.Clear();
                return;
            }

            var selected = vm.SelectedRows.ToList();
            if (selected.Count == 0) _sidebar.Clear();
            else if (selected.Count == 1) _sidebar.Show(selected[0]);
            else _sidebar.ShowMulti(selected.Count);
        }

        private void TrashPurge_Click(object sender, RoutedEventArgs e)
            => ViewModel?.PurgeSelectionCommand.Execute(null);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 交互机械与浏览页同源（唯一选中集合 / 输入子系统），语义只读：
    /// 无撤销/重做、无编辑、无剪贴板；**站内不可搬移**（条目只能被打开查看或永久删除）。
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
                _shortcutHost = new ShortcutHost(
                    ShortcutCatalog.Build(ShortcutPage.Trash, BuildShortcutCommands(ViewModel)), () => ActiveScope);
                _shortcutHost.Attach(this);
            };

            // 切到本页（全局导航切页）→ 键盘焦点收进本页（快捷键按焦点路由）
            // 右栏详情栏不在此刷新：它绑定 VM 的 Details（选中集合的投影），选中一变即自动更新。
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) PageFocus.Take(this);
            };

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

        /// <summary>Shell 端口：切到回收站页时装载（导航加载口径——亮遮罩 + 行入场动画）。</summary>
        public async Task RefreshAsync()
        {
            if (ViewModel is { } vm) await vm.LoadAsync(navigating: true);
        }

        private TrashViewModel? _wiredVm;
        private ShortcutHost? _shortcutHost;

        private TrashViewModel? ViewModel => DataContext as TrashViewModel;

        private ShortcutScope ActiveScope => ViewModel?.ActivePane == TrashPane.Tree
            ? ShortcutScope.TrashTree
            : ShortcutScope.TrashMain;

        /// <summary>
        /// 本页「动作 id → 命令」映射（**键位不在本文件**：全站键位只声明在 <see cref="ShortcutCatalog"/>）。
        /// 本页不提供的键位：无 Ctrl+Z/Y、无 Ctrl+X/C/V、无 F2/Ctrl+Shift+N、**不注册任何全局键**。
        /// </summary>
        private static ShortcutCommandMap BuildShortcutCommands(TrashViewModel vm) => new ShortcutCommandMap()
            .Add(ShortcutAction.TrashGoBack, vm.GoBackCommand)
            .Add(ShortcutAction.TrashGoUp, vm.GoUpCommand)
            .Add(ShortcutAction.TrashGoForward, vm.GoForwardCommand)
            .Add(ShortcutAction.TrashRefresh, vm.RefreshCommand)
            .Add(ShortcutAction.TrashFocusPath, vm.EnterPathEditCommand)
            .Add(ShortcutAction.TrashSelectAll, vm.SelectAllCommand)
            .Add(ShortcutAction.TrashOpen, vm.OpenSelectionCommand)
            .Add(ShortcutAction.TrashPurge, vm.PurgeSelectionCommand)
            .Add(ShortcutAction.TrashRestoreOrigin, vm.RestoreSelectionCommand)
            .Add(ShortcutAction.TrashRestoreRoot, vm.RestoreSelectionToRootCommand)
            .Add(ShortcutAction.TrashEscape, vm.EscapeCommand)
            .Add(ShortcutAction.TrashContextMenu, vm.ShowContextMenuCommand)
            .Add(ShortcutAction.TrashMoveUp, vm.MoveSelectionCommand)
            .Add(ShortcutAction.TrashMoveDown, vm.MoveSelectionCommand)
            .Add(ShortcutAction.TrashSelectLast, vm.SelectLastCommand)
            .Add(ShortcutAction.TrashTreeUp, vm.MoveTreeSelectionCommand)
            .Add(ShortcutAction.TrashTreeDown, vm.MoveTreeSelectionCommand)
            .Add(ShortcutAction.TrashTreeCollapse, vm.ToggleTreeExpandCommand)
            .Add(ShortcutAction.TrashTreeExpand, vm.ToggleTreeExpandCommand);

        // 焦点不变式与行入场动画已收口到 UIKit（**唯一实现**）：
        // · `PageFocus.Take/Restore(this)` = 焦点收进页内（栏激活 / 页变可见 / 刷新链结束）
        // · `RowEntrance.Play(TrashTable.RowsList)` = 导航加载才播的行错峰入场

        private void OnPaneActivated(object? sender, TrashPane pane) => PageFocus.Take(this);

        /// <summary>刷新链结束：导航加载才播行入场动画；并守住"焦点在页内"不变式。</summary>
        private void OnRefreshCompleted(object? sender, bool wasNavigation)
        {
            if (wasNavigation) RowEntrance.Play(TrashTable.RowsList);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                PageFocus.Restore(this);
                // 覆盖层的自动关闭由 VM 刷新收尾负责（条目消失即关；状态在 VM，视图不持任何详情状态）
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
                new DataTableColumn { Field = "name", LabelKey = "ui.noun.name", Width = -1 },
                new DataTableColumn { Field = "type", LabelKey = "ui.noun.type", Width = 90 },
                new DataTableColumn { Field = "origin_path", LabelKey = "ui.noun.origin", Width = -2 },
                new DataTableColumn { Field = "deleted_at", LabelKey = "ui.noun.deletedAt", Width = 150 },
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

        // 行错峰入场已收口到 UIKit `Views.RowEntrance`（唯一实现，见 OnRefreshCompleted）。

        // ================= 行手势（与浏览页同一套归属校验） =================

        private TrashRowViewModel? _pressRow;
        private ModifierKeys _pressModifiers;
        private int _pressClickCount;

        private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressRow = null;
            _pressModifiers = Keyboard.Modifiers;
            _pressClickCount = e.ClickCount;

            _pressRow = (sender as FrameworkElement)?.DataContext as TrashRowViewModel;
            if (ViewModel == null || _pressRow == null) return;
            ViewModel.ActivatePane(TrashPane.Main);
            if (_pressModifiers == ModifierKeys.None && !ViewModel.IsSelectedId(_pressRow.Id))
                ViewModel.SetSelection(new[] { _pressRow.Id });   // 按下即反馈（与浏览页口径一致）
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
            if (!ReferenceEquals(row, pressed) || clicks > 1) return;
            ViewModel.SelectRowWithModifiers(row, mods);
            ViewModel.SetContextRow(row);
        }

        private void RowBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TrashRowViewModel row || ViewModel == null) return;
            if (!ViewModel.IsSelectedId(row.Id)) ViewModel.SetSelection(new[] { row.Id });
            ViewModel.SetContextRow(row);
        }

        // ================= 列表卡：空白点击清选中 =================
        // 唯一实现 = UIKit `Views.BlankClick`（XAML 上按区域挂载：列表卡用 ClearMainPaneSelectionCommand，
        // 内容区/导航行/状态栏空白用 ClearPageSelectionCommand）；命中回收站行不算空白
        // （行外层 Border 带 Tag="TrashRow"）。本文件不再保留手写命中测试（铁律 10）。

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

        // ================= 面包屑（编辑态） =================

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

        // ================= 只读详情覆盖层（共享详情页 LinkDetailPane） =================

        /// <summary>打开只读详情覆盖层：数据与动作全在 VM（共享详情页），视图不持任何状态。</summary>
        private void OnShowLinkDetailRequested(object? sender, TrashRowViewModel row)
            => ViewModel?.OpenLinkDetail(row);

        // ================= 工具栏按钮（还原 / 还原到根目录 / 永久删除） =================
        // （右栏详情栏不在此刷新：数据源 = VM 的 Details，由 VM 在选中投影点重建）

        private void TrashRestore_Click(object sender, RoutedEventArgs e)
            => ViewModel?.RestoreSelectionCommand.Execute(null);

        private void TrashRestoreToRoot_Click(object sender, RoutedEventArgs e)
            => ViewModel?.RestoreSelectionToRootCommand.Execute(null);

        private void TrashPurge_Click(object sender, RoutedEventArgs e)
            => ViewModel?.PurgeSelectionCommand.Execute(null);
    }
}

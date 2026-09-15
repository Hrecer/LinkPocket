using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.ViewModels;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 资源管理器式浏览页（P4）。视图只负责渲染与鼠标交互转发，
/// 业务逻辑全部在 BrowserViewModel（数据经协议、行状态在行 VM 上）。
/// </summary>
public partial class BrowserView : UserControl
{
    private bool _suppressTreeSelection;

    public BrowserView()
    {
        InitializeComponent();
        BrowserViewModel.Prompt ??= (title, defaultValue) => InputDialog.Show(title, defaultValue);
        DataContextChanged += (_, _) =>
        {
            if (ViewModel != null)
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    // —— 列宽拖拽（Windows 语义：列头右边界可拖动调整该列宽度，各行同步）——

    /// <summary>用户是否手动拖拽过列宽：拖过之后名称列不再自动重算（Windows 语义，列宽由用户决定）。</summary>
    private bool _columnsResizedByUser;

    private void RowsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_columnsResizedByUser || e.NewSize.Width <= 0) return;
        ViewModel?.InitColumnWidths(e.NewSize.Width);
    }

    private void ColumnResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.Thumb thumb) return;
        if (thumb.Tag is not string tag || !int.TryParse(tag, out var index)) return;
        if (ViewModel == null) return;

        _columnsResizedByUser = true;
        ViewModel.ResizeColumn(index, e.HorizontalChange);
    }

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

    // —— 面包屑路径编辑：候选键盘导航与取消 ——

    private bool _suppressCandidateChoose;

    private void PathEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Key == Key.Down)
        {
            _suppressCandidateChoose = true;
            try { ViewModel.MoveCandidate(1); } finally { _suppressCandidateChoose = false; }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _suppressCandidateChoose = true;
            try { ViewModel.MoveCandidate(-1); } finally { _suppressCandidateChoose = false; }
            e.Handled = true;
        }
    }

    private void PathEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 焦点移出路径框 → 退出编辑态（候选 ListBox Focusable=False，点击候选不会触发）
        if (ViewModel is { IsPathEditing: true })
            ViewModel.CancelPathEditCommand.Execute(null);
    }

    private void PathCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCandidateChoose) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string name) return;
        ViewModel?.ChooseCandidate(name);
    }

    private void PathCandidatesPopup_Closed(object? sender, EventArgs e)
    {
        // 点击候选项以外区域关闭 Popup → 退出编辑态（与 Esc 一致）
        if (ViewModel is { IsPathEditing: true })
            ViewModel.CancelPathEditCommand.Execute(null);
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
        if (payload == null || targetFolderId == null || ViewModel == null) return false;
        if (targetFolderId == null) return true;
        foreach (var item in payload.Rows)
        {
            if (item.Id == targetFolderId) return false;
            if (item.IsFolder && ViewModel.IsSelfOrDescendant(item.Id, targetFolderId)) return false;
        }
        return true;
    }

    private void RowBorder_DragOver(object sender, DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
        var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;

        var ok = row is { IsFolder: true } && IsDropValid(payload, row.Id);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void RowBorder_Drop(object sender, DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
        var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        if (payload != null && row is { IsFolder: true } && IsDropValid(payload, row.Id))
            _ = ViewModel?.MoveItemsAsync(payload.Rows.Select(r => (r.Id, r.IsFolder)), row.Id);
        e.Handled = true;
    }

    // —— 树节点拖放（移入对应文件夹；根节点 = 移到全部书签）——

    private void TreeItem_DragOver(object sender, DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
        var node = (sender as TreeViewItem)?.DataContext as FolderNode;
        var ok = node != null && IsDropValid(payload, node.FolderId);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TreeItem_Drop(object sender, DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
        var node = (sender as TreeViewItem)?.DataContext as FolderNode;
        if (payload != null && node != null && IsDropValid(payload, node.FolderId))
            _ = ViewModel?.MoveItemsAsync(payload.Rows.Select(r => (r.Id, r.IsFolder)), node.FolderId);
        e.Handled = true;
    }

    /// <summary>点击树节点 → 进入对应目录。</summary>
    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection) return;
        if (e.NewValue is FolderNode node && ViewModel != null)
            _ = ViewModel.LoadAsync(node.FolderId);
    }

    /// <summary>VM 当前目录变化（双击行/面包屑/后退前进）→ 同步左侧树选中；进入路径编辑态 → 聚焦并全选。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.IsPathEditing))
        {
            if (ViewModel is { IsPathEditing: true })
            {
                PathEditBox.Focus();
                PathEditBox.SelectAll();
            }
            return;
        }

        if (e.PropertyName != nameof(BrowserViewModel.CurrentFolderId) || ViewModel == null) return;

        var targetId = ViewModel.CurrentFolderId;
        _suppressTreeSelection = true;
        try
        {
            SelectTreeItem(FolderTreeControl.Items, targetId);
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    private bool SelectTreeItem(ItemCollection items, string? folderId)
    {
        foreach (var item in items)
        {
            if (FolderTreeControl.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                continue;

            if (item is FolderNode node && string.Equals(node.FolderId, folderId, StringComparison.Ordinal))
            {
                container.IsSelected = true;
                return true;
            }

            if (SelectTreeItem(container.Items, folderId))
            {
                container.IsExpanded = true;
                return true;
            }
        }
        return false;
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

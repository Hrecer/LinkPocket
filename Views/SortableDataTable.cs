using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkPocket.Views;

/// <summary>
/// 可排序数据表的一列定义（数据驱动，单一数据源）：
/// 表头按钮与每一行的 Grid 列都由 Columns 生成，杜绝两份列定义。
/// 浏览页主栏与搜索结果表共用本控件（见 SortableDataTable）。
/// </summary>
public class DataTableColumn
{
    /// <summary>排序键标识（表头点击时回传）。</summary>
    public string Field { get; init; } = "";
    /// <summary>表头文案。</summary>
    public string Label { get; init; } = "";
    /// <summary>列宽；小于 0 表示 Star（占满剩余）。</summary>
    public double Width { get; init; } = -1;
    /// <summary>排序键：把数据项映射为可比较值（null = 该列不可内部排序，由外部 VM 排序）。</summary>
    public Func<object, IComparable>? SortKey { get; init; }
    /// <summary>单元格工厂：把数据项映射为该列的单元格内容（仅默认行模式使用）。</summary>
    public Func<object, FrameworkElement> CellFactory { get; init; } = _ => new FrameworkElement();
}

/// <summary>表头点击排序事件（外部 VM 排序模式消费；如浏览页的服务端排序）。</summary>
public class SortEventArgs : EventArgs
{
    /// <summary>排序字段（= 列定义 Field）。</summary>
    public string Field { get; init; } = "";
    /// <summary>true = 升序。</summary>
    public bool Ascending { get; init; }
}

/// <summary>
/// 唯一的共享数据表控件（表头排序 + 列宽拖拽 + 行悬停/选中 + 空态占位）。
/// 浏览页主栏与搜索结果表复用同一份实现，杜绝两份表头/行代码。
///
/// 两种行模式（互斥）：
/// - <see cref="ItemTemplate"/> 模式：设置 ItemTemplate 后，行 = 模板呈现（行悬停/选中/右键菜单
///   全部由模板自带触发器与事件处理，控件不干预）。适合行内容复杂、需要富交互的页面（浏览页）。
///   此模式下排序不内部执行，只发 <see cref="SortChanged"/> 事件由外部 VM 驱动（如服务端排序）。
/// - 默认工厂模式：不设 ItemTemplate 时，按列 <see cref="DataTableColumn.CellFactory"/> 生成
///   单元格行，控件自带悬停高亮 / 单击选中 / RowClick / RowDoubleClick，列有 SortKey 时内部排序
///   （搜索结果表）。
///
/// 列宽单一数据源：<see cref="ColumnWidths"/>（ObservableCollection）。表头 Grid 与每一行的
/// Grid 列全部通过 <see cref="ColumnWidthBinding"/> 绑定到它，拖拽表头右缘 Thumb 只改这一份，
/// 表头与所有行实时同步。
/// </summary>
public class SortableDataTable : Grid
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(IEnumerable<DataTableColumn>), typeof(SortableDataTable),
        new PropertyMetadata(null, (d, _) => ((SortableDataTable)d).Rebuild()));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SortableDataTable),
        new PropertyMetadata(null, (d, _) => ((SortableDataTable)d).RenderRows()));

    public static readonly DependencyProperty SortFieldProperty = DependencyProperty.Register(
        nameof(SortField), typeof(string), typeof(SortableDataTable),
        new PropertyMetadata("", (d, _) => ((SortableDataTable)d).OnSortChanged()));

    public static readonly DependencyProperty SortAscendingProperty = DependencyProperty.Register(
        nameof(SortAscending), typeof(bool), typeof(SortableDataTable),
        new PropertyMetadata(true, (d, _) => ((SortableDataTable)d).OnSortChanged()));

    public static readonly DependencyProperty EmptyContentProperty = DependencyProperty.Register(
        nameof(EmptyContent), typeof(FrameworkElement), typeof(SortableDataTable),
        new PropertyMetadata(null, (d, _) => ((SortableDataTable)d).RenderRows()));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(SortableDataTable),
        new PropertyMetadata(null, (d, _) => ((SortableDataTable)d).ApplyRowMode()));

    /// <summary>表头色带外距：搜索页默认 8（行区浮动留白对齐）；嵌入卡片容器时置 0（浏览页）。</summary>
    public static readonly DependencyProperty HeaderBandMarginProperty = DependencyProperty.Register(
        nameof(HeaderBandMargin), typeof(Thickness), typeof(SortableDataTable),
        new PropertyMetadata(new Thickness(8, 0, 8, 0)));

    public IEnumerable<DataTableColumn> Columns
    {
        get => (IEnumerable<DataTableColumn>)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string SortField
    {
        get => (string)GetValue(SortFieldProperty);
        set => SetValue(SortFieldProperty, value);
    }

    public bool SortAscending
    {
        get => (bool)GetValue(SortAscendingProperty);
        set => SetValue(SortAscendingProperty, value);
    }

    public FrameworkElement EmptyContent
    {
        get => (FrameworkElement)GetValue(EmptyContentProperty);
        set => SetValue(EmptyContentProperty, value);
    }

    /// <summary>
    /// 行模板（模板模式）。设置后行由模板呈现，控件不再生成默认单元格行，
    /// 排序只发 SortChanged 事件、内部不排序；空态与行悬停/选中由使用方负责。
    /// </summary>
    public DataTemplate ItemTemplate
    {
        get => (DataTemplate)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public Thickness HeaderBandMargin
    {
        get => (Thickness)GetValue(HeaderBandMarginProperty);
        set => SetValue(HeaderBandMarginProperty, value);
    }

    /// <summary>列宽单一数据源：表头与每一行的 Grid 列都绑定到这里（拖拽只改这一份）。</summary>
    public ObservableCollection<GridLength> ColumnWidths { get; } = new();

    /// <summary>表头点击排序（Field + 方向已切换完毕）。外部 VM 排序模式在此接手。</summary>
    public event EventHandler<SortEventArgs>? SortChanged;

    /// <summary>当前选中项（默认工厂模式内部绘制 PrimaryContainer 选中态；模板模式下不使用）。</summary>
    public object? SelectedItem { get; private set; }

    /// <summary>行单击时触发（参数 = 数据项；仅默认工厂模式）。</summary>
    public event EventHandler<object>? RowClick;

    /// <summary>行双击时触发（参数 = 数据项；仅默认工厂模式）。</summary>
    public event EventHandler<object>? RowDoubleClick;

    /// <summary>行宿主（供使用方做行容器级操作，如入场动画取容器）。</summary>
    public ItemsControl RowsList => _rowsList;

    private readonly Border _headerBand;
    private readonly Grid _headerGrid;
    private readonly ItemsControl _rowsList;
    /// <summary>空态承载器（独立 ContentControl，绝不与 ItemsSource 混用 Items —— 混用会抛
    /// "在使用 ItemsSource 之前，项集合必须为空"，搜索结果因此永远渲染不出来）。</summary>
    private readonly ContentControl _emptyHost;
    private readonly Dictionary<object, Border> _rowMap = new();

    /// <summary>表头按钮登记表（按钮位于单元格 Grid 内，不能靠 OfType 直查表头 Grid）。</summary>
    private readonly List<SortableHeaderButton> _headerButtons = new();

    /// <summary>是否模板模式（ItemTemplate 已设置）。</summary>
    private bool IsTemplateMode => ItemTemplate != null;

    public SortableDataTable()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headerBand = new Border
        {
            Background = (Brush)Application.Current.FindResource("TintPanel"),
            CornerRadius = new CornerRadius(24, 24, 0, 0),
            // ⚠️ 高度固定 32px = 侧栏「文件夹」标题带（Padding 10,10,10,6 + 12px 文字 = 32）：
            // 两条紫色色带等高，底边严格对齐（此前靠内容撑高，比侧栏低边多出几像素）。
            // 水平内距 16 = 行容器内距，列边界逐列对齐不变；表头内容垂直居中。
            // 注意：此类数值均由用户直接确认后写入，属"对齐类"简单调整——后续微调直接改值即可，
            // 无需探针/截图等重验证流程（过度验证反而拖慢迭代）。
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0)
        };
        _headerBand.SetBinding(MarginProperty, new Binding(nameof(HeaderBandMargin)) { Source = this });
        _headerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _headerBand.Child = _headerGrid;
        Children.Add(_headerBand);
        SetRow(_headerBand, 0);

        _rowsList = new ItemsControl
        {
            ItemsPanel = BuildRowsPanel(),
            Padding = new Thickness(0, 4, 0, 0)
        };
        _emptyHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false, // 空态不拦截鼠标（右键空白区菜单仍可用）
            Visibility = Visibility.Collapsed
        };
        var rowsArea = new Grid();
        rowsArea.Children.Add(_rowsList);
        rowsArea.Children.Add(_emptyHost);
        var scroller = new ScrollViewer
        {
            Content = rowsArea,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Children.Add(scroller);
        SetRow(scroller, 1);
    }

    private static ItemsPanelTemplate BuildRowsPanel()
    {
        const string xaml =
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<StackPanel/>" +
            "</ItemsPanelTemplate>";
        return (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private IEnumerable<DataTableColumn> ColumnList => Columns ?? Array.Empty<DataTableColumn>();

    // —— 列定义变化：重建表头 + 列宽单一数据源 ——

    private void Rebuild()
    {
        _headerGrid.ColumnDefinitions.Clear();
        _headerGrid.Children.Clear();
        _headerButtons.Clear();

        // 列数不变时保留现行列宽（用户拖拽结果不因 Columns 重设而丢）
        if (ColumnWidths.Count != ColumnList.Count())
        {
            ColumnWidths.Clear();
            foreach (var col in ColumnList)
                ColumnWidths.Add(col.Width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(col.Width));
        }

        var i = 0;
        foreach (var col in ColumnList)
        {
            // 表头列宽绑定到 ColumnWidths[i]（单一数据源，拖拽 Replace 通知即同步）
            var cd = new ColumnDefinition();
            BindingOperations.SetBinding(cd, ColumnWidthBinding.WidthProperty,
                new Binding($"ColumnWidths[{i}]") { Source = this });
            _headerGrid.ColumnDefinitions.Add(cd);

            // 单元格 = 排序按钮 + 右缘拖拽 Thumb（与主栏同一套交互）
            var cell = new Grid();
            // Z 递减：本列手柄凸出部分压在右邻单元格之上
            cell.SetValue(Panel.ZIndexProperty, ColumnList.Count() - i + 10);

            var header = new SortableHeaderButton
            {
                Content = col.Label,
                Field = col.Field,
                Direction = col.Field == SortField ? SortAscending : null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // 药丸紧挨（无外距），拖拽分隔线画在两药丸的贴合线上（同上一版本观感）
                Margin = new Thickness(0, 0, 0, 0),
                ToolTip = $"按{col.Label}排序"
            };
            header.Click += OnHeaderClick;
            cell.Children.Add(header);
            _headerButtons.Add(header);

            // 手柄：App.xaml 共享 ColumnResizeThumb 样式（hover/拖动紫线由样式触发器驱动）
            var thumb = new Thumb { Style = (Style)Application.Current.FindResource("ColumnResizeThumb"), Tag = i };
            thumb.SetValue(Panel.ZIndexProperty, 2);
            thumb.DragDelta += OnThumbDragDelta;
            cell.Children.Add(thumb);

            Grid.SetColumn(cell, i);
            _headerGrid.Children.Add(cell);
            i++;
        }
        ApplyRowMode();
        OnSortChanged();
    }

    /// <summary>拖拽表头右缘：改 ColumnWidths[idx]（像素），表头与所有行经绑定实时同步。</summary>
    private void OnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not int idx) return;
        if (idx < 0 || idx >= ColumnWidths.Count) return;

        var current = _headerGrid.ColumnDefinitions[idx].ActualWidth;
        if (current <= 0) current = 60;
        ColumnWidths[idx] = new GridLength(Math.Max(60, Math.Round(current + e.HorizontalChange)));
    }

    // —— 行模式：模板模式 = ItemsSource 直通 + 模板呈现；工厂模式 = 控件生成行 ——

    private void ApplyRowMode()
    {
        // 注意：不再使用 Items 直添（与 ItemsSource 互斥会抛异常）；空态由 _emptyHost 承载。
        _rowMap.Clear();
        if (IsTemplateMode)
        {
            // 模板模式：ItemsSource 直通（外部集合变化自动增删行），行视觉/交互归模板
            _rowsList.ItemTemplate = ItemTemplate;
            _rowsList.SetBinding(ItemsControl.ItemsSourceProperty,
                new Binding(nameof(ItemsSource)) { Source = this });
            _emptyHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            _rowsList.ItemTemplate = null;
            _rowsList.ItemsSource = null; // 断开直通绑定，交给 RenderRows 管理
            RenderRows();
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not SortableHeaderButton header) return;
        if (header.Field == SortField) SortAscending = !SortAscending;
        else { SortField = header.Field; SortAscending = true; }
        SortChanged?.Invoke(this, new SortEventArgs { Field = SortField, Ascending = SortAscending });
    }

    private void OnSortChanged()
    {
        foreach (var header in _headerButtons)
            header.Direction = header.Field == SortField ? SortAscending : null;

        // 内部排序只在默认工厂模式 + 列有 SortKey 时执行；模板模式（外部 VM 排序）不重排行
        if (!IsTemplateMode && ColumnList.Any(c => c.Field == SortField && c.SortKey != null))
            RenderRows();
    }

    private IEnumerable<object> SortedItems()
    {
        var items = ItemsSource?.Cast<object>() ?? Array.Empty<object>();
        var column = ColumnList.FirstOrDefault(c => c.Field == SortField);
        if (column?.SortKey == null) return items;
        return SortAscending
            ? items.OrderBy(column.SortKey)
            : items.OrderByDescending(column.SortKey);
    }

    private void RenderRows()
    {
        if (IsTemplateMode) return; // 模板模式行由 ItemsSource 直通驱动

        var items = SortedItems().ToList();
        _rowMap.Clear();
        SelectedItem = null;

        if (items.Count == 0)
        {
            // 空态走独立 _emptyHost（Items 与 ItemsSource 绝不混用）
            _rowsList.ItemsSource = null;
            if (EmptyContent != null)
            {
                EmptyContent.HorizontalAlignment = HorizontalAlignment.Center;
                EmptyContent.VerticalAlignment = VerticalAlignment.Center;
                EmptyContent.Margin = new Thickness(0, 56, 0, 0);
            }
            _emptyHost.Content = EmptyContent;
            _emptyHost.Visibility = EmptyContent != null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _emptyHost.Content = null;
        _emptyHost.Visibility = Visibility.Collapsed;
        _rowsList.ItemsSource = items.Select(BuildRow).ToList();
    }

    private Border BuildRow(object item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(8, 1, 8, 1),
            Padding = new Thickness(16, 9, 16, 9),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Triggers.Add(new Trigger
        {
            Property = IsMouseOverProperty, Value = true,
            Setters = { new Setter(BackgroundProperty, (Brush)Application.Current.FindResource("SurfaceContainerHighest")) }
        });
        row.Style = style;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        foreach (var col in ColumnList)
        {
            // 行列宽同样绑定 ColumnWidths[i]：拖拽表头 → Replace 通知 → 行实时同步
            var cd = new ColumnDefinition();
            var index = grid.ColumnDefinitions.Count;
            BindingOperations.SetBinding(cd, ColumnWidthBinding.WidthProperty,
                new Binding($"ColumnWidths[{index}]") { Source = this });
            grid.ColumnDefinitions.Add(cd);

            var cell = col.CellFactory(item);
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        row.Child = grid;

        row.PreviewMouseLeftButtonDown += (s, e) =>
        {
            SelectItem(item);
            if (e.ClickCount == 2)
                RowDoubleClick?.Invoke(this, item);
            else
                RowClick?.Invoke(this, item);
        };

        return row;
    }

    /// <summary>选中某一项（更新内部绘制；不匹配的项恢复透明。仅默认工厂模式）。</summary>
    public void SelectItem(object item)
    {
        if (IsTemplateMode) return;
        if (SelectedItem is { } old && _rowMap.TryGetValue(old, out var oldRow))
            oldRow.Background = Brushes.Transparent;
        SelectedItem = item;
        if (_rowMap.TryGetValue(item, out var newRow))
            newRow.Background = (Brush)Application.Current.FindResource("PrimaryContainer");
    }

    /// <summary>清除选中（仅默认工厂模式）。</summary>
    public void ClearSelection()
    {
        if (SelectedItem is { } old && _rowMap.TryGetValue(old, out var oldRow))
            oldRow.Background = Brushes.Transparent;
        SelectedItem = null;
    }
}

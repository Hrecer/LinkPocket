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
using System.Windows.Threading;
using LinkPocket.UIKit;
using LinkPocket.I18n;

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
    /// <summary>表头**文案键**（<c>ui.noun.*</c>）；空串 = 无字面（勾选列）。
    /// 这里存的必须是键不是文本：表头由本控件建，切语言时它重跑一次投影就够，
    /// 而存文本就等于把语言烤进调用点。
    /// </summary>
    public string LabelKey { get; init; } = "";
    /// <summary>列宽；负值表示 Star（按权重占剩余空间：-1 = 1 份，-2 = 2 份…），正值 = 固定像素。</summary>
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

    /// <summary>行选中开关（默认开）。两种模式：
    /// · 开启 = 控件内部绘制（点击行 → SelectItem 单选自绘）；
    /// · 关闭 = **外部托管**：点击行不绘制，页面用 <see cref="ApplySelection"/> 把
    ///   VM 的选中集合投影到行上（搜索 / 智能列表 / 去重明细）。
    /// RowClick/RowDoubleClick 两种模式下都照常触发。</summary>
    public static readonly DependencyProperty SelectionEnabledProperty = DependencyProperty.Register(
        nameof(SelectionEnabled), typeof(bool), typeof(SortableDataTable), new PropertyMetadata(true));

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

    /// <summary>行选中开关（默认开）：关闭时 <see cref="SelectItem"/> 为无操作——行结构性不可选中。</summary>
    public bool SelectionEnabled
    {
        get => (bool)GetValue(SelectionEnabledProperty);
        set => SetValue(SelectionEnabledProperty, value);
    }

    /// <summary>列宽单一数据源：表头与每一行的 Grid 列都绑定到这里（拖拽只改这一份）。</summary>
    public ObservableCollection<GridLength> ColumnWidths { get; } = new();

    /// <summary>表头点击排序（Field + 方向已切换完毕）。外部 VM 排序模式在此接手。</summary>
    public event EventHandler<SortEventArgs>? SortChanged;

    /// <summary>当前选中项（= 已绘制选中集里的第一项；多选以 <see cref="ApplySelection"/> 为准）。</summary>
    public object? SelectedItem { get; private set; }

    /// <summary>已绘制选中底色的行集合（**唯一绘制事实源**：内部单选自绘与外部托管投影共用一套）。</summary>
    private readonly HashSet<object> _paintedSelection = new();

    /// <summary>行单击时触发（参数 = 数据项；仅默认工厂模式）。</summary>
    public event EventHandler<object>? RowClick;

    /// <summary>行双击时触发（参数 = 数据项；仅默认工厂模式）。</summary>
    public event EventHandler<object>? RowDoubleClick;

    /// <summary>行宿主（供使用方做行容器级操作，如入场动画取容器）。</summary>
    public ItemsControl RowsList => _rowsList;

    private readonly Border _headerBand;
    private readonly Grid _headerGrid;
    private readonly ItemsControl _rowsList;
    /// <summary>
    /// 行区滚动宿主（排序后需要恢复滚动位置、把某行滚入视口）。
    /// ⚠️ **惰性取**：它现在由行列表的控件模板生成（模板未应用时不存在），
    /// 不能在构造函数里抓引用（那只在"ScrollViewer 包在 ItemsControl 外面"的旧结构下成立，
    /// 而那个结构正是**虚拟化失效**的原因，见 <see cref="BuildRowsTemplate"/>）。
    /// </summary>
    private ScrollViewer? _rowsScroller;
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

        // 表头文案的**换语言失效通道**：表头是代码建的控件（`ColumnList` 里只有键），
        // 走不了 `{loc:Loc}` 那条绑定，所以由本控件听语言版本、只刷新文案（不重建表头）。
        // ⚠️ 挂载点选**构造函数**而不是 `Loaded`：不挂进可视树的表（渲染检查与单测直接建的那些）永远不触发
        //    `Loaded`，那样"订阅了但没生效"和"没订阅"长得一模一样（实测踩过：语言切了表头还是中文）。
        //    卸在 `Unloaded`（可重挂，不泄漏），与 `LocText` 的版本绑定是同一条底层机制。
        Loc.Table.PropertyChanged += OnLocaleTableChanged;
        Unloaded += (_, _) => Loc.Table.PropertyChanged -= OnLocaleTableChanged;

        _headerBand = new Border
        {
            CornerRadius = new CornerRadius(24, 24, 0, 0),
            // ⚠️ 高度固定 32px = 侧栏「文件夹」标题带（BrowserView.xaml 中同样 Height=32、文字垂直居中）：
            // 两条紫色色带等高，底边严格对齐（侧栏曾靠 Padding+行高撑出 31.x 导致底边差一点）。
            // 水平内距 16 = 行容器内距，列边界逐列对齐不变；表头内容垂直居中。
            // 注意：此类数值属"对齐类"简单调整——后续微调直接改值即可。
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0)
        };
        // ⚠️ 表头底色必须是**资源引用**，不能 FindResource 取画刷后赋值（换主题后
        // 这一栏永远停在旧主题的紫。「切主题 = 资源字典换画刷实例」的地方，一次性取到的画刷会被
        // 固化成本地值，之后再也不跟随）。SetResourceReference 挂的是资源引用表达式 → 换主题即跟随。
        _headerBand.SetResourceReference(Border.BackgroundProperty, Theming.Tokens.AppTokens.SurfaceHeaderBand);
        _headerBand.SetBinding(MarginProperty, new Binding(nameof(HeaderBandMargin)) { Source = this });
        _headerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _headerBand.Child = _headerGrid;
        Children.Add(_headerBand);
        SetRow(_headerBand, 0);

        _rowsList = new ItemsControl
        {
            ItemsPanel = BuildRowsPanel(),
            Template = BuildRowsTemplate(),
            Padding = new Thickness(0, 4, 0, 0),
            // 「聚焦禁描边」是硬性口径，且**逐类容器都要核对**（WARNINGS 43）：表格内部的
            // ScrollViewer / ItemsControl / ContentControl 来自框架默认模板，FocusVisualStyle 非空
            // ——一旦它们（经 Tab 或代码 Focus）拿到键盘焦点就会画出**原生焦点虚线框**
            //（去重明细页 F5 后会出现黑虚线）。逐类置空，本控件内部不再有可疑元素。
            FocusVisualStyle = null
        };
        _emptyHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false, // 空态不拦截鼠标（右键空白区菜单仍可用）
            Visibility = Visibility.Collapsed,
            FocusVisualStyle = null
        };
        // 虚拟化四项**在 ItemsControl 与面板模板两处都写**：
        // 面板读的是"面板上的值"（模板里那份），而滚动单位这类设置的实际生效路径会去问 ItemsControl——
        // 实测只写在面板上时，读回来是 Pixel 但滚动行为与 Extent 仍是"按项"（Extent=项数，不是像素高）。
        // 两处都写，谁读都对；重复设置同一附加属性没有副作用。
        VirtualizingPanel.SetIsVirtualizing(_rowsList, true);
        VirtualizingPanel.SetVirtualizationMode(_rowsList, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_rowsList, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(_rowsList, new VirtualizationCacheLength(1));
        VirtualizingPanel.SetCacheLengthUnit(_rowsList, VirtualizationCacheLengthUnit.Page);

        // 空态与行列表同容器叠放（空态是覆盖层、不参与命中）。
        var rowsArea = new Grid();
        rowsArea.Children.Add(_rowsList);
        rowsArea.Children.Add(_emptyHost);
        Children.Add(rowsArea);
        SetRow(rowsArea, 1);

        // 表头列区与行内容区对齐：行区宽度会随「纵向滚动条出现/消失」「窗口缩放」变化，
        // 也必须等布局结束后才能实测（行容器是布局期生成的）。
        _rowsList.SizeChanged += (_, _) => ScheduleHeaderAlignment();
        Loaded += (_, _) => ScheduleHeaderAlignment();

        // 容器**被实现**（滚进视口）时重投影一次选中底色。
        // ⚠️ 为什么需要：虚拟化生效后，行容器是"用到才挂进可视树"的——工厂模式（搜索/回收站/智能列表）
        //    的行底色是**画在容器上**的（`_rowMap` + SetResourceReference），
        //    而 `ApplySelection` 只在调用的那一刻给当时**已实现**的行上色 ⇒
        //    滚动到之前没实现过的选中行时底色会缺一块（"选中丢了"的假象）。
        //    这是虚拟化的必然连带项，不是可选优化。
        _rowsList.ItemContainerGenerator.StatusChanged += (_, _) => RepaintSelectionOnRealizedContainers();
    }

    /// <summary>
    /// 行列表面板：<see cref="VirtualizingStackPanel"/>，**虚拟化属性直接写在面板上**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 属性为什么不写在 ItemsControl 上（那样写曾经整整失效一版）：
    /// <c>VirtualizingPanel.IsVirtualizing</c> 是继承属性，会传到面板；
    /// 而 <c>VirtualizationMode</c> / <c>ScrollUnit</c> / <c>CacheLength</c> **不是**继承属性——
    /// 写在 ItemsControl 上时，实测面板读回的仍是默认值（`Mode=Standard`、`ScrollUnit=Item`）。
    /// 写在面板标签上才是"这条设置真的到了它该在的地方"。
    /// <para>⚠️ 1.1 如实说明：虚拟化只对【模板模式】（ItemsSource=数据对象 + ItemTemplate）生效；
    /// 【工厂模式】的 ItemsSource 元素是 BuildRow 生成的 Border（UIElement），Panel 直接挂进可视树、
    /// 不做容器化 → 行数 = 实例化的控件数（万级数据下实测约 2.3 ms/行，见 `PERF-LARGE-LIBRARY.md`）。
    /// 工厂模式用于搜索/智能列表/回收站，它们的规模必须由**调用方**保证（搜索无上限是已知缺陷）。</para>
    /// </remarks>
    private static ItemsPanelTemplate BuildRowsPanel()
    {
        const string xaml =
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<VirtualizingStackPanel VirtualizingPanel.IsVirtualizing='True' " +
            "VirtualizingPanel.VirtualizationMode='Recycling' " +
            "VirtualizingPanel.ScrollUnit='Pixel' " +
            "VirtualizingPanel.CacheLength='1' VirtualizingPanel.CacheLengthUnit='Page'/>" +
            "</ItemsPanelTemplate>";
        return (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>
    /// 行列表的控件模板：<b>ScrollViewer 直接包 <see cref="ItemsPresenter"/></b>。
    /// </summary>
    /// <remarks>
    /// 这是 WPF 列表虚拟化**唯一生效的形状**（ListBox / TreeView 的默认模板都是它）：
    /// ScrollViewer 的滚动宿主（<c>ScrollContentPresenter</c>）必须直接看到
    /// <see cref="ItemsPresenter"/> 里的 <see cref="VirtualizingStackPanel"/>，才会把
    /// <c>IScrollInfo</c> 交给面板；否则滚动宿主退化成 <c>ScrollContentPresenter</c> 自己（物理滚动），
    /// 面板的 <c>Extent/Viewport</c> 恒为 0 → **每个数据项都实例化一个行容器**。
    /// <para>旧结构（ScrollViewer 包在 ItemsControl 外面：`scroller.Content = _rowsList`）实测在
    /// 1 630 行时实例化 1 630 个行容器、10 001 行时实例化 10 001 个（打开一个 10 001 行的目录
    /// 让 UI 线程停摆 37～90 秒）。改成本模板后同样数据实测 25 个容器、首屏 0.6～0.9 秒。</para>
    /// <para>⚠️ 滚动宿主改由模板生成 ⇒ 引用它的地方一律经 <see cref="RowsScroller"/> 惰性取。</para>
    /// </remarks>
    private static ControlTemplate BuildRowsTemplate()
    {
        const string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ItemsControl'>" +
            "<Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>" +
            // FocusVisualStyle 置空（WARNINGS 43/62：框架默认容器的焦点虚线框必须逐类摘掉）。
            "<ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' " +
            "HorizontalScrollBarVisibility='Disabled' Focusable='False' FocusVisualStyle='{x:Null}'>" +
            "<ItemsPresenter/>" +
            "</ScrollViewer></Border></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>行区滚动宿主（模板生成，惰性取）。模板未应用时返回 null——调用方必须容忍。</summary>
    /// <summary>行区滚动**接近底部**（余量不足两屏）：分页续载的触发信号——
    /// 万行目录不再一次全量搬运（引擎按 per_page 切片，界面滚到底自动追加下一页）。
    /// 少行表（Extent 没超出两屏）也会触发，由订阅方用"还有没有下一页"自行短路。</summary>
    public event EventHandler? ScrollNearBottom;

    private ScrollViewer? RowsScroller
    {
        get
        {
            if (_rowsScroller == null)
            {
                _rowsScroller = FindVisualChild<ScrollViewer>(_rowsList);
                if (_rowsScroller != null)
                {
                    double lastFiredOffset = -1;
                    _rowsScroller.ScrollChanged += (_, e) =>
                    {
                        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
                        // 只在**向下滚过上次触发点**时发信号：续载追加 → Reset 重建 → 编辑框重聚焦
                        // 会把视口拉回底部（实测把 7 页一口气拉完），必须用"偏移在增长"斩断这条环。
                        if (e.VerticalOffset <= lastFiredOffset) return;
                        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ViewportHeight * 2)
                        {
                            lastFiredOffset = e.VerticalOffset;
                            ScrollNearBottom?.Invoke(this, EventArgs.Empty);
                        }
                    };
                }
            }
            return _rowsScroller;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindVisualChild<T>(child) is { } deep) return deep;
        }
        return null;
    }

    private IEnumerable<DataTableColumn> ColumnList => Columns ?? Array.Empty<DataTableColumn>();

    /// <summary>
    /// 语言版本变了：<b>只重盖表头文案的语言代数</b>（不重建表头、不动列宽与排序）。
    /// </summary>
    /// <remarks>
    /// 表头是代码建的控件（列定义里只有键），没有 <c>{loc:Loc}</c> 那种绑定位置，所以由本控件听版本。
    /// 文案值是 <see cref="LinkPocket.I18n.LocText"/>（语言代数烙在值身份里）：语言一变必须**重盖一次代数**，
    /// 值才会真的变化、WPF 才会把它推给模板里的自适应通道 —— 直写等值的 <c>LocValue</c> 会被属性系统
    /// 判定"没变"，表头停在上一种语言（实测：版本事件到了、表头一个字没换）。
    /// 只改文案 ⇒ 排序态、列宽、选中都不受影响。提示不在此重设：<c>SetTip</c> 那条绑定自带语言版本。
    /// </remarks>
    private void OnLocaleTableChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var i = 0;
        foreach (var col in ColumnList)
        {
            if (i >= _headerButtons.Count) break;
            _headerButtons[i].SetValue(SortableHeaderButton.TextProperty,
                LinkPocket.I18n.LocText.Of(Loc.K(col.LabelKey)));
            i++;
        }
    }

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
                // 负值 = Star 权重（-1 → 1*，-2 → 2*），正值 = 固定像素
                ColumnWidths.Add(col.Width < 0
                    ? new GridLength(-col.Width, GridUnitType.Star)
                    : new GridLength(col.Width));
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
                Field = col.Field,
                Direction = col.Field == SortField ? SortAscending : null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // 药丸紧挨（无外距），拖拽分隔线画在两药丸的贴合线上（同上一版本观感）
                Margin = new Thickness(0, 0, 0, 0)
            };
            // 表头文字的**可用宽** = 本列表头单元格实测宽 − 药丸内距：列宽是冻结几何，文案宽度随语言变，
            // 放不下时由 LocFit 缩字号（绝不是画到相邻列上被右缘裁掉——那正是没有这个数时的实测症状）。
            // ⚠️ 接的是**单元格的实测宽**而不是 `ColumnWidths[i]`：星号列的 `GridLength.Value` 是权重
            //    （1 / 2），转换器会算出 0 ⇒ `MaxWidth=0` 把表头文字**夹成 0 宽**（「名称」「路径」整格没字），
            //    而单元格实测宽在星号列冻结前后都是真像素。值一变（拖列宽 / 窗口缩放）就重投影一次。
            BindingOperations.SetBinding(header, SortableHeaderButton.TextWidthProperty,
                new Binding(nameof(FrameworkElement.ActualWidth))
                {
                    Source = cell,
                    Converter = ColumnTextWidthConverter.Instance,
                    Mode = BindingMode.OneWay,
                });
            // 表头文字 = 文案值（LocText：语言代数烙在值身份里），提示 = 文案值走绑定。
            // ⚠️ 写 `Text`（自有 DP）而不是 `Content`：`Content` 注册类型是 object，模板绑定会把
            //    文案值转成 ToString 的记录字符串，自适应通道拿不到值（实测表头因此画不出列名）。
            // ⚠️ 用 `SetValue` 直写而不是在自有 DP 上再套一层取词绑定：取词转换器产出的是**字符串**，
            //    与 LocText 型 DP 类型不合，绑定静默失败、值停在默认值（实测表达式在、值恒空）。
            //    这里本来就有"列定义一变就重建 + 语言版本一变就重盖代数"两条生命周期（见
            //    OnLocaleTableChanged），直写已经够用。
            header.SetValue(SortableHeaderButton.TextProperty,
                LinkPocket.I18n.LocText.Of(Loc.K(col.LabelKey)));
            header.SetTip(Loc.K("ui.sort.tip", Loc.K(col.LabelKey)));
            header.Click += OnHeaderClick;
            cell.Children.Add(header);
            _headerButtons.Add(header);

            // 手柄：App.xaml 共享 ColumnResizeThumb 样式（hover/拖动紫线由样式触发器驱动）
            // ⚠️ 最右列不放拖拽手柄：最后一列右缘之外已无列可调，放了会在表头右端凭空多出
            //     一根可拖拽竖线（否则创建时间右边会多出一根"滑动条"）。
            if (i < ColumnList.Count() - 1)
            {
                var thumb = new Thumb { Style = (Style)Application.Current.FindResource("ColumnResizeThumb"), Tag = i };
                thumb.SetValue(Panel.ZIndexProperty, 2);
                thumb.DragDelta += OnThumbDragDelta;
                cell.Children.Add(thumb);
            }

            Grid.SetColumn(cell, i);
            _headerGrid.Children.Add(cell);
            i++;
        }
        ApplyRowMode();
        OnSortChanged();
    }

    /// <summary>列宽下限（与表头拖拽一致）。</summary>
    private const double MinColumnWidth = 60;

    /// <summary>
    /// 拖拽表头右缘调整列宽（资源管理器语义）：
    /// 1. 先把所有 Star（弹性）列冻结为当前像素宽 —— 否则把某列从 * 改成固定值时，
    ///    剩余空间会在所有弹性列之间隐形重分配，表现为"拖这一列，别的列跟着变"（严重误导）。
    /// 2. 本列 +Δ、相邻列 −Δ（总宽恒定，其余列纹丝不动）；两列都不低于下限。
    /// </summary>
    private void OnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not int idx) return;

        var defs = _headerGrid.ColumnDefinitions;
        if (idx < 0 || idx >= defs.Count) return;
        if (idx >= ColumnWidths.Count) return;

        FreezeStarColumns();

        var left = defs[idx].ActualWidth;
        if (left <= 0) left = ColumnWidths[idx].Value;

        // 相邻补偿列：优先右邻，最右列则用左邻（保证总宽恒定、不撑破视口）
        var partner = idx + 1 < defs.Count ? idx + 1 : (idx - 1 >= 0 ? idx - 1 : -1);
        if (partner < 0)
        {
            ColumnWidths[idx] = new GridLength(Math.Max(MinColumnWidth, Math.Round(left + e.HorizontalChange)));
            return;
        }

        var partnerWidth = defs[partner].ActualWidth;
        if (partnerWidth <= 0) partnerWidth = ColumnWidths[partner].Value;
        var total = left + partnerWidth;

        // 两列合计固定：本列在 [下限, 合计−下限] 之间自由拖动，超出的部分由相邻列吸收
        var wanted = Math.Clamp(Math.Round(left + e.HorizontalChange), MinColumnWidth, Math.Max(MinColumnWidth, total - MinColumnWidth));

        ColumnWidths[idx] = new GridLength(wanted);
        ColumnWidths[partner] = new GridLength(Math.Max(MinColumnWidth, Math.Round(total - wanted)));
    }

    /// <summary>
    /// 把所有 Star（弹性）列冻结成像素列（拖拽前调用一次即可）。
    ///
    /// ⚠️ 基准宽度必须取「行内容区」的列容器实宽，绝不能用表头 Grid 实宽：
    /// 表头带比行区宽（行容器另有外边距 + 内距，且行区还要减去纵向滚动条），
    /// 用表头宽度冻结会让所有列合计超出行区宽度 → 行内容被裁剪、右侧整块"向右闪一下"
    /// （第一次拖动列宽时，右侧区域会挪动一下）。
    /// 分配按 Star 权重比例（视觉比例保持不变），最后一个弹性列吸收取整误差，
    /// 保证冻结后合计恰好等于行区宽度。
    /// </summary>
    private void FreezeStarColumns()
    {
        var defs = _headerGrid.ColumnDefinitions;
        if (defs.Count == 0 || ColumnWidths.Count != defs.Count) return;

        var baseWidth = ColumnAreaWidth();
        if (baseWidth <= 0) return;

        double weightSum = 0, fixedSum = 0;
        foreach (var w in ColumnWidths)
        {
            if (w.IsStar) weightSum += w.Value;
            else fixedSum += w.Value;
        }

        var starSpace = baseWidth - fixedSum;
        if (weightSum <= 0 || starSpace <= 0) return;

        var lastStar = -1;
        for (var i = 0; i < ColumnWidths.Count; i++)
            if (ColumnWidths[i].IsStar) lastStar = i;

        double used = 0;
        for (var i = 0; i < ColumnWidths.Count; i++)
        {
            if (!ColumnWidths[i].IsStar) continue;
            var share = i == lastStar
                ? Math.Max(MinColumnWidth, Math.Round(starSpace - used))
                : Math.Max(MinColumnWidth, Math.Round(starSpace * ColumnWidths[i].Value / weightSum));
            used += share;
            ColumnWidths[i] = new GridLength(share);
        }
    }

    /// <summary>
    /// 行内容区的列容器实宽 —— 表头列宽与弹性列冻结的唯一基准。
    /// 优先实测已实例化行内的列容器（含纵向滚动条占宽的真实结果），
    /// 无行可测时退回表头实宽（空表场景，无行可裁，不影响观感）。
    /// </summary>
    private double ColumnAreaWidth()
    {
        if (FindRowColumnGrid() is { ActualWidth: > 0 } rowGrid) return rowGrid.ActualWidth;
        if (_headerGrid.ActualWidth > 0) return _headerGrid.ActualWidth;
        return _headerGrid.ColumnDefinitions.Sum(cd => cd.ActualWidth);
    }

    /// <summary>取第一行的列容器 Grid（列数与 <see cref="ColumnWidths"/> 一致），用于实测行内容区。</summary>
    private Grid? FindRowColumnGrid()
    {
        // 工厂模式：行映射里直接取行容器的子 Grid
        foreach (var row in _rowMap.Values)
        {
            if (row.ActualWidth <= 0) continue;
            if (row.Child is Grid g && g.ColumnDefinitions.Count == ColumnWidths.Count && g.ColumnDefinitions.Count > 0)
                return g;
        }

        // 模板模式：直接在**已实现的行容器**里找（虚拟化下可视树里只有视口 ± 缓存页那些）。
        // ⚠️ 不要写成 `for (i < Items.Count) ContainerFromIndex(i)`：那是按数据项逐个问容器，
        //    在万级数据上每次表头对齐都要空转上万次（且容易把"未实现"误当"没有行"）。
        return FindColumnGrid(_rowsList);
    }

    private Grid? FindColumnGrid(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid g && g.ColumnDefinitions.Count == ColumnWidths.Count && g.ColumnDefinitions.Count > 0)
                return g;
            if (FindColumnGrid(child) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>
    /// 让表头列区与行内容区在水平方向严格重合（宽度 + 左缘）。
    /// 目的有二：① 表头分隔线正好压在行内列边界上（拖拽所见即所得）；
    /// ② 冻结基准与真实行宽一致，杜绝拖拽瞬间行区溢出（"右侧向右闪一下"）。
    /// 行区宽度受纵向滚动条影响会变化，故在行区尺寸变化/重新渲染后都重新对齐。
    /// </summary>
    private void AlignHeaderToRows()
    {
        var rowGrid = FindRowColumnGrid();

        if (rowGrid is not { ActualWidth: > 0 })
        {
            // 无行可测：恢复表头自适应（避免残留上一次的固定宽度）
            if (!double.IsNaN(_headerGrid.Width))
            {
                _headerGrid.Width = double.NaN;
                _headerGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                _headerGrid.Margin = new Thickness(0);
            }
            return;
        }

        try
        {
            var rowLeft = rowGrid.TransformToAncestor(this).Transform(new Point(0, 0)).X;
            var headLeft = _headerGrid.TransformToAncestor(this).Transform(new Point(0, 0)).X;
            var shift = Math.Max(0, _headerGrid.Margin.Left + (rowLeft - headLeft));

            _headerGrid.HorizontalAlignment = HorizontalAlignment.Left;
            _headerGrid.Width = rowGrid.ActualWidth;
            if (Math.Abs(_headerGrid.Margin.Left - shift) > 0.5)
                _headerGrid.Margin = new Thickness(Math.Round(shift, 1), 0, 0, 0);
        }
        catch (InvalidOperationException)
        {
            // 尚未接入可视树/布局未完 —— 跳过，等下一次尺寸变化再对齐
        }
    }

    /// <summary>延后到布局结束后再对齐（行容器是布局期生成的，必须等它落地）。</summary>
    private void ScheduleHeaderAlignment()
        => Dispatcher.BeginInvoke(new Action(AlignHeaderToRows), DispatcherPriority.Loaded);

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

        // 行模式变化 → 行容器水平内距/滚动条状态可能变化，重新对齐表头列区
        ScheduleHeaderAlignment();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not SortableHeaderButton header) return;
        // 与"生效排序"比较：未显式设过排序时第一列就是当前排序列，点它应该是切方向而不是跳成升序
        if (header.Field == EffectiveSortField) SortAscending = !SortAscending;
        else { SortField = header.Field; SortAscending = true; }
        SortChanged?.Invoke(this, new SortEventArgs { Field = SortField, Ascending = SortAscending });
    }

    private void OnSortChanged()
    {
        // 表头指示器始终指向"生效排序"列：调用方没设排序时落在第一个可排序列（见 EffectiveSortField）
        var activeField = EffectiveSortField;
        foreach (var header in _headerButtons)
            header.Direction = header.Field == activeField ? SortAscending : null;

        // 内部排序只在默认工厂模式 + 列有 SortKey 时执行；模板模式（外部 VM 排序）不重排行
        // preserveView = true：重排同一批数据，保留选中与滚动位置
        if (!IsTemplateMode && ColumnList.Any(c => c.Field == activeField && c.SortKey != null))
            RenderRows(preserveView: true);
    }

    private IEnumerable<object> SortedItems()
    {
        var items = ItemsSource?.Cast<object>() ?? Array.Empty<object>();
        var column = ColumnList.FirstOrDefault(c => c.Field == EffectiveSortField);
        if (column?.SortKey == null) return items;
        return SortAscending
            ? items.OrderBy(column.SortKey)
            : items.OrderByDescending(column.SortKey);
    }

    /// <summary>
    /// 生效排序字段（架构保证）：调用方未设 SortField 时，自动落到第一个可排序列（升序）。
    /// 这样任何使用方都不可能渲染出"一行三角形都没有"的表格——排序永远存在且表头可见；
    /// 用局部推导而不是回写 DP，避免触发 DP 回调造成递归渲染。
    /// </summary>
    public string EffectiveSortField
        => !string.IsNullOrEmpty(SortField)
            ? SortField
            : ColumnList.FirstOrDefault(c => c.SortKey != null)?.Field ?? string.Empty;

    private void RenderRows(bool preserveView = false)
    {
        if (IsTemplateMode) return; // 模板模式行由 ItemsSource 直通驱动

        // 排序只是重排同一批数据：应保留选中行与滚动位置（否则"点一下表头，选中没了、详情栏还在"）
        var previousSelection = preserveView ? _paintedSelection.ToList() : null;
        var previousOffset = preserveView ? (RowsScroller?.VerticalOffset ?? 0) : 0;
        var keepView = preserveView && RowHasData();

        var items = SortedItems().ToList();
        _rowMap.Clear();
        _paintedSelection.Clear();
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
            ScheduleHeaderAlignment(); // 无行可测 → 表头恢复自适应宽度
            return;
        }

        _emptyHost.Content = null;
        _emptyHost.Visibility = Visibility.Collapsed;
        _rowsList.ItemsSource = items.Select(BuildRow).ToList();
        ScheduleHeaderAlignment(); // 行重建 → 行区宽度/滚动条状态可能变化，表头列区需重新对齐

        if (!keepView) return;

        // 选中集合在新行列表里找回并恢复高亮（覆盖式：只恢复仍在数据里的项）
        if (previousSelection != null)
            ApplySelection(previousSelection.Where(item => _rowMap.ContainsKey(item)));

        // 滚动位置恢复要等新行完成布局（虚拟化下偏移单位是像素，直接设回去即可）
        if (previousOffset > 0)
        {
            var target = previousOffset;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => RowsScroller?.ScrollToVerticalOffset(target)));
        }
    }

    /// <summary>当前是否已有数据行（用于判断"重排"而非"换数据"）。</summary>
    private bool RowHasData()
    {
        foreach (var item in _rowsList.Items)
            if (item != null) return true;
        return false;
    }

    private Border BuildRow(object item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(8, 1, 8, 1),
            Padding = new Thickness(16, 9, 16, 9),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            // 行容器约定（BlankClick）：带字符串 Tag 的行 = "行内空白属于行"——
            // 挂在区域上的"点空白清选中"不会把点行误判成点空白（与 BrowserRow / TrashRow 同约定）
            Tag = "DataRow"
        };
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Triggers.Add(new Trigger
        {
            Property = IsMouseOverProperty, Value = true,
            // 悬停底色同样走**资源引用**（Setter 里的 DynamicResource）：一次性取的画刷会被固化，
            // 换主题后悬停色仍停在旧主题。
            // ⚠️ 取**卡面那一支**悬停（`App.Surface.CardHover`）：行画在卡面上，悬停必须是"这块卡加深一档"。
            //    页面底那支 `App.Surface.Hover` 比卡面深 8 档、色相也不同 ⇒ 悬停看着像"跳了一块别的颜色"，
            //    而且比选中底还深（悬停压过选中）。
            Setters = { new Setter(BackgroundProperty, new DynamicResourceExtension("App.Surface.CardHover")) }
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

        // ⚠️ 必须登记行映射：SelectItem/ClearSelection/排序后恢复选中全靠它查回行容器。
        // 缺了这行 → 选中态画不出来（表现为"点了行没有任何反馈"），且排序后无法恢复选中。
        _rowMap[item] = row;

        return row;
    }

    /// <summary>选中某一项（单选；更新内部绘制；不匹配的项恢复默认态。仅默认工厂模式）。
    /// <see cref="SelectionEnabled"/> 关闭时整体无操作——外部托管表请用 <see cref="ApplySelection"/>。</summary>
    public void SelectItem(object item)
    {
        if (IsTemplateMode || !SelectionEnabled) return;
        ApplySelection(new[] { item });
    }

    /// <summary>清除选中（仅默认工厂模式）。</summary>
    public void ClearSelection()
    {
        if (IsTemplateMode) return;
        ApplySelection(Array.Empty<object>());
    }

    /// <summary>
    /// **外部托管模式的选中绘制**（与 <see cref="SelectionEnabled"/>=false 配套）：把绘制集合整体
    /// 替换为 <paramref name="items"/>，其余行复位。页面 VM 的 `ListSelection` 是选中的唯一事实来源，
    /// 本方法只是它的**投影**——每次调用覆盖式更新（铁律 9），绝不累积。
    /// </summary>
    public void ApplySelection(IEnumerable<object> items)
    {
        if (IsTemplateMode) return;
        foreach (var old in _paintedSelection) ResetRowBackground(old);
        _paintedSelection.Clear();
        SelectedItem = null;
        foreach (var item in items)
        {
            _paintedSelection.Add(item);
            SelectedItem ??= item;
            if (_rowMap.TryGetValue(item, out var row))
                row.SetResourceReference(Border.BackgroundProperty, "App.Surface.Selected");
        }
    }

    /// <summary>
    /// 容器实现状态变化 → 把**当前选中集合**重投影到已实现的行容器上（虚拟化的连带项，见构造函数注释）。
    /// 只对工厂模式有意义（模板模式的行选中由行模板自己的绑定投影）。
    /// </summary>
    private void RepaintSelectionOnRealizedContainers()
    {
        if (IsTemplateMode) return;
        if (_rowsList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;

        // 逐项是幂等的（覆盖式重设同一个资源引用）；没挂进可视树的行设了也无害——挂上时自然带着它。
        foreach (var item in _paintedSelection)
            if (_rowMap.TryGetValue(item, out var row))
                row.SetResourceReference(BackgroundProperty, "App.Surface.Selected");
    }

    /// <summary>当前**视觉顺序**的数据项（含当前排序）——外部托管页面据此做 ↑/↓、Ctrl+A。</summary>
    public IReadOnlyList<object> OrderedItems() => SortedItems().ToList();

    /// <summary>把某数据行滚入视口（移动选中 / 定位后保证可见）。</summary>
    /// <remarks>
    /// 模板模式下**不依赖行容器已实现**（虚拟化后视口外的行没有容器）：
    /// 先按数据索引与平均行高估算一个偏移把目标滚进大致位置，再让真实容器自己 <c>BringIntoView</c> 对齐。
    /// 工厂模式仍走行映射（那些行本来就是控件、必然在）。
    /// </remarks>
    public void ScrollItemIntoView(object item)
    {
        // 已实现（挂进可视树）的行：让它自己进来。虚拟化下"有行对象"不等于"已实现"（工厂模式的行对象常驻、
        // 但可能没挂进可视树，此时 BringIntoView 是空操作），故判据落在 IsVisible 上。
        if (_rowMap.TryGetValue(item, out var row) && row.IsVisible)
        {
            row.BringIntoView();
            return;
        }

        var index = _rowsList.Items.IndexOf(item);
        if (index < 0) return;

        var scroller = RowsScroller;
        if (scroller == null) return;

        if (_rowsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement realized && realized.IsVisible)
        {
            realized.BringIntoView();
            return;
        }

        // 未实现：按平均行高估算滚动（行高由内容定，量出来比猜常量可靠），随后容器已生成即对齐。
        var count = _rowsList.Items.Count;
        if (count <= 0 || scroller.ExtentHeight <= 0) return;
        var average = scroller.ExtentHeight / count;
        scroller.ScrollToVerticalOffset(Math.Max(0, (index - 2) * average));
        _rowsList.UpdateLayout();
        (_rowsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)?.BringIntoView();
    }

    /// <summary>
    /// 复位某行的选中底色：用 ClearValue 而不是赋 Transparent —— 本地值会压过样式触发器，
    /// 赋过 Transparent 之后该行的悬停高亮就永久失效了。
    /// </summary>
    /// <remarks>选中底色现在由 <c>SetResourceReference</c> 挂上（资源引用同样算本地值），
    /// <c>ClearValue</c> 一样能整条摘掉并回落到样式触发器 —— 与旧的"赋值 + ClearValue"行为一致
    /// （实测：<c>Border.BackgroundProperty</c> 与这里的 <c>BackgroundProperty</c> 是同一个 DP 实例）。</remarks>
    private void ResetRowBackground(object? item)
    {
        if (item == null) return;
        if (_rowMap.TryGetValue(item, out var row))
            row.ClearValue(BackgroundProperty);
    }
}

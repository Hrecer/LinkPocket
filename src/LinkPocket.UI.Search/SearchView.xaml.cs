using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.ViewModels;
using Material3.Wpf;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.Views;

/// <summary>
/// 搜索页视图（MVVM）：只做视图层装配——
/// 结果表列定义/单元格工厂/空态渲染/关键词高亮在这里，查询执行、范围守卫、
/// 选中态与页面动作命令全部在 <see cref="SearchViewModel"/>（构造注入组合根）。
/// </summary>
public partial class SearchView : UserControl
{
    private SearchViewModel? _vm;
    private ShortcutHost? _shortcutHost;

    /// <summary>首帧紧凑行模板 / 完整行模板（切换只影响容器实例化，不改变数据与选中）。</summary>
    private DataTemplate? _compactRowTemplate;
    private DataTemplate? _fullRowTemplate;
    private int _rowTemplateGen;

    private double _lastVerticalOffset;

    /// <summary>活结果集结构变化前的阅读锚点：第一条可见行 + 行内像素偏移。</summary>
    private string? _liveAnchorKey;
    private int _liveAnchorIndex;
    private double _liveAnchorOffset;
    private double _liveAnchorAverage;
    private bool _liveAnchorCaptured;

    public SearchView()
    {
        InitializeComponent();
        _fullRowTemplate = ResultsTable.ItemTemplate;
        _compactRowTemplate = (DataTemplate)Resources["SearchRowCompactTemplate"];
        // 预热行模板（建一个脱离可视树的实例并走一遍 Measure/Arrange）：
        // 首次搜索结果落地时少付一次 BAML/JIT 与文本排版预热。紧凑模板也要预热——
        // 首帧走的就是它，否则第一次搜索会多付一次模板 JIT（实测首查比复跑慢 ~14ms）。
        WarmRowTemplate(ResultsTable.ItemTemplate);
        WarmRowTemplate(_compactRowTemplate);
        ResultsTable.VerticalOffsetChanged += (_, offset) =>
        {
            _lastVerticalOffset = offset;
            RequestVisiblePriority(offset);
        };
        SizeChanged += (_, _) => UpdatePrefetchRows();
        DataContextChanged += (_, _) =>
        {
            if (ReferenceEquals(_vm, DataContext)) return;
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.RefreshCompleted -= OnRefreshCompleted;
                _vm.LiveUpdateStarting -= OnLiveUpdateStarting;
                _vm.LiveUpdateCompleted -= OnLiveUpdateCompleted;
                _vm.Results.CollectionChanged -= OnResultsCollectionChanged;
                _vm.ResultSetReplaced -= OnResultSetReplaced;
            }
            _vm = DataContext as SearchViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.LiveUpdateStarting += OnLiveUpdateStarting;
                _vm.LiveUpdateCompleted += OnLiveUpdateCompleted;
                _vm.Results.CollectionChanged += OnResultsCollectionChanged;
                _vm.ResultSetReplaced += OnResultSetReplaced;
                _vm.ResetRequested += (_, _) => SearchBox.Focus();
                _vm.FocusRowRequested += (_, item) => ResultsTable.ScrollItemIntoView(item);
                // 点空白 = 清选中 + 焦点收回页内（BlankClick 唯一实现；命令端在视图收口——
                // "把焦点收回页内"是视图职责，与浏览页 ClearPageSelection/ActivatePane 同口径）
                BlankClick.SetCommand(ContentArea, new RelayCommand(() =>
                {
                    _vm.ClearSelectionCommand.Execute(null);
                    PageFocus.Restore(this);
                }));
                // F5 真刷新结束：播行入场动画（唯一实现 = UIKit RowEntrance）+ 守住"焦点在页内"不变式
                _vm.RefreshCompleted += OnRefreshCompleted;
                SetupTable(_vm);
                UpdatePrefetchRows();
                // 迟挂的 DataContext：把 VM 当前的空态/结果同步到表上
                ApplyEmptyState();
                ApplyResults();

                // 快捷键：键位在 ShortcutCatalog（本页 = 搜索框内 Enter + 结果列表的完整集；
                // 搜索框内 Enter 属控件锚定，其余为页面级 —— 页面只做「动作 id → 命令」映射）
                _shortcutHost?.Detach();
                var commands = new ShortcutCommandMap()
                    .Add(ShortcutAction.SearchRun, _vm.SearchCommand)
                    .Add(ShortcutAction.SearchMoveUp, _vm.MoveSelectionCommand)
                    .Add(ShortcutAction.SearchMoveDown, _vm.MoveSelectionCommand)
                    .Add(ShortcutAction.SearchSelectLast, _vm.SelectLastCommand)
                    .Add(ShortcutAction.SearchSelectAll, _vm.SelectAllCommand)
                    // Enter = 打开该链接的**浏览页详情页**（与智能列表/去重明细统一）；
                    // 「跳转」（进目录 + 选中行）是顶部药丸 / 右栏图标钮的语义，键位不承担。
                    .Add(ShortcutAction.SearchOpen, _vm.OpenDetailCommand)
                    .Add(ShortcutAction.SearchDelete, _vm.DeleteSelectionCommand)
                    .Add(ShortcutAction.SearchRefresh, _vm.RefreshCommand)
                    .Add(ShortcutAction.SearchEscape, _vm.EscapeCommand);
                _shortcutHost = new ShortcutHost(ShortcutCatalog.Build(ShortcutPage.Search, commands), () => ShortcutScope.Search);
                _shortcutHost.Attach(this);
                _shortcutHost.AttachControls(ShortcutPage.Search, this, commands);
                PageRefresh.Register(this, _vm.RefreshCommand);   // 右键「刷新」= F5 同一个命令
            }
        };
    }

    // —— 表格装配：列定义 = 数据 + 排序键 + 单元格工厂，表头/行/排序全部由控件驱动 ——

    /// <summary>首屏预取 = 视口行数 ×2，下限 64。行高用 Phase 0 实测的 57.9 DIP 估算；
    /// 这里只决定“预取多少行”，不影响可达性，也不需要精确到像素。</summary>
    private void UpdatePrefetchRows()
    {
        if (_vm == null || ResultsTable.ActualHeight <= 0) return;
        var viewportRows = Math.Max(1, (int)Math.Ceiling(ResultsTable.ActualHeight / 58.0));
        _vm.PrefetchRows = Math.Max(64, viewportRows * 2);
    }

    /// <summary>把滚动偏移换算成行号（视口中心），通知 VM 优先补这一页。
    /// 只影响取页顺序，不影响结果集范围/排序；预取之外的行仍是同高占位行。</summary>
    private void RequestVisiblePriority(double offset)
    {
        if (_vm == null) return;
        var count = _vm.Results.Count;
        var extent = ResultsTable.ExtentHeight;
        if (count <= 0 || extent <= 0) return;
        var viewport = ResultsTable.ViewportHeight;
        var center = offset + (viewport > 0 ? viewport / 2 : 0);
        var row = (int)(Math.Clamp(center / extent, 0, 1) * count);
        _vm.RequestVisibleRow(row);
    }

    private void SetupTable(SearchViewModel vm)
    {
        ResultsTable.SortField = "title";   // 默认名称升序（与主栏一致，表头初始即显示 ▲）
        ResultsTable.SortAscending = true;
        ResultsTable.Columns = new[]
        {
            new DataTableColumn
            {
                // 名称列占 2 份剩余空间：标题下方还有 URL，必须留出可见宽度，空间来自右侧四列压到极限
                Field = "title", LabelKey = "ui.noun.name", Width = -2,
                SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                    ? ((LinkItem)r).Url : ((LinkItem)r).Title),
            },
            new DataTableColumn
            {
                // 位置列占 3 份剩余空间（名称 2 份）：层级路径最长、最需要宽度；
                // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                // 的实测宽 105 + 9 列间余量（渲染检查实测值；改小会截断成省略号，或让相邻列贴在一起）
                // 省下的宽度全部让给名称/位置，URL 不得被压缩
                Field = "path", LabelKey = "ui.noun.location", Width = -3,
                SortKey = r => (IComparable)vm.ResolveFolderPath(((LinkItem)r).ListId).Resolve(),
            },
            new DataTableColumn
            {
                Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
            },
            new DataTableColumn
            {
                Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 114,
                SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
            },
            new DataTableColumn
            {
                Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 72,
                SortKey = r => (IComparable)((LinkItem)r).VisitCount,
            },
            new DataTableColumn
            {
                Field = "created_at", LabelKey = "ui.noun.createdAt", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
            },
        };

        // 模板模式下控件不内部排序（行由 DataTemplate 呈现）：表头点击 → VM 交引擎重算，
        // 首屏 + 后台补齐保持同一排序口径（十万条不做本地整表排序）。
        ResultsTable.SortChanged += (_, e) => _ = vm.SortAsync(e.Field, e.Ascending);

        // 外部托管选中（SelectionEnabled=False）：点击按修饰键路由（Ctrl 翻转 / Shift 区间），
        // 绘制统一走 ApplySelectionPaint（VM 的 ListSelection 是唯一事实来源）。
        // 顺序直接用结果集当前顺序（引擎已排好），不要再对十万条做 LINQ 排序。
        vm.OrderProvider = () => vm.OrderedLinkIds;
    }

    private static void WarmRowTemplate(System.Windows.DataTemplate? template)
    {
        if (template == null) return;
        var warm = new LinkItem
        {
            LinkId = "__warm__",
            Title = "Warmup",
            Url = "https://warmup.invalid/",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        for (var i = 0; i < 12; i++)
        {
            var presenter = new ContentPresenter
            {
                Content = warm,
                ContentTemplate = template,
                Width = 1200,
                Height = 60,
            };
            presenter.Measure(new Size(1200, 60));
            presenter.Arrange(new Rect(0, 0, 1200, 60));
            presenter.UpdateLayout();
        }
    }

    // —— VM 状态 → 视图渲染 ——

    /// <summary>用户发起的刷新（F5）结束：播行入场动画（UIKit 唯一实现）并把焦点收回页内。</summary>
    private void OnRefreshCompleted(object? sender, EventArgs e)
    {
        RowEntrance.Play(ResultsTable.RowsList);
        PageFocus.Restore(this);
    }

    /// <summary>增量插入/删除/移动前：按“平均行高 = Extent/Count”换算出第一条可见行与行内偏移。
    /// 搜索行高固定，因此结构变化完成后按同一行号重算像素偏移即可把同一内容留在原处。</summary>
    private void OnLiveUpdateStarting(object? sender, EventArgs e)
    {
        _liveAnchorCaptured = false;
        if (_vm == null || !IsVisible || _vm.Results.Count == 0) return;

        var count = _vm.Results.Count;
        var average = ResultsTable.ExtentHeight > 0 ? ResultsTable.ExtentHeight / count : 58.0;
        if (average <= 0) average = 58.0;

        var index = (int)Math.Clamp(Math.Floor(ResultsTable.VerticalOffset / average), 0, count - 1);
        _liveAnchorKey = _vm.Results[index].Key;
        _liveAnchorIndex = index;
        _liveAnchorOffset = ResultsTable.VerticalOffset - index * average;
        _liveAnchorAverage = average;
        _liveAnchorCaptured = true;
    }

    private void OnLiveUpdateCompleted(object? sender, EventArgs e)
    {
        if (_vm == null || !_liveAnchorCaptured) return;
        _liveAnchorCaptured = false;

        var key = _liveAnchorKey;
        var oldIndex = _liveAnchorIndex;
        var within = _liveAnchorOffset;
        var fallbackAverage = _liveAnchorAverage;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_vm == null) return;
            var newIndex = string.IsNullOrEmpty(key) ? -1 : _vm.IndexOfRowKey(key);
            if (newIndex < 0) newIndex = Math.Min(oldIndex, _vm.Results.Count - 1);
            if (newIndex < 0) return;

            var count = _vm.Results.Count;
            var average = ResultsTable.ExtentHeight > 0 && count > 0
                ? ResultsTable.ExtentHeight / count
                : fallbackAverage;
            if (average <= 0) average = 58.0;

            var max = Math.Max(0, ResultsTable.ExtentHeight - ResultsTable.ViewportHeight);
            ResultsTable.ScrollToVerticalOffset(Math.Clamp(newIndex * average + within, 0, max));
        }));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.Results):
                ApplyResults();
                break;
            case nameof(SearchViewModel.EmptyState):
                ApplyEmptyState();
                break;
            case nameof(SearchViewModel.SelectedItems):
                ApplySelectionPaint();
                break;
        }
    }

    private void ApplyResults()
    {
        ResultsTable.ItemsSource = _vm?.Results;
        _paintedSelected.Clear();
        ApplySelectionPaint();   // 行重建后把选中集合重新投影（外部托管：绘制随 ItemsSource 重建清零）
    }

    /// <summary>
    /// 新结果集挂上后：先换**紧凑行模板**（标题 + URL，真实数据、同高），让首帧只实例化两个文本块；
    /// 下一帧（Loaded 优先级，排在渲染之后）恢复完整模板补全位置/日期/图标/高亮。
    /// 只影响行的视觉实例化，不动集合、数据、选中与滚动范围。
    /// </summary>
    private void OnResultSetReplaced(object? sender, EventArgs e)
    {
        if (_compactRowTemplate is null || _fullRowTemplate is null) return;
        var gen = ++_rowTemplateGen;
        ResultsTable.ItemTemplate = _compactRowTemplate;
        RestoreFullTemplateAfterFirstFrame(gen);
    }

    /// <summary>等紧凑行真的落地并渲染出一帧后再恢复完整模板（与浏览页同一判据）：
    /// 挂在 Loaded/Background 会抢在首帧之前执行，首帧又背上完整模板的布局。</summary>
    private void RestoreFullTemplateAfterFirstFrame(int gen)
    {
        var restored = false;
        var compactLaidOut = false;
        EventHandler? onRendered = null;
        EventHandler? onLayout = null;
        System.Windows.Threading.DispatcherTimer? fallback = null;

        void Restore()
        {
            if (restored) return;
            restored = true;
            if (onRendered is not null) CompositionTarget.Rendering -= onRendered;
            if (onLayout is not null) ResultsTable.LayoutUpdated -= onLayout;
            fallback?.Stop();
            if (gen != _rowTemplateGen || _fullRowTemplate is null) return;
            ResultsTable.ItemTemplate = _fullRowTemplate;
        }

        onLayout = (_, _) =>
        {
            // 无命中时没有行容器可等：空态本身已经布局完成，按“紧凑帧已落地”处理。
            if (!HasCompactRow(ResultsTable) && ResultsTable.RowsList.Items.Count != 0) return;
            compactLaidOut = true;
            if (onLayout is not null) ResultsTable.LayoutUpdated -= onLayout;
        };
        ResultsTable.LayoutUpdated += onLayout;

        onRendered = (_, _) =>
        {
            if (!compactLaidOut) return;
            CompositionTarget.Rendering -= onRendered;
            fallback?.Stop();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Restore));
        };
        CompositionTarget.Rendering += onRendered;
        fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        fallback.Tick += (_, _) => Restore();
        fallback.Start();
    }

    private static bool HasCompactRow(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "SearchRowCompact" }) return true;
            if (HasCompactRow(child)) return true;
        }
        return false;
    }

    /// <summary>结果集合被整批替换（Reset）后，旧的选中投影已失效：重投影到新行对象。</summary>
    private void OnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        _paintedSelected.Clear();
        ApplySelectionPaint();
        // 刷新（F5 / 范围切换 / 数据变更）保留阅读位置；**新查询**回到顶部——
        // 否则上一次滚到的偏移会把新结果集直接打开在中段。
        if (_lastVerticalOffset > 0 && _vm is { PreserveScrollOnNextResultReplace: true })
        {
            var offset = _lastVerticalOffset;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => ResultsTable.ScrollToVerticalOffset(offset)));
        }
        else
        {
            _lastVerticalOffset = 0;
        }
    }

    /// <summary>行点击：带修饰键路由到 VM（Ctrl 翻转 / Shift 区间），双击打开详情页（与工厂模式同口径）。</summary>
    private void SearchRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LinkItem item } || _vm == null) return;
        _vm.ClickItem(item, Keyboard.Modifiers);
        if (e.ClickCount == 2)
            _vm.OpenDetailCommand.Execute(null);
    }

    private void ApplyEmptyState()
    {
        if (_vm == null) return;
        if (_vm.EmptyState != null)
            ResultsTable.EmptyContent = BuildSearchState(_vm.EmptyState.IconKind, _vm.EmptyState.Title,
                _vm.EmptyState.Subtitle, _vm.EmptyState.ContainerBrush, _vm.EmptyState.OnContainerBrush);
    }

    /// <summary>已画过选中的行（只记选中项，不遍历全部结果）：十万条下每次点选仍只改变化的几个。</summary>
    private readonly Dictionary<string, LinkItem> _paintedSelected = new(StringComparer.Ordinal);

    /// <summary>选中投影（**外部托管**）：把 VM 的选中集合差异投影到行模型的 IsSelected。
    /// 只有视口附近的容器订阅该属性；未实现的选中行在滚入视口时直接带着标志绑定。</summary>
    private void ApplySelectionPaint()
    {
        if (_vm == null) return;
        var current = _vm.Selection.Ids.ToHashSet(StringComparer.Ordinal);

        foreach (var (id, item) in _paintedSelected)
            if (!current.Contains(id)) item.IsSelected = false;

        foreach (var id in current)
            if (!_paintedSelected.ContainsKey(id) && _vm.FindItem(id) is { } item)
                item.IsSelected = true;

        _paintedSelected.Clear();
        foreach (var id in current)
            if (_vm.FindItem(id) is { } item) _paintedSelected[id] = item;
    }

    // —— 渲染原语（纯视图） ——

    /// <summary>MD3E 空状态视图：大圆角色块徽章 + 引导性文案（替代生硬的系统提示）。</summary>
    private FrameworkElement BuildSearchState(string iconKind, LocValue title, LocValue? subtitle,
        string containerBrush, string onContainerBrush)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 56, 0, 0) };
        var badge = new Border
        {
            Width = 96, Height = 96, CornerRadius = new CornerRadius(32),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        badge.SetResourceReference(Border.BackgroundProperty, containerBrush);
        var badgeIcon = new M3Icon
        {
            Kind = iconKind, Width = 40, Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        badgeIcon.SetResourceReference(TextElement.ForegroundProperty, onContainerBrush);
        badge.Child = badgeIcon;
        sp.Children.Add(badge);
        var stateTitle = new TextBlock
        {
            FontSize = 17, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0)
        };
        stateTitle.SetText(title);
        stateTitle.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
        sp.Children.Add(stateTitle);
        if (subtitle != null)
        {
            var stateSubtitle = new TextBlock
            {
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                MaxWidth = 420, TextAlignment = TextAlignment.Center
            };
            stateSubtitle.SetText(subtitle.Value);
            stateSubtitle.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            sp.Children.Add(stateSubtitle);
        }
        return sp;
    }

}

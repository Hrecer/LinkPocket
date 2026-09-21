using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
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

    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ReferenceEquals(_vm, DataContext)) return;
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.RefreshCompleted -= OnRefreshCompleted;
            }
            _vm = DataContext as SearchViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
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
            }
        };
    }

    // —— 表格装配：列定义 = 数据 + 排序键 + 单元格工厂，表头/行/排序全部由控件驱动 ——

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
                CellFactory = r => BuildSearchNameCell((LinkItem)r)
            },
            new DataTableColumn
            {
                // 位置列占 3 份剩余空间（名称 2 份）：层级路径最长、最需要宽度；
                // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                // 的实测宽 105 + 9 列间余量（探针实测值；改小会截断成省略号，或让相邻列贴在一起）
                // 省下的宽度全部让给名称/位置，URL 不得被压缩
                Field = "path", LabelKey = "ui.noun.location", Width = -3,
                SortKey = r => (IComparable)vm.ResolveFolderPath(((LinkItem)r).ListId).Resolve(),
                CellFactory = r => TextCell(vm.ResolveFolderPath(((LinkItem)r).ListId), 12.5)
            },
            new DataTableColumn
            {
                Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
                CellFactory = r => TextCell(UiClock.Text(((LinkItem)r).UpdatedAt.ToLocalTime()), 12.5)
            },
            new DataTableColumn
            {
                Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 114,
                SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
                CellFactory = r => ((LinkItem)r).LastVisitedAt is { } visited
                    ? TextCell(UiClock.Text(visited.ToLocalTime()), 12.5)
                    : TextCell(Loc.K("clock.never"), 12.5)
            },
            new DataTableColumn
            {
                Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 72,
                SortKey = r => (IComparable)((LinkItem)r).VisitCount,
                CellFactory = r => TextCell(Loc.K("count.viewsN", ((LinkItem)r).VisitCount), 12.5)
            },
            new DataTableColumn
            {
                Field = "created_at", LabelKey = "ui.noun.createdAt", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
                CellFactory = r => TextCell(UiClock.Text(((LinkItem)r).CreatedAt.ToLocalTime()), 12.5)
            },
        };

        // 外部托管选中（SelectionEnabled=False）：点击按修饰键路由（Ctrl 翻转 / Shift 区间），
        // 绘制统一走 ApplySelectionPaint（VM 的 ListSelection 是唯一事实来源）。
        vm.OrderProvider = () => ResultsTable.OrderedItems().OfType<LinkItem>().Select(i => i.LinkId).ToList();
        ResultsTable.RowClick += (_, item) => vm.ClickItem((LinkItem)item, Keyboard.Modifiers);
        ResultsTable.RowDoubleClick += (_, item) =>
        {
            vm.ClickItem((LinkItem)item, ModifierKeys.None);
            vm.OpenDetailCommand.Execute(null);   // 双击 = 打开详情页（跳转只由顶部药丸 / 右栏图标钮触发）
        };
    }

    // —— VM 状态 → 视图渲染 ——

    /// <summary>用户发起的刷新（F5）结束：播行入场动画（UIKit 唯一实现）并把焦点收回页内。</summary>
    private void OnRefreshCompleted(object? sender, EventArgs e)
    {
        RowEntrance.Play(ResultsTable.RowsList);
        PageFocus.Restore(this);
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
        ApplySelectionPaint();   // 行重建后把选中集合重新投影（外部托管：绘制随 ItemsSource 重建清零）
    }

    private void ApplyEmptyState()
    {
        if (_vm == null) return;
        if (_vm.EmptyState != null)
            ResultsTable.EmptyContent = BuildSearchState(_vm.EmptyState.IconKind, _vm.EmptyState.Title,
                _vm.EmptyState.Subtitle, _vm.EmptyState.ContainerBrush, _vm.EmptyState.OnContainerBrush);
        if (_vm.Results == null)
            ResultsTable.ItemsSource = null;
    }

    /// <summary>选中投影（**外部托管**）：把 VM 的选中集合整体画到表上——覆盖式更新（铁律 9），绝不累积。</summary>
    private void ApplySelectionPaint()
    {
        if (_vm == null) return;
        ResultsTable.ApplySelection(_vm.SelectedItems.Cast<object>());
    }

    // —— 渲染原语（纯视图） ——

    /// <summary>名称列：favicon + 标题 + URL 副行（关键词高亮）。</summary>
    private FrameworkElement BuildSearchNameCell(LinkItem item)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal };

        var iconGrid = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var faviconBmp = TryLoadFavicon(item.FaviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
        if (faviconBmp == null) faviconImg.Visibility = Visibility.Collapsed;
        var earthIcon = new M3Icon
        {
            Kind = "earth",
            Width = 16, Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // 颜色一律走**资源引用**：一次性 FindResource 取画刷赋值 = 换主题后停在旧主题（表头同根因）
        earthIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Muted");
        if (faviconBmp != null) earthIcon.Visibility = Visibility.Collapsed;
        iconGrid.Children.Add(faviconImg);
        iconGrid.Children.Add(earthIcon);

        if (!string.IsNullOrWhiteSpace(item.FaviconUrl) && faviconBmp == null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FaviconService.PrefetchAndCacheAsync(item.FaviconUrl);
                    var cached = FaviconService.LoadFromCache(item.FaviconUrl);
                    if (cached != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            faviconImg.Source = cached;
                            faviconImg.Visibility = Visibility.Visible;
                            earthIcon.Visibility = Visibility.Collapsed;
                        });
                    }
                }
                catch { }
            });
        }

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var displayTitle = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url;
        var titleBlock = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
        AddHighlightedRuns(titleBlock, displayTitle, _vm?.LastQuery ?? "", "App.Text.Primary");
        textStack.Children.Add(titleBlock);

        var urlBlock = new TextBlock
        {
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        };
        urlBlock.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
        AddHighlightedRuns(urlBlock, item.Url, _vm?.LastQuery ?? "", "App.Text.Secondary");
        textStack.Children.Add(urlBlock);

        panel.Children.Add(iconGrid);
        panel.Children.Add(textStack);
        return panel;
    }

    /// <summary>数据单元格（时间戳 / 路径这类用户数据，永不翻译）。</summary>
    private static TextBlock TextCell(string text, double fontSize)
    {
        var tb = BuildCell(fontSize);
        tb.Text = text;
        return tb;
    }

    /// <summary>
    /// 两个长度形态的单元格（日期这类**结构化列**）：走 <c>LocFit</c> 的降级链——
    /// 放不下时换短式（去年份），最后才截断；<b>绝不缩字号</b>（同行字号必须一致，见 UI-SPEC §3）。
    /// </summary>
    private static TextBlock TextCell(LinkPocket.I18n.LocText text, double fontSize)
    {
        var tb = BuildCell(fontSize);
        LocFit.SetMode(tb, LocFitMode.ShrinkThenEllipsis);
        LocFit.SetText(tb, text);
        return tb;
    }

    /// <summary>文案单元格（键 + 参数；语言一变自己重算）。</summary>
    private static TextBlock TextCell(LocValue text, double fontSize)
    {
        var tb = BuildCell(fontSize);
        tb.SetText(text);
        return tb;
    }

    /// <summary>普通文本单元格（表格化信息列统一规格）。</summary>
    private static TextBlock BuildCell(double fontSize)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        tb.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
        return tb;
    }

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

    /// <summary>把命中的关键词染成强调色（大小写不敏感），其余用普通画刷。
    /// 画刷一律经**资源引用**（<paramref name="normalKey"/> = 资源键）：一次性取画刷赋值会在换主题后停在旧主题。</summary>
    private void AddHighlightedRuns(TextBlock tb, string text, string query, string normalKey)
    {
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(query))
        {
            tb.Inlines.Add(ColoredRun(text, normalKey));
            return;
        }
        var lower = text.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        var pos = 0;
        while (true)
        {
            var hit = lower.IndexOf(q, pos, StringComparison.Ordinal);
            if (hit < 0)
            {
                if (pos < text.Length)
                    tb.Inlines.Add(ColoredRun(text[pos..], normalKey));
                break;
            }
            if (hit > pos)
                tb.Inlines.Add(ColoredRun(text[pos..hit], normalKey));
            var accent = ColoredRun(text.Substring(hit, q.Length), "App.Accent.Fill");
            accent.FontWeight = FontWeights.Bold;
            tb.Inlines.Add(accent);
            pos = hit + q.Length;
        }
    }

    /// <summary>按资源键着色的 Run（Foreground 走 TextElement 附加属性 → 换主题自动跟随）。</summary>
    private static Run ColoredRun(string text, string key)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, key);
        return run;
    }

    private static BitmapImage? TryLoadFavicon(string? faviconUrl)
        => FaviconService.LoadFromCache(faviconUrl);
}

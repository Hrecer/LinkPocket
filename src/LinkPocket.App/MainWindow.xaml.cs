using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using LinkPocket.Api;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;
using Material3.Wpf;

namespace LinkPocket;

public partial class MainWindow : Window, Services.IUiCoordinator, Services.IBrowserLocateHost
{
    private readonly Managers.SelectionManager _selectionManager = new();

    // 搜索页自己的选中态（老「链接」页删除后，主窗口只剩搜索页需要残余状态）
    private LinkItem? _selectedSearchItem;

    // 搜索页右侧详情栏：复用 Views/DetailSidebar 控件（与浏览页同一数据契约，解耦于 BrowserViewModel）
    private readonly SearchDetailsViewModel _searchDetails = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(_selectionManager);
        BrowserPage.DataContext = ((MainViewModel)DataContext).BrowserViewModel;
        Services.UiCoordinator.Instance = this;
        // 内容定位组件（「跳转」）的宿主注册：本窗口只提供"切到浏览页 + 进入目录并选中一行"两个原语，
        // 定位算法在 Services/ContentLocator 里——页面与工具都只依赖 IContentLocator，不直接碰窗口。
        Services.BrowserLocateHost.Current = this;
        SetupSearchTable(); // 搜索结果表：列定义 + 排序 + 行交互（完全数据驱动）
        SearchSidebar.DataContext = _searchDetails; // 搜索详情栏：同一控件，数据由 SearchDetailsViewModel 驱动
        WireSearchDetailsCommands();

        // 老「链接」页的 LinkNavigator（在旧列表里定位/展开/滚动到某条链接）随页面一并删除；
        // 搜索页的「跳转」已改为在「浏览」页直接打开该链接的详情页。
        if (DataContext is MainViewModel searchVm)
        {
            searchVm.OnNavigatedToSearch += (s, e) => ResetSearchUI();
            searchVm.OnNavigatedFromSearch += (s, e) =>
            {
                _selectedSearchItem = null;
                SearchJumpToLinkBtn.IsEnabled = false;
                _searchDetails.UpdateFrom(null, "");
            };
            searchVm.OnSearchRefreshRequested += async (s, e) =>
            {
                var query = SearchBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(query))
                    await ExecuteTitleSearchAsync(searchVm, query);
            };
        }
        Loaded += MainWindow_Loaded;
        StateChanged += Window_StateChanged;
        SizeChanged += (_, _) => UpdateShellClip();
        // 分段胶囊导航：CurrentNavId 变化时让选中药丸滑过去（弹簧曲线）
        DataContextChanged += (_, _) => HookNavPillDriver();
        HookNavPillDriver();
    }

    #region IUiCoordinator 实现（供 ViewModel 解耦调用）
    void Services.IUiCoordinator.OpenLinkInBrowser(string linkId) => OpenLinkInBrowserPage(linkId);

    void Services.IUiCoordinator.OpenFolderInBrowser(string folderId)
    {
        if (string.IsNullOrEmpty(folderId) || DataContext is not MainViewModel vm) return;
        vm.SelectNavCommand.Execute("browser");
        _ = vm.BrowserViewModel.LoadAsync(folderId);
    }

    // —— IBrowserLocateHost（「跳转」= 进入目标目录并选中目标行）——
    // 本窗口只提供两个原语：切页 + 委托浏览页执行"进入目录并选中一行"。
    // 目标类型判别、容器目录推导等算法全部在 Services/ContentLocator（组件），窗口不参与。

    void Services.IBrowserLocateHost.ShowBrowser()
    {
        if (DataContext is MainViewModel vm)
            vm.SelectNavCommand.Execute("browser");
    }

    Task<bool> Services.IBrowserLocateHost.EnterAndSelectAsync(string? folderId, string rowId)
        => DataContext is MainViewModel vm
            ? vm.BrowserViewModel.NavigateAndSelectAsync(folderId, rowId)
            : Task.FromResult(false);

    Task Services.IUiCoordinator.RefreshTrashPageAsync() => TrashView is Views.TrashPage tp ? tp.RefreshAsync() : Task.CompletedTask;

    void Services.IUiCoordinator.ShowNavigationTabs()
    {
        if (FindName("NavigationTabs") is ItemsControl navTabs)
            navTabs.Visibility = Visibility.Visible;
    }

    bool Services.IUiCoordinator.ConfirmDeleteFolder(string folderName) => ShowDeleteFolderConfirmation(folderName);

    // Windows 口径：删除文件夹 = 整体移入回收站，不再罗列"子文件夹一并删除"等后果说明
    private bool ShowDeleteFolderConfirmation(string folderName)
        => ShowConfirmDialog("删除文件夹", $"将文件夹「{folderName}」移入回收站吗？");

    /// <summary>通用确认弹窗：统一走 MD3E ConfirmDialog（药丸 + 色调卡片），确定 = true。</summary>
    private bool ShowConfirmDialog(string title, string message)
        => Views.ConfirmDialog.Show(title, message, "删除");

    #endregion

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 无边框大圆角壳：最大化时收掉外距与圆角（窗口贴满工作区），
        // 恢复时回到悬浮卡形态（外距 18 = 阴影呼吸空间，外圆角 22）。
        if (WindowState == WindowState.Maximized)
        {
            WindowShell.Margin = new Thickness(8);
            WindowShell.CornerRadius = new CornerRadius(0);
        }
        else
        {
            WindowShell.Margin = new Thickness(18);
            WindowShell.CornerRadius = new CornerRadius(22);
        }
        UpdateShellClip();
        RepositionNavPill(animate: false);
    }

    /// <summary>CornerRadius 不会圆角裁切子内容：用 RectangleGeometry 裁出窗口圆角。</summary>
    private void UpdateShellClip()
    {
        var w = WindowShell.ActualWidth;
        var h = WindowShell.ActualHeight;
        if (WindowState == WindowState.Maximized || w <= 0 || h <= 0)
        {
            WindowShell.Clip = null;
            return;
        }
        WindowShell.Clip = new System.Windows.Media.RectangleGeometry(
            new System.Windows.Rect(0, 0, w, h), 22, 22);
    }

    private System.ComponentModel.PropertyChangedEventHandler? _navPillHook;
    private void HookNavPillDriver()
    {
        if (DataContext is MainViewModel vm)
        {
            if (_navPillHook != null) vm.PropertyChanged -= _navPillHook;
            _navPillHook = (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.CurrentNavId))
                    Dispatcher.BeginInvoke(new Action(() => RepositionNavPill(animate: true)),
                        System.Windows.Threading.DispatcherPriority.Loaded);
            };
            vm.PropertyChanged += _navPillHook;
        }
    }

    /// <summary>
    /// 让分段胶囊导航的选中药丸对准当前选中项。animate=true 时用 BackEase 弹簧滑动（MD3E expressive）。
    /// </summary>
    private void RepositionNavPill(bool animate)
    {
        if (NavPill.Visibility != Visibility.Visible) return;
        Button? target = null;
        foreach (var btn in FindDescendantButtons(NavigationTabs))
        {
            if (btn.DataContext is NavigationItem ni && ni.IsSelected) { target = btn; break; }
        }
        if (target == null || target.ActualWidth <= 0)
        {
            NavPill.Width = 0;
            return;
        }
        var pt = target.TransformToVisual(NavHost).Transform(new Point(0, 0));
        var targetX = pt.X;
        var targetW = target.ActualWidth;
        if (!animate || SystemParameters.ClientAreaAnimation == false)
        {
            NavPillTransform.X = targetX;
            NavPill.Width = targetW;
            return;
        }
        var animX = new System.Windows.Media.Animation.DoubleAnimation(targetX, new System.Windows.Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var animW = new System.Windows.Media.Animation.DoubleAnimation(targetW, new System.Windows.Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        NavPillTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animX);
        NavPill.BeginAnimation(WidthProperty, animW);
    }

    private static System.Collections.Generic.IEnumerable<Button> FindDescendantButtons(System.Windows.DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b) yield return b;
            foreach (var sub in FindDescendantButtons(child)) yield return sub;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SuppressContextMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        // 搜索页展示「所属文件夹」名称用的扁平文件夹列表
        await viewModel.LoadFolderTreeAsync();

        // 「浏览」页是默认首页（CurrentNavId 初始即 "browser"，启动时不会走 SelectNav），
        // 因此必须在这里主动装载一次，否则左侧文件夹树与列表在启动时是空的、
        // 要手动点一下「浏览」才会加载。
        await viewModel.BrowserViewModel.LoadAsync(null);

        // 导航项此时已完成测量：让选中药丸对准当前选中项（不带动画的初始定位）
        _ = Dispatcher.BeginInvoke(new Action(() => RepositionNavPill(animate: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string FindFolderNameForLink(string? listId)
    {
        if (string.IsNullOrEmpty(listId))
            return "全部书签";

        if (Application.Current.MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
            return MainViewModel.FindFolderPathInNodes(vm.FolderItems, listId) ?? "未知目录";

        return "未知目录";
    }



    private void ResetSearchUI()
    {
        SearchBox.Text = string.Empty;
        ShowSearchEmptyState();
        SearchBox.Focus();
    }

    /// <summary>
    /// 搜索结果表（SortableDataTable）：列定义 = 数据 + 排序键 + 单元格工厂，表头/行/排序全部由控件驱动。
    /// </summary>
    private void SetupSearchTable()
    {
        SearchResultsTable.SortField = "title";   // 默认名称升序（与主栏一致，表头初始即显示 ▲）
        SearchResultsTable.SortAscending = true;
        SearchResultsTable.Columns = new[]
        {
            new DataTableColumn
            {
                // 名称列占 2 份剩余空间：标题下方还有 URL，必须留出可见宽度
                // （用户 2026-09-16 反馈"URL 被大幅压缩"）——空间来自右侧四列压到极限
                Field = "title", Label = "名称", Width = -2,
                SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                    ? ((LinkItem)r).Url : ((LinkItem)r).Title),
                CellFactory = r => BuildSearchNameCell((LinkItem)r)
            },
            new DataTableColumn
            {
                // 位置列占 3 份剩余空间（名称 2 份）：层级路径最长、最需要宽度；
                // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                // 的实测宽 105 + 9 列间余量（探针实测值；改小会截断成省略号，或让相邻列贴在一起）
                // 省下的宽度全部让给名称/位置（用户 2026-09-16 要求 URL 不再被压缩）
                Field = "path", Label = "位置", Width = -3,
                SortKey = r => (IComparable)(FindFolderNameForLink(((LinkItem)r).ListId) ?? "全部书签"),
                CellFactory = r => SearchTextCell(FindFolderNameForLink(((LinkItem)r).ListId) ?? "全部书签", 12.5)
            },
            new DataTableColumn
            {
                Field = "updated_at", Label = "最后更新", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
                CellFactory = r => SearchTextCell(((LinkItem)r).UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12.5)
            },
            new DataTableColumn
            {
                Field = "last_visited_at", Label = "最后查看", Width = 114,
                SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
                CellFactory = r => SearchTextCell(
                    ((LinkItem)r).LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未", 12.5)
            },
            new DataTableColumn
            {
                Field = "visit_count", Label = "查看次数", Width = 72,
                SortKey = r => (IComparable)((LinkItem)r).VisitCount,
                CellFactory = r => SearchTextCell($"{((LinkItem)r).VisitCount} 次", 12.5)
            },
            new DataTableColumn
            {
                Field = "created_at", Label = "创建时间", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
                CellFactory = r => SearchTextCell(((LinkItem)r).CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12.5)
            },
        };

        SearchResultsTable.RowClick += (_, item) =>
        {
            _selectedSearchItem = (LinkItem)item;
            SearchJumpToLinkBtn.IsEnabled = true;
            // 详情栏与表格「位置」列同一口径（沿文件夹树解析路径）
            _searchDetails.UpdateFrom((LinkItem)item, FindFolderNameForLink(((LinkItem)item).ListId));
        };
        SearchResultsTable.RowDoubleClick += (_, item) => OpenLinkInBrowserPage(((LinkItem)item).LinkId);
    }

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
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
        if (faviconBmp == null) faviconImg.Visibility = Visibility.Collapsed;
        var earthIcon = new M3Icon
        {
            Kind = "earth",
            Width = 16, Height = 16,
            Foreground = (Brush)FindResource("OnSurfaceMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
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
            Foreground = (Brush)FindResource("OnSurface"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AddHighlightedRuns(titleBlock, displayTitle, _lastSearchQuery, (Brush)FindResource("OnSurface"));
        textStack.Children.Add(titleBlock);

        var urlBlock = new TextBlock
        {
            FontSize = 11.5,
            Foreground = (Brush)FindResource("OnSurfaceVariant"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        };
        AddHighlightedRuns(urlBlock, item.Url, _lastSearchQuery, (Brush)FindResource("OnSurfaceVariant"));
        textStack.Children.Add(urlBlock);

        panel.Children.Add(iconGrid);
        panel.Children.Add(textStack);
        return panel;
    }

    /// <summary>普通文本单元格（表格化信息列统一规格）。</summary>
    private TextBlock SearchTextCell(string text, double fontSize)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = (Brush)FindResource("OnSurfaceVariant"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

    /// <summary>MD3E 空状态视图：大圆角色块徽章 + 引导性文案（替代生硬的系统提示）。</summary>
    private FrameworkElement BuildSearchState(string iconKind, string title, string? subtitle,
        string containerBrush, string onContainerBrush)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 56, 0, 0) };
        var badge = new Border
        {
            Width = 96, Height = 96, CornerRadius = new CornerRadius(32),
            Background = (Brush)FindResource(containerBrush),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        badge.Child = new M3Icon
        {
            Kind = iconKind, Width = 40, Height = 40,
            Foreground = (Brush)FindResource(onContainerBrush),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(badge);
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("OnSurface"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0)
        });
        if (subtitle != null)
            sp.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("OnSurfaceVariant"), Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                MaxWidth = 420, TextAlignment = TextAlignment.Center
            });
        return sp;
    }

    private void ShowSearchEmptyState()
    {
        _selectedSearchItem = null;
        SearchJumpToLinkBtn.IsEnabled = false;
        _searchDetails.UpdateFrom(null, "");
        SearchResultsTable.EmptyContent = BuildSearchState("magnify", "想找点什么？",
            "输入关键词，回车即可搜索；也可以用上方标签扩大或缩小范围",
            "PrimaryContainer", "OnPrimaryContainer");
        SearchResultsTable.ItemsSource = null;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            var query = SearchBox.Text.Trim();
            _ = ExecuteTitleSearchAsync(vm, query);
            e.Handled = true;
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var query = SearchBox.Text.Trim();
            _ = ExecuteTitleSearchAsync(vm, query);
        }
    }

    private void SearchCancelButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ShowSearchEmptyState();
        SearchBox.Focus();
    }

    private async Task ExecuteTitleSearchAsync(MainViewModel vm, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowSearchEmptyState();
            return;
        }

        // 范围守卫：四个范围全部取消勾选时没有可搜字段，直接给引导空态
        // （原行为会无视范围全量返回，与"搜索范围"语义矛盾）
        if (SearchPathCb.IsChecked != true && SearchUrlCb.IsChecked != true
            && SearchTitleCb.IsChecked != true && SearchDescCb.IsChecked != true)
        {
            _selectedSearchItem = null;
            SearchJumpToLinkBtn.IsEnabled = false;
            _searchDetails.UpdateFrom(null, "");
            SearchResultsTable.EmptyContent = BuildSearchState("alert-circle-outline", "请先选择搜索范围",
                "至少勾选 路径 / URL / 标题 / 描述 之一，再进行搜索",
                "SecondaryContainer", "OnSecondaryContainer");
            SearchResultsTable.ItemsSource = null;
            return;
        }

        // 加载态：清空数据 + 加载占位
        SearchResultsTable.EmptyContent = BuildSearchState("magnify", "正在搜索…",
            null, "SecondaryContainer", "OnSecondaryContainer");
        SearchResultsTable.ItemsSource = null;

        try
        {
            var results = await vm.SearchLinksByTitleAsync(
                query,
                searchPath: SearchPathCb.IsChecked == true,
                searchUrl: SearchUrlCb.IsChecked == true,
                searchTitle: SearchTitleCb.IsChecked == true,
                searchDescription: SearchDescCb.IsChecked == true
            );
            _selectedSearchItem = null;
            SearchJumpToLinkBtn.IsEnabled = false;
            _searchDetails.UpdateFrom(null, "");
            _lastSearchQuery = query;

            // 无结果：空态占位显示"没有找到"；有结果：数据驱动渲染（排序状态保持）
            SearchResultsTable.EmptyContent = results.Count == 0
                ? BuildSearchState("emoticon-sad-outline",
                    $"没有找到与「{query}」相关的内容",
                    "换个关键词，或用上方标签扩大搜索范围再试试",
                    "SecondaryContainer", "OnSecondaryContainer")
                : BuildSearchState("magnify", "想找点什么？",
                    "输入关键词，回车即可搜索；也可以用上方标签扩大或缩小范围",
                    "PrimaryContainer", "OnPrimaryContainer");
            SearchResultsTable.ItemsSource = results;
        }
        catch (Exception ex)
        {
            SearchResultsTable.EmptyContent = BuildSearchState("alert-outline", "搜索出了点小问题",
                ex.Message, "SurfaceContainerHighest", "OnSurface");
            SearchResultsTable.ItemsSource = null;
        }
    }

    private string _lastSearchQuery = "";

    private List<LinkItem> _lastSearchResults = new();

    // —— 范围变化 → 静默刷新（非全量）：防抖 300ms 后只替换行集合，
    //    不出现「正在搜索…」加载态、不重置排序/表头，原选中项若仍在结果中则保持选中 ——
    private System.Windows.Threading.DispatcherTimer? _scopeDebounce;
    private int _scopeRefreshGen;

    private void SearchScope_Changed(object sender, RoutedEventArgs e)
    {
        _scopeDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _scopeDebounce.Stop();
        _scopeDebounce.Tick -= ScopeDebounce_Tick;
        _scopeDebounce.Tick += ScopeDebounce_Tick;
        _scopeDebounce.Start();
    }

    private void ScopeDebounce_Tick(object? sender, EventArgs e)
    {
        _scopeDebounce?.Stop();
        if (DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(_lastSearchQuery))
            _ = RefreshSearchResultsAsync(vm);
    }

    /// <summary>
    /// 范围变化后的就地刷新：拿新范围的结果直接替换 ItemsSource（跳过加载占位与清空闪烁）；
    /// 代次计数防止连续切换时旧结果覆盖新结果。范围全空时给引导空态（与搜索守卫同一口径）。
    /// </summary>
    private async Task RefreshSearchResultsAsync(MainViewModel vm)
    {
        var query = _lastSearchQuery;
        if (string.IsNullOrWhiteSpace(query)) return;

        int gen = ++_scopeRefreshGen;

        if (SearchPathCb.IsChecked != true && SearchUrlCb.IsChecked != true
            && SearchTitleCb.IsChecked != true && SearchDescCb.IsChecked != true)
        {
            _selectedSearchItem = null;
            SearchJumpToLinkBtn.IsEnabled = false;
            _searchDetails.UpdateFrom(null, "");
            SearchResultsTable.EmptyContent = BuildSearchState("alert-circle-outline", "请先选择搜索范围",
                "至少勾选 路径 / URL / 标题 / 描述 之一，再进行搜索",
                "SecondaryContainer", "OnSecondaryContainer");
            SearchResultsTable.ItemsSource = null;
            return;
        }

        try
        {
            var results = await vm.SearchLinksByTitleAsync(
                query,
                searchPath: SearchPathCb.IsChecked == true,
                searchUrl: SearchUrlCb.IsChecked == true,
                searchTitle: SearchTitleCb.IsChecked == true,
                searchDescription: SearchDescCb.IsChecked == true
            );
            if (gen != _scopeRefreshGen) return; // 已有更新的范围变化，放弃旧结果

            SearchResultsTable.ItemsSource = results;

            // 选中保持：原选中项仍在新结果里 → 恢复行选中与详情栏；不在 → 清空
            if (_selectedSearchItem is { } prev)
            {
                var still = results.FirstOrDefault(r => r.LinkId == prev.LinkId);
                if (still != null)
                {
                    _selectedSearchItem = still;
                    SearchResultsTable.SelectItem(still);
                    _searchDetails.UpdateFrom(still, FindFolderNameForLink(still.ListId));
                }
                else
                {
                    _selectedSearchItem = null;
                    SearchJumpToLinkBtn.IsEnabled = false;
                    _searchDetails.UpdateFrom(null, "");
                }
            }
        }
        catch { /* 静默刷新失败时保留旧列表 */ }
    }

    /// <summary>把命中的关键词染成强调色（大小写不敏感），其余用普通画刷。</summary>
    private void AddHighlightedRuns(TextBlock tb, string text, string query, Brush normal)
    {
        var accent = (Brush)FindResource("Primary");
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(query))
        {
            tb.Inlines.Add(new Run(text) { Foreground = normal });
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
                    tb.Inlines.Add(new Run(text[pos..]) { Foreground = normal });
                break;
            }
            if (hit > pos)
                tb.Inlines.Add(new Run(text[pos..hit]) { Foreground = normal });
            tb.Inlines.Add(new Run(text.Substring(hit, q.Length))
            {
                Foreground = accent,
                FontWeight = FontWeights.Bold
            });
            pos = hit + q.Length;
        }
    }


    private void SearchResultsArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 行选中/清除已由 SortableDataTable 内部管理
    }

    private void SearchJumpToLinkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchItem != null)
            OpenLinkInBrowserPage(_selectedSearchItem.LinkId);
    }

    /// <summary>
    /// 搜索结果「跳转」：老「链接」页已整体删除，改为切到「浏览」页并打开该链接的详情页
    /// （无需先导航到它所在的文件夹，详情页自带所属路径与打开/编辑/删除操作）。
    /// </summary>
    private void OpenLinkInBrowserPage(string linkId)
    {
        if (string.IsNullOrEmpty(linkId) || DataContext is not MainViewModel vm) return;

        vm.SelectNavCommand.Execute("browser");
        _ = vm.BrowserViewModel.OpenDetailPageByIdAsync(linkId);
    }

    /// <summary>
    /// 搜索详情栏的页面动作命令（数据模型只持契约，动作由本窗口注入）：
    /// 打开 / 编辑都进入「浏览」页的链接详情页（详情页自带完整编辑与删除入口）；
    /// 删除 = 确认后移入回收站（可恢复），随后重跑当前搜索刷新结果。
    /// </summary>
    private void WireSearchDetailsCommands()
    {
        _searchDetails.OpenCommand = new RelayCommand(
            () => { if (_selectedSearchItem != null) OpenLinkInBrowserPage(_selectedSearchItem.LinkId); },
            () => _selectedSearchItem != null);
        _searchDetails.RenameCommand = new RelayCommand(
            () => { if (_selectedSearchItem != null) OpenLinkInBrowserPage(_selectedSearchItem.LinkId); },
            () => _selectedSearchItem != null);
        _searchDetails.OpenWebsiteCommand = new RelayCommand(
            () => _ = OpenSelectedSearchLinkWebsiteAsync(),
            () => _selectedSearchItem != null);
        _searchDetails.DeleteCommand = new RelayCommand(() => _ = DeleteSelectedSearchLinkAsync(),
            () => _selectedSearchItem != null);
    }

    /// <summary>搜索侧栏「打开网站」：默认浏览器打开并记录一次访问（与浏览页侧栏同口径）。</summary>
    private async Task OpenSelectedSearchLinkWebsiteAsync()
    {
        var item = _selectedSearchItem;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
        catch { /* 无法打开时保持静默 */ }
        try
        {
            await Services.AppServices.Api.RecordVisitAsync(item.LinkId);
            if (_selectedSearchItem?.LinkId == item.LinkId)
                _searchDetails.UpdateFrom(item, FindFolderNameForLink(item.ListId)); // 统计行原位刷新
        }
        catch { /* 记账失败不打断 */ }
    }

    private async Task DeleteSelectedSearchLinkAsync()
    {
        var item = _selectedSearchItem;
        if (item == null) return;

        var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
        if (!ShowConfirmDialog("删除链接", $"将链接「{name}」移入回收站吗？")) return;

        try
        {
            await Services.AppServices.Api.TrashLinkAsync(item.LinkId);
            _selectedSearchItem = null;
            SearchJumpToLinkBtn.IsEnabled = false;
            _searchDetails.UpdateFrom(null, "");

            // 重跑当前搜索刷新结果（无在搜关键词时只清详情）
            if (!string.IsNullOrWhiteSpace(_lastSearchQuery) && DataContext is MainViewModel vm)
                await ExecuteTitleSearchAsync(vm, _lastSearchQuery);
        }
        catch (Exception ex)
        {
            SearchResultsTable.EmptyContent = BuildSearchState("alert-outline", "删除出了点小问题",
                ex.Message, "SurfaceContainerHighest", "OnSurface");
        }
    }



    private static BitmapImage? TryLoadFavicon(string? faviconUrl)
    {
        return FaviconService.LoadFromCache(faviconUrl);
    }

}

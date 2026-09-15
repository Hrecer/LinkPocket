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
using Material3.Wpf;

namespace LinkPocket;

public partial class MainWindow : Window, Services.IUiCoordinator
{
    private readonly Managers.SelectionManager _selectionManager = new();

    // 搜索页自己的选中态（老「链接」页删除后，主窗口只剩搜索页需要残余状态）
    private Border? _selectedSearchCard;
    private LinkItem? _selectedSearchItem;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(_selectionManager);
        BrowserPage.DataContext = ((MainViewModel)DataContext).BrowserViewModel;
        Services.UiCoordinator.Instance = this;

        // 老「链接」页的 LinkNavigator（在旧列表里定位/展开/滚动到某条链接）随页面一并删除；
        // 搜索页的「跳转」已改为在「浏览」页直接打开该链接的详情页。
        if (DataContext is MainViewModel searchVm)
        {
            searchVm.OnNavigatedToSearch += (s, e) => ResetSearchUI();
            searchVm.OnNavigatedFromSearch += (s, e) =>
            {
                _selectedSearchCard = null;
                _selectedSearchItem = null;
                ResetDetailPanelPlaceholder(SearchFixedSidebar);
                SearchJumpToLinkBtn.IsEnabled = false;
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
    }

    #region IUiCoordinator 实现（供 ViewModel 解耦调用）

    // 老「链接」页已整体删除：以下成员原本全部作用在它的界面上（左侧文件夹树、中间链接列表、
    // 右侧详情栏、整页详情/编辑页、新建文件夹弹窗）。这里保留方法签名但不再有界面动作，
    // 以免 ViewModel 侧既有调用链断裂；新页面的同级能力由 BrowserView 自行承担。
    void Services.IUiCoordinator.ShowEditPage() { }
    void Services.IUiCoordinator.CloseEditPage(bool returnToDetail) { }
    void Services.IUiCoordinator.ShowDetailView() { }
    void Services.IUiCoordinator.CloseDetailView() { }
    void Services.IUiCoordinator.UpdateDetailPanel(LinkItem link) { }
    void Services.IUiCoordinator.ClearDetailPanel() { }
    LinkItem? Services.IUiCoordinator.GetSelectedLink() => null;
    void Services.IUiCoordinator.RefreshSidebar() { }
    Task Services.IUiCoordinator.RefreshSidebarAsync() => Task.CompletedTask;
    Task Services.IUiCoordinator.RefreshMainListAsync() => Task.CompletedTask;
    void Services.IUiCoordinator.ClearMainList() { }
    void Services.IUiCoordinator.ExpandFolder(string folderId) { }
    void Services.IUiCoordinator.ClearFolderSelection() { }
    void Services.IUiCoordinator.FocusNewFolderDialog() { }

    // 浏览页显隐改为 MainWindow.xaml 里 CurrentNavId 的 DataTrigger 声明式控制
    // （与搜索/回收站/智能列表/工具/设置各页一致）——若在这里用代码设置 Visibility，
    // 本地值会盖过样式触发器，导致离开浏览页后旧页面仍盖在上面。
    void Services.IUiCoordinator.ShowBrowserPage() { }
    void Services.IUiCoordinator.CloseBrowserPage() { }

    void Services.IUiCoordinator.OpenLinkInBrowser(string linkId) => OpenLinkInBrowserPage(linkId);

    void Services.IUiCoordinator.OpenFolderInBrowser(string folderId)
    {
        if (string.IsNullOrEmpty(folderId) || DataContext is not MainViewModel vm) return;
        vm.SelectNavCommand.Execute("browser");
        _ = vm.BrowserViewModel.LoadAsync(folderId);
    }

    Task Services.IUiCoordinator.RefreshTrashPageAsync() => TrashView is Views.TrashPage tp ? tp.RefreshAsync() : Task.CompletedTask;

    void Services.IUiCoordinator.ShowNavigationTabs()
    {
        if (FindName("NavigationTabs") is ItemsControl navTabs)
            navTabs.Visibility = Visibility.Visible;
    }

    bool Services.IUiCoordinator.ConfirmDeleteFolder(string folderName) => ShowDeleteFolderConfirmation(folderName);

    private bool ShowDeleteFolderConfirmation(string folderName)
    {
        var dialog = new Window
        {
            Title = "删除文件夹",
            Width = 360, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Background = System.Windows.Media.Brushes.Transparent,
            AllowsTransparency = true
        };

        var contentPanel = new StackPanel { Margin = new Thickness(24) };

        contentPanel.Children.Add(new TextBlock
        {
            Text = "删除文件夹", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4)
        });

        contentPanel.Children.Add(new TextBlock
        {
            Text = $"确定要删除文件夹 \"{folderName}\" 吗？",
            FontSize = 14, Margin = new Thickness(0, 0, 0, 20),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("OnSurface")
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand, BorderBrush = (Brush)Application.Current.FindResource("OutlineVariant"),
            BorderThickness = new Thickness(1)
        };
        cancelBtn.Click += (s, e) => dialog.DialogResult = false;
        btnPanel.Children.Add(cancelBtn);

        var okBtn = new System.Windows.Controls.Button
        {
            Content = "确定", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.FindResource("Primary"),
            Foreground = System.Windows.Media.Brushes.White
        };
        okBtn.Click += (s, e) => dialog.DialogResult = true;
        btnPanel.Children.Add(okBtn);

        contentPanel.Children.Add(btnPanel);

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = (Brush)Application.Current.FindResource("OutlineVariant"),
            BorderThickness = new Thickness(1),
            Child = contentPanel
        };

        dialog.Content = outerBorder;

        dialog.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                dialog.DialogResult = true;
                e.Handled = true;
            }
        };

        return dialog.ShowDialog() == true;
    }

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
        if (WindowState == WindowState.Maximized)
        {
            var wa = SystemParameters.WorkArea;
            RootGrid.Margin = new Thickness(
                wa.Left, wa.Top,
                SystemParameters.PrimaryScreenWidth - wa.Right,
                SystemParameters.PrimaryScreenHeight - wa.Bottom);
        }
        else
        {
            RootGrid.Margin = new Thickness(0);
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

    private static readonly Dictionary<string, string> SortFieldLabels = new()
    {
        { "title", "按名称" }, { "updated_at", "最后更新" },
        { "last_visited_at", "最后查看" }, { "visit_count", "累计查看次数" }, { "created_at", "创建时间" }
    };


    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        // 搜索页展示「所属文件夹」名称用的扁平文件夹列表
        await viewModel.LoadFolderTreeAsync();

        // 「浏览」页是默认首页（CurrentNavId 初始即 "browser"，启动时不会走 SelectNav），
        // 因此必须在这里主动装载一次，否则左侧文件夹树与列表在启动时是空的、
        // 要手动点一下「浏览」才会加载。
        await viewModel.BrowserViewModel.LoadAsync(null);
    }





    private static void PopulateDetailPanel(Panel panel, string url, string? title, string? description,
        string? faviconUrl, DateTime updatedAt, DateTime? lastVisitedAt, int visitCount,
        DateTime createdAt, string? linkId, string folderName)
    {
        panel.Children.Clear();

        var topIconRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };

        var linkVariantIcon = new M3Icon
        {
            Kind = "link-variant", Width = 32, Height = 32,
            Foreground = (Brush)Application.Current.FindResource("Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var faviconBorder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
            Background = Brushes.White,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var faviconGrid = new Grid();
        var faviconBmp = TryLoadFavicon(faviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (faviconBmp == null)
            faviconImg.Visibility = Visibility.Collapsed;
        var earthIcon = new M3Icon
        {
            Kind = "earth",
            Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        if (faviconBmp != null)
            earthIcon.Visibility = Visibility.Collapsed;
        faviconGrid.Children.Add(faviconImg);
        faviconGrid.Children.Add(earthIcon);
        faviconBorder.Child = faviconGrid;

        topIconRow.Children.Add(linkVariantIcon);
        topIconRow.Children.Add(faviconBorder);
        panel.Children.Add(topIconRow);

        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var folderIcon = new M3Icon { Kind = "folder-outline", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("OnSurfaceVariant"), Opacity = 0.6 };
        Grid.SetColumn(folderIcon, 0);
        folderRow.Children.Add(folderIcon);
        var folderText = new TextBlock { Text = folderName ?? "全部书签", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(folderText, 1);
        folderRow.Children.Add(folderText);
        panel.Children.Add(folderRow);

        panel.Children.Add(new TextBlock { Text = "URL", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        var urlGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 320 });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var urlTb = new TextBlock { Text = url ?? "", FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(urlTb, 0);
        urlGrid.Children.Add(urlTb);
        var urlCopyBtn = new Button
        {
            Content = new M3Icon { Kind = "content-copy", Width = 12, Height = 12, Foreground = Brushes.Black },
            Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "复制",
            VerticalAlignment = VerticalAlignment.Center
        };
        var capturedUrl = url ?? "";
        urlCopyBtn.Click += (s, e) => { Clipboard.SetText(capturedUrl); };
        Grid.SetColumn(urlCopyBtn, 1);
        urlGrid.Children.Add(urlCopyBtn);
        panel.Children.Add(urlGrid);

        panel.Children.Add(new TextBlock { Text = "标题", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = title ?? "", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 12), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "描述", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = description ?? "（无描述）", FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 12), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "最后更新", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "最后查看", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = lastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未", FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "累计查看次数", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = visitCount == 0 ? "0 次" : $"{visitCount} 次", FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "创建时间", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBox { Text = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        panel.Children.Add(new TextBlock { Text = "ID", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(CreateValueWithCopy(linkId ?? "", linkId ?? "", true));

        if (!string.IsNullOrWhiteSpace(faviconUrl) && faviconBmp == null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FaviconService.PrefetchAndCacheAsync(faviconUrl);
                    var cached = FaviconService.LoadFromCache(faviconUrl);
                    if (cached != null)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
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
    }

    private static void ResetDetailPanelPlaceholder(Panel panel)
    {
        panel.Children.Clear();

        var placeholder = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 0)
        };
        placeholder.Children.Add(new M3Icon
        {
            Kind = "bookmark-outline", Width = 48, Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.15
        });
        placeholder.Children.Add(new TextBlock
        {
            Text = "选中以查看详情", FontSize = 13, Opacity = 0.3,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(placeholder);
    }

    private static Panel CreateValueWithCopy(string text, string copyValue, bool useMonospace)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBox
        {
            Text = text,
            FontSize = useMonospace ? 11 : 13,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
            ContextMenu = null
        };
        if (useMonospace)
        {
            tb.FontFamily = new FontFamily("Consolas");
        }
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);
        var btn = new Button
        {
            Content = new M3Icon { Kind = "content-copy", Width = 12, Height = 12, Foreground = Brushes.Black },
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "复制",
            VerticalAlignment = VerticalAlignment.Center
        };
        var captured = copyValue;
        btn.Click += (s, e) => { Clipboard.SetText(captured); };
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    private static string FindFolderNameForLink(string? listId)
    {
        if (string.IsNullOrEmpty(listId))
            return "全部书签";

        if (Application.Current.MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
            return MainViewModel.FindFolderPathInNodes(vm.FolderItems, listId) ?? "未知目录";

        return "未知目录";
    }



    private void SearchSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        UpdateSearchSortMenu(vm);
        SearchSortMenu.PlacementTarget = SearchSortButton;
        SearchSortMenu.IsOpen = true;
    }

    private void UpdateSearchSortMenu(MainViewModel vm)
    {
        foreach (MenuItem item in SearchSortMenu.Items)
        {
            var field = item.Tag as string;
            if (field == null) continue;
            var isActive = field == vm.LinkSortField;
            var arrow = isActive ? (vm.LinkSortOrder == "asc" ? " ↑" : " ↓") : "";
            var check = isActive ? "✓ " : "   ";
            item.Header = $"{check}{SortFieldLabels.GetValueOrDefault(field, field)}{arrow}";
        }
        if (SortFieldLabels.TryGetValue(vm.LinkSortField, out var label))
            SearchSortButtonText.Text = label;
        else
            SearchSortButtonText.Text = "排序";
        SearchSortOrderText.Text = vm.LinkSortOrder == "asc" ? "↑ 升序" : "↓ 降序";
        SearchSortButton.ToolTip = $"结果排序：{label} {(vm.LinkSortOrder == "asc" ? "升序" : "降序")}";
    }

    private async void SearchSortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string field) return;
        if (DataContext is not MainViewModel vm) return;
        await vm.SetLinkSortAsync(field);
        UpdateSearchSortMenu(vm);
        if (!string.IsNullOrWhiteSpace(SearchBox.Text.Trim()))
        {
            var query = SearchBox.Text.Trim();
            _ = ExecuteTitleSearchAsync(vm, query);
        }
    }

    private void ResetSearchUI()
    {
        SearchBox.Text = string.Empty;
        ShowSearchEmptyState();
        if (DataContext is MainViewModel sortVm)
            UpdateSearchSortMenu(sortVm);
        SearchBox.Focus();
    }

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
        SearchResultsPanel.Children.Clear();
        SearchResultsPanel.Children.Add(BuildSearchState("magnify", "想找点什么？",
            "输入关键词，回车即可搜索；也可以用上方标签扩大或缩小范围",
            "PrimaryContainer", "OnPrimaryContainer"));
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
        SearchResultsPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            ShowSearchEmptyState();
            return;
        }

        SearchResultsPanel.Children.Add(BuildSearchState("magnify", "正在搜索…",
            null, "SecondaryContainer", "OnSecondaryContainer"));

        try
        {
            var results = await vm.SearchLinksByTitleAsync(
                query,
                searchPath: SearchPathCb.IsChecked == true,
                searchUrl: SearchUrlCb.IsChecked == true,
                searchTitle: SearchTitleCb.IsChecked == true,
                searchDescription: SearchDescCb.IsChecked == true
            );
            SearchResultsPanel.Children.Clear();
            _selectedSearchCard = null;
            _selectedSearchItem = null;
            ResetDetailPanelPlaceholder(SearchFixedSidebar);
            SearchJumpToLinkBtn.IsEnabled = false;

            if (results.Count == 0)
            {
                SearchResultsPanel.Children.Add(BuildSearchState("emoticon-sad-outline",
                    $"没有找到与「{query}」相关的内容",
                    "换个关键词，或用上方标签扩大搜索范围再试试",
                    "SecondaryContainer", "OnSecondaryContainer"));
                return;
            }

            _lastSearchQuery = query;
            for (var i = 0; i < results.Count; i++)
            {
                var card = CreateSearchResultCard(results[i], vm);
                SearchResultsPanel.Children.Add(card);
                PlayCardEntrance(card, i);
            }
        }
        catch (Exception ex)
        {
            SearchResultsPanel.Children.Clear();
            SearchResultsPanel.Children.Add(BuildSearchState("alert-outline", "搜索出了点小问题",
                ex.Message, "SurfaceContainerHighest", "OnSurface"));
        }
    }

    private string _lastSearchQuery = "";

    /// <summary>
    /// 结果卡错峰入场：淡入 + 轻微上移，弹簧（BackEase）曲线，每张错开 40ms。
    /// 系统关闭客户端动画（辅助功能「减少动态效果」）时跳过。
    /// </summary>
    private void PlayCardEntrance(FrameworkElement el, int index)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var tt = new TranslateTransform(0, 16);
        el.RenderTransform = tt;
        el.Opacity = 0;
        var ease = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 40);

        var oy = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(340))
        { EasingFunction = ease, BeginTime = begin };
        var oo = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        { BeginTime = begin };

        // 动画结束后清除动画层并落到终值（否则 Stop 会回退到本地值 0，卡片永远透明）
        var done = (EventHandler)((_, _) =>
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        });
        oo.Completed += done;
        oy.Completed += done;

        tt.BeginAnimation(TranslateTransform.YProperty, oy);
        el.BeginAnimation(UIElement.OpacityProperty, oo);
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

    private Border CreateSearchResultCard(LinkItem item, MainViewModel vm)
    {
        // MD3E：色彩分层替代阴影层级 —— 默认 SurfaceContainerLowest，hover 升到 High，
        // 选中 PrimaryContainer + Primary 描边；不使用 DropShadow。
        var card = new Border
        {
            Tag = "SearchCard", Margin = new Thickness(4, 4, 4, 4), CornerRadius = new CornerRadius(16),
            Cursor = Cursors.Hand, Width = 720, HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("SurfaceContainerLowest"),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
            Padding = new Thickness(16, 12, 16, 12)
        };
        var style = new Style(typeof(Border));
        style.Triggers.Add(new Trigger
        {
            Property = Border.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, (Brush)FindResource("SurfaceContainerHigh")) }
        });
        card.Style = style;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 图标位：PrimaryContainer 圆角色块（大圆角，与卡片 16 圆角形成形状对比）
        var iconBorder = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("PrimaryContainer"),
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        var iconGrid = new Grid();

        var faviconBmp = TryLoadFavicon(item.FaviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6)
        };
        if (faviconBmp == null)
            faviconImg.Visibility = Visibility.Collapsed;

        var earthIcon = new M3Icon
        {
            Kind = "earth",
            Width = 20, Height = 20,
            Foreground = (Brush)FindResource("OnPrimaryContainer"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.75
        };
        if (faviconBmp != null)
            earthIcon.Visibility = Visibility.Collapsed;

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

        iconBorder.Child = iconGrid;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // 文本区：强调型排版 —— 标题 15 SemiBold（关键词强调色高亮）/ URL / 元数据三个层级
        var displayTitle = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url;
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleBlock = new TextBlock
        {
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("OnSurface"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AddHighlightedRuns(titleBlock, displayTitle, _lastSearchQuery, (Brush)FindResource("OnSurface"));
        textStack.Children.Add(titleBlock);

        var urlBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("OnSurfaceVariant"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0)
        };
        AddHighlightedRuns(urlBlock, item.Url, _lastSearchQuery, (Brush)FindResource("OnSurfaceVariant"));
        textStack.Children.Add(urlBlock);

        // 元数据行：来源文件夹 · 最后更新 —— 小字号、低对比
        var folderName = FindFolderNameForLink(item.ListId) ?? "全部书签";
        var metaText = $"{folderName} · 最后更新 {item.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        textStack.Children.Add(new TextBlock
        {
            Text = metaText, FontSize = 11,
            Foreground = (Brush)FindResource("OnSurfaceVariant"), Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        card.Child = grid;

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (_selectedSearchCard != null && _selectedSearchCard != card)
            {
                _selectedSearchCard.BorderBrush = Brushes.Transparent;
                _selectedSearchCard.Background = (Brush)FindResource("SurfaceContainerLowest");
            }

            _selectedSearchCard = card;
            _selectedSearchItem = item;
            card.Background = (Brush)FindResource("PrimaryContainer");
            card.BorderBrush = (Brush)FindResource("Primary");

            PopulateDetailPanel(SearchFixedSidebar, item.Url, item.Title, item.Description, item.FaviconUrl,
                item.UpdatedAt, item.LastVisitedAt, item.VisitCount, item.CreatedAt, item.LinkId,
                FindFolderNameForLink(item.ListId));

            SearchJumpToLinkBtn.IsEnabled = true;

            if (e.ClickCount == 2)
            {
                OpenLinkInBrowserPage(item.LinkId); // 老「链接」详情页已删除 → 改在「浏览」页打开详情
                e.Handled = true;
            }
        };

        return card;
    }

    private void SearchResultsArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideGridSplitter(e.OriginalSource as DependencyObject)) return;
        if (e.OriginalSource is not Border && _selectedSearchCard != null)
        {
            _selectedSearchCard.BorderBrush = (Brush)FindResource("OutlineVariant");
            _selectedSearchCard = null;
            _selectedSearchItem = null;
            ResetDetailPanelPlaceholder(SearchFixedSidebar);
            SearchJumpToLinkBtn.IsEnabled = false;
        }
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



    private static BitmapImage? TryLoadFavicon(string? faviconUrl)
    {
        return FaviconService.LoadFromCache(faviconUrl);
    }

    private static bool IsInsideGridSplitter(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is GridSplitter) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

}

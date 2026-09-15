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

public partial class MainWindow : Window, Services.IUiCoordinator
{
    private readonly Managers.SelectionManager _selectionManager = new();

    // 搜索页自己的选中态（老「链接」页删除后，主窗口只剩搜索页需要残余状态）
    private LinkItem? _selectedSearchItem;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(_selectionManager);
        BrowserPage.DataContext = ((MainViewModel)DataContext).BrowserViewModel;
        Services.UiCoordinator.Instance = this;
        SetupSearchTable(); // 搜索结果表：列定义 + 排序 + 行交互（完全数据驱动）

        // 老「链接」页的 LinkNavigator（在旧列表里定位/展开/滚动到某条链接）随页面一并删除；
        // 搜索页的「跳转」已改为在「浏览」页直接打开该链接的详情页。
        if (DataContext is MainViewModel searchVm)
        {
            searchVm.OnNavigatedToSearch += (s, e) => ResetSearchUI();
            searchVm.OnNavigatedFromSearch += (s, e) =>
            {
                _selectedSearchItem = null;
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
        SizeChanged += (_, _) => UpdateShellClip();
        // 分段胶囊导航：CurrentNavId 变化时让选中药丸滑过去（弹簧曲线）
        DataContextChanged += (_, _) => HookNavPillDriver();
        HookNavPillDriver();
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
        Dispatcher.BeginInvoke(new Action(() => RepositionNavPill(animate: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
                Field = "title", Label = "名称", Width = -1,
                SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                    ? ((LinkItem)r).Url : ((LinkItem)r).Title),
                CellFactory = r => BuildSearchNameCell((LinkItem)r)
            },
            new DataTableColumn
            {
                Field = "path", Label = "位置", Width = 200,
                SortKey = r => (IComparable)(FindFolderNameForLink(((LinkItem)r).ListId) ?? "全部书签"),
                CellFactory = r => SearchTextCell(FindFolderNameForLink(((LinkItem)r).ListId) ?? "全部书签", 12.5)
            },
            new DataTableColumn
            {
                Field = "updated_at", Label = "最后更新", Width = 150,
                SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
                CellFactory = r => SearchTextCell(((LinkItem)r).UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 13)
            },
            new DataTableColumn
            {
                Field = "last_visited_at", Label = "最后查看", Width = 150,
                SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
                CellFactory = r => SearchTextCell(
                    ((LinkItem)r).LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未", 13)
            },
            new DataTableColumn
            {
                Field = "visit_count", Label = "查看次数", Width = 90,
                SortKey = r => (IComparable)((LinkItem)r).VisitCount,
                CellFactory = r => SearchTextCell($"{((LinkItem)r).VisitCount} 次", 13)
            },
            new DataTableColumn
            {
                Field = "created_at", Label = "创建时间", Width = 150,
                SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
                CellFactory = r => SearchTextCell(((LinkItem)r).CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 13)
            },
        };

        SearchResultsTable.RowClick += (_, item) =>
        {
            _selectedSearchItem = (LinkItem)item;
            SearchJumpToLinkBtn.IsEnabled = true;
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

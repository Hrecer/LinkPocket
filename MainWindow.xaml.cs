using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using MaterialDesignThemes.Wpf;

namespace LinkPocket;

public partial class MainWindow : Window
{
    private int _selectedFolderId = -1;
    private readonly HashSet<int> _expandedFolders = new() { 0 };
    private LinkItem? _currentSelectedLink;
    private readonly Dictionary<int, Border> _sidebarLinkBorders = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += MainWindow_Loaded;
        RefreshDetailPanel();
    }

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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CopyDetailId_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            Clipboard.SetText(vm.DetailLinkIdDisplay);
    }

    private void CopyDetailUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            Clipboard.SetText(vm.DetailUrl);
    }

    private void CopyEditId_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            Clipboard.SetText(vm.EditLinkIdDisplay);
    }

    private void DetailMarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var raisedEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent
        };
        var parent = DetailMarkdownViewer?.Parent as UIElement;
        parent?.RaiseEvent(raisedEvent);
    }

    private void DetailMarkdownViewer_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }

    private void SuppressContextMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void LinkList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.LinkViewModel != null)
        {
            var hitTest = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender));
            if (hitTest?.VisualHit == null || !IsLinkCard(hitTest.VisualHit))
            {
                vm.LinkViewModel.ClearSelectionCommand.Execute(null);
            }
        }
    }

    private bool IsLinkCard(DependencyObject element)
    {
        while (element != null)
        {
            if (element is Border border && "LinkCard".Equals(border.Tag as string))
                return true;
            if (element is Visual)
                element = VisualTreeHelper.GetParent(element);
            else
                break;
        }
        return false;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.LoadFolderTreeAsync();
            RefreshSidebar(viewModel);
            await RefreshMainListAsync();
            if (viewModel.LinkViewModel != null)
            {
                await viewModel.LinkViewModel.LoadLinksAsync();
            }
        }
    }

    public void RefreshSidebar(MainViewModel viewModel)
    {
        FolderListPanel.Children.Clear();
        _sidebarLinkBorders.Clear();

        var folderItems = viewModel.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            RenderFolderNode(folder, FolderListPanel, 0, viewModel);
        }
    }

    private void RenderFolderNode(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _expandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectedFolderId;

        var folderRow = CreateFolderRow(folder, isExpanded, isSelected, depth, viewModel);
        container.Children.Add(folderRow);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };

            foreach (var child in folder.Children)
            {
                RenderFolderNode(child, childPanel, depth + 1, viewModel);
            }

            List<Data.Link> links;
            if (folder.Id == 0)
                links = viewModel.GetRootLevelLinksAsync().GetAwaiter().GetResult();
            else
            {
                var linksResult = viewModel.GetLinksForSidebarAsync(folder.Id).GetAwaiter().GetResult();
                links = linksResult.Links;
            }
            if (links != null && links.Count > 0)
            {
                foreach (var link in links)
                {
                    var itemRow = CreateSidebarLinkRow(link, viewModel);
                    childPanel.Children.Add(itemRow);
                }
            }

            container.Children.Add(childPanel);
        }
    }

    private Border CreateFolderRow(FolderNode folder, bool isExpanded, bool isSelected, int depth, MainViewModel viewModel)
    {
        var row = new Border
        {
            MinHeight = 28, Padding = new Thickness(4 + depth * 4, 4, 4, 4),
            Cursor = Cursors.Hand, Tag = folder.Id,
            Background = isSelected
                ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var chevronBorder = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Tag = folder.Id
        };
        chevronBorder.Child = new PackIcon
        {
            Width = 12, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Kind = isExpanded ? PackIconKind.ChevronDown : PackIconKind.ChevronRight,
            Opacity = 0.5
        };
        stack.Children.Add(chevronBorder);

        stack.Children.Add(new PackIcon
        {
            Width = 16, Height = 16, Margin = new Thickness(2, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Kind = folder.Id == 0 ? PackIconKind.BookmarkOutline : (isExpanded ? PackIconKind.Folder : PackIconKind.FolderOutline),
            Opacity = 0.7
        });

        stack.Children.Add(new TextBlock
        {
            Text = folder.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center
        });

        if (folder.LinkCount > 0 && folder.Id != 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $" {folder.LinkCount}", FontSize = 10, Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        row.Child = stack;

        chevronBorder.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int folderId)
            {
                if (_expandedFolders.Contains(folderId))
                    _expandedFolders.Remove(folderId);
                else
                    _expandedFolders.Add(folderId);

                RefreshSidebar(viewModel);
            }
            e.Handled = true;
        };

        row.MouseLeftButtonDown += (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int folderId)
            {
                if (_selectedFolderId == folderId)
                    ClearFolderSelection();
                else
                    SetFolderSelection(folderId);
            }
            e.Handled = true;
        };

        row.MouseEnter += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid && fid != _selectedFolderId)
                b.Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
        };

        row.MouseLeave += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid && fid != _selectedFolderId)
                b.Background = new SolidColorBrush(Colors.Transparent);
        };

        return row;
    }

    private Border CreateSidebarLinkRow(Data.Link link, MainViewModel viewModel)
    {
        var itemRow = new Border
        {
            MinHeight = 28, Padding = new Thickness(4, 2, 4, 2),
            Cursor = Cursors.Hand, Tag = link.Id,
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(1, 1, 1, 1)
        };

        var itemStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var iconBorder = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var iconGrid = new Grid();

        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = !string.IsNullOrEmpty(link.FaviconUrl) ? new BitmapImage(new Uri(link.FaviconUrl, UriKind.Absolute)) : null,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (string.IsNullOrEmpty(link.FaviconUrl))
            faviconImg.Visibility = Visibility.Collapsed;

        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
            Width = 11, Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        if (!string.IsNullOrEmpty(link.FaviconUrl))
            earthIcon.Visibility = Visibility.Collapsed;

        iconGrid.Children.Add(faviconImg);
        iconGrid.Children.Add(earthIcon);
        iconBorder.Child = iconGrid;

        itemStack.Children.Add(iconBorder);

        itemStack.Children.Add(new TextBlock
        {
            Text = !string.IsNullOrEmpty(link.Title) ? link.Title : link.Url,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        itemRow.Child = itemStack;

        _sidebarLinkBorders[link.Id] = itemRow;

        itemRow.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (s is Border br && br.Tag is int lid && viewModel.LinkViewModel != null)
            {
                var targetLink = viewModel.LinkViewModel.Links.FirstOrDefault(l => l.Id == lid);
                if (targetLink == null) return;

                if (e.ClickCount == 2)
                {
                    viewModel.ShowDetailCommand.Execute(targetLink);
                    e.Handled = true;
                    return;
                }

                ClearFolderSelection();
                viewModel.LinkViewModel.ToggleSelectCommand.Execute(targetLink);
                _currentSelectedLink = targetLink.IsSelected ? targetLink : null;
                RefreshDetailPanel();
                e.Handled = true;
            }
        };

        itemRow.MouseEnter += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid)
            {
                var l = viewModel.LinkViewModel?.Links.FirstOrDefault(x => x.Id == fid);
                if (l != null && !l.IsSelected)
                    b.Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
            }
        };

        itemRow.MouseLeave += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid)
            {
                var l = viewModel.LinkViewModel?.Links.FirstOrDefault(x => x.Id == fid);
                if (l != null && !l.IsSelected)
                    b.Background = new SolidColorBrush(Colors.Transparent);
            }
        };

        return itemRow;
    }

    private void LinkViewModel_SelectionChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            UpdateSidebarSelectionVisuals(vm);
    }

    private void UpdateSidebarSelectionVisuals(MainViewModel viewModel)
    {
        if (viewModel.LinkViewModel == null) return;
        foreach (var kvp in _sidebarLinkBorders)
        {
            var border = kvp.Value;
            var linkId = kvp.Key;
            var link = viewModel.LinkViewModel.Links.FirstOrDefault(l => l.Id == linkId);
            if (link != null)
            {
                border.Background = link.IsSelected
                    ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                    : new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    public async Task RefreshSidebarAsync(MainViewModel viewModel)
    {
        await viewModel.LoadFolderTreeAsync();
        Application.Current.Dispatcher.Invoke(() => RefreshSidebar(viewModel));
    }

    public void RefreshDetailPanel()
    {
        DetailPanel.Children.Clear();

        if (_currentSelectedLink == null)
        {
            var placeholder = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 80, 0, 0)
            };
            placeholder.Children.Add(new PackIcon
            {
                Kind = PackIconKind.BookmarkOutline, Width = 48, Height = 48,
                HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.15
            });
            placeholder.Children.Add(new TextBlock
            {
                Text = "选中书签查看详情", FontSize = 13, Opacity = 0.3,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0)
            });
            DetailPanel.Children.Add(placeholder);
            return;
        }

        var link = _currentSelectedLink;

        var topIconRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };

        var linkVariantIcon = new PackIcon
        {
            Kind = PackIconKind.LinkVariant, Width = 32, Height = 32,
            Foreground = (FindResource("PrimaryHueMidBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(98, 0, 238)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var faviconBorder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var faviconGrid = new Grid();
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = !string.IsNullOrEmpty(link.FaviconUrl) ? new BitmapImage(new Uri(link.FaviconUrl, UriKind.Absolute)) : null,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (string.IsNullOrEmpty(link.FaviconUrl))
            faviconImg.Visibility = Visibility.Collapsed;
        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
            Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        if (!string.IsNullOrEmpty(link.FaviconUrl))
            earthIcon.Visibility = Visibility.Collapsed;
        faviconGrid.Children.Add(faviconImg);
        faviconGrid.Children.Add(earthIcon);
        faviconBorder.Child = faviconGrid;

        topIconRow.Children.Add(linkVariantIcon);
        topIconRow.Children.Add(faviconBorder);
        DetailPanel.Children.Add(topIconRow);

        DetailPanel.Children.Add(new TextBlock { Text = "URL", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        var urlGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var urlTb = new TextBox { Text = link.Url ?? "", FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, ContextMenu = null };
        Grid.SetColumn(urlTb, 0);
        urlGrid.Children.Add(urlTb);
        var urlCopyBtn = new Button
        {
            Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 12, Height = 12, Foreground = Brushes.Black },
            Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "复制URL",
            VerticalAlignment = VerticalAlignment.Center
        };
        var capturedUrl = link.Url ?? "";
        urlCopyBtn.Click += (s, e) => { Clipboard.SetText(capturedUrl); };
        Grid.SetColumn(urlCopyBtn, 1);
        urlGrid.Children.Add(urlCopyBtn);
        DetailPanel.Children.Add(urlGrid);

        DetailPanel.Children.Add(new TextBlock { Text = "标题", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        DetailPanel.Children.Add(new TextBox { Text = link.Title ?? "", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 12), ContextMenu = null });

        DetailPanel.Children.Add(new TextBlock { Text = "描述", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        DetailPanel.Children.Add(new TextBox { Text = link.Description ?? "（无描述）", FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 12), ContextMenu = null });

        DetailPanel.Children.Add(new TextBlock { Text = "最后更新", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        DetailPanel.Children.Add(new TextBox { Text = link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        DetailPanel.Children.Add(new TextBlock { Text = "创建时间", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        DetailPanel.Children.Add(new TextBox { Text = link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, 8), ContextMenu = null });

        DetailPanel.Children.Add(new TextBlock { Text = "ID", FontSize = 11, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) });
        DetailPanel.Children.Add(CreateValueWithCopy(link.LinkId ?? "", link.LinkId ?? "", true));
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
            Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 12, Height = 12, Foreground = Brushes.Black },
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

    public void UpdateDetailPanel(LinkItem link)
    {
        _currentSelectedLink = link;
        RefreshDetailPanel();
    }

    public void ClearDetailPanel()
    {
        _currentSelectedLink = null;
        RefreshDetailPanel();
    }

    private async void SetFolderSelection(int folderId)
    {
        _selectedFolderId = folderId;

        if (DataContext is MainViewModel vm)
        {
            vm.SelectFolder(folderId);
            if (vm.LinkViewModel != null)
            {
                vm.LinkViewModel.ClearSelectionCommand.Execute(null);
                _currentSelectedLink = null;
                RefreshDetailPanel();
            }
            await RefreshMainListAsync();
            RefreshSidebar(vm);
        }
    }

    public void ExpandFolder(int folderId)
    {
        if (folderId >= 0 && !_expandedFolders.Contains(folderId))
            _expandedFolders.Add(folderId);
    }

    private async void ClearFolderSelection()
    {
        _selectedFolderId = -1;

        if (DataContext is MainViewModel vm)
        {
            vm.ClearFolderSelectionVM();
            await RefreshMainListAsync();
            RefreshSidebar(vm);
        }
    }

    public async Task RefreshMainListAsync()
    {
        if (MainListContentPanel == null) return;
        MainListContentPanel.Children.Clear();

        if (DataContext is not MainViewModel vm) return;
        var folderItems = vm.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            await RenderMainListFolderNodeAsync(folder, MainListContentPanel, 0, vm);
        }
    }

    public void RefreshMainList()
    {
        if (MainListContentPanel == null) return;
        MainListContentPanel.Children.Clear();

        if (DataContext is not MainViewModel vm) return;
        var folderItems = vm.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            RenderMainListFolderNode(folder, MainListContentPanel, 0, vm);
        }
    }

    private async Task RenderMainListFolderNodeAsync(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _expandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectedFolderId;

        var row = CreateMainListFolderRow(folder, isExpanded, isSelected, depth, viewModel);
        container.Children.Add(row);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(depth == 0 ? 0 : 20, 0, 0, 0) };

            foreach (var child in folder.Children)
            {
                await RenderMainListFolderNodeAsync(child, childPanel, depth + 1, viewModel);
            }

            List<Data.Link> links;
            if (folder.Id == 0)
                links = await viewModel.GetRootLevelLinksAsync();
            else
            {
                var linksResult = await viewModel.GetLinksForSidebarAsync(folder.Id);
                links = linksResult.Links;
            }

            if (links != null)
            {
                foreach (var link in links)
                {
                    var card = CreateMainListLinkCard(link, viewModel);
                    childPanel.Children.Add(card);
                }
            }

            container.Children.Add(childPanel);
        }
    }

    private void RenderMainListFolderNode(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _expandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectedFolderId;

        var row = CreateMainListFolderRow(folder, isExpanded, isSelected, depth, viewModel);
        container.Children.Add(row);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(depth == 0 ? 0 : 20, 0, 0, 0) };

            foreach (var child in folder.Children)
            {
                RenderMainListFolderNode(child, childPanel, depth + 1, viewModel);
            }

            List<Data.Link> links;
            if (folder.Id == 0)
                links = viewModel.GetRootLevelLinksAsync().GetAwaiter().GetResult();
            else
            {
                var linksResult = viewModel.GetLinksForSidebarAsync(folder.Id).GetAwaiter().GetResult();
                links = linksResult.Links;
            }

            if (links != null)
            {
                foreach (var link in links)
                {
                    var card = CreateMainListLinkCard(link, viewModel);
                    childPanel.Children.Add(card);
                }
            }

            container.Children.Add(childPanel);
        }
    }

    private Border CreateMainListFolderRow(FolderNode folder, bool isExpanded, bool isSelected, int depth, MainViewModel viewModel)
    {
        var row = new Border
        {
            Margin = new Thickness(0), CornerRadius = new CornerRadius(0), Cursor = Cursors.Hand,
            Background = isSelected
                ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                : new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 5, 12, 5),
            Tag = "FolderCard"
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var chevronBorder = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Tag = folder.Id
        };
        chevronBorder.Child = new PackIcon
        {
            Width = 12, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Kind = isExpanded ? PackIconKind.ChevronDown : PackIconKind.ChevronRight,
            Opacity = 0.45
        };
        stack.Children.Add(chevronBorder);

        stack.Children.Add(new PackIcon
        {
            Kind = folder.Id == 0 ? PackIconKind.BookmarkOutline : (isExpanded ? PackIconKind.Folder : PackIconKind.FolderOutline),
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0), Opacity = 0.5
        });

        stack.Children.Add(new TextBlock
        {
            Text = folder.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68))
        });

        row.Child = stack;

        chevronBorder.PreviewMouseLeftButtonDown += async (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int fid)
            {
                if (_expandedFolders.Contains(fid))
                    _expandedFolders.Remove(fid);
                else
                    _expandedFolders.Add(fid);
                await RefreshMainListAsync();
                RefreshSidebar(viewModel);
            }
            e.Handled = true;
        };

        row.MouseLeftButtonDown += (s, e) =>
        {
            if (_selectedFolderId == folder.Id)
                ClearFolderSelection();
            else
                SetFolderSelection(folder.Id);
            e.Handled = true;
        };

        row.MouseEnter += (s, e) =>
        {
            if (s is Border b && _selectedFolderId != folder.Id)
                b.Background = new SolidColorBrush(Color.FromArgb(10, 0, 0, 0));
        };

        row.MouseLeave += (s, e) =>
        {
            if (s is Border b && _selectedFolderId != folder.Id)
                b.Background = new SolidColorBrush(Colors.Transparent);
        };

        return row;
    }

    private static StackPanel StackPanelWithTextTrimming(Data.Link link)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = !string.IsNullOrEmpty(link.Title) ? link.Title : link.Url,
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(new TextBlock
        {
            Text = link.Url, FontSize = 11, Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        });
        return sp;
    }

    private Border CreateMainListLinkCard(Data.Link link, MainViewModel viewModel)
    {
        var card = new Border
        {
            Tag = "LinkCard", Margin = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand, MaxWidth = 480,
            Background = (Brush)FindResource("MaterialDesignCardBackground"),
            BorderThickness = new Thickness(2), Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("MaterialDesignDivider")));
        style.Setters.Add(new Setter(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.08 }));
        style.Triggers.Add(new Trigger { Property = Border.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.15 }) }
        });
        card.Style = style;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        var iconGrid = new Grid();
        Image? faviconImg = null;
        try
        {
            if (!string.IsNullOrEmpty(link.FaviconUrl))
            {
                faviconImg = new Image
                {
                    Source = new BitmapImage(new Uri(link.FaviconUrl, UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
        }
        catch { }

        if (faviconImg == null)
        {
            iconGrid.Children.Add(new PackIcon
            {
                Kind = PackIconKind.Earth, Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            iconGrid.Children.Add(faviconImg);
        }
        iconBorder.Child = iconGrid;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var textStack = StackPanelWithTextTrimming(link);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        card.Child = grid;

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (viewModel.LinkViewModel == null) return;
            var targetLink = viewModel.LinkViewModel.Links.FirstOrDefault(l => l.Id == link.Id);
            if (targetLink == null)
            {
                targetLink = new LinkItem
                {
                    Id = link.Id, LinkId = link.LinkId, Url = link.Url ?? "",
                    Title = link.Title ?? "", Description = link.Description ?? "",
                    FaviconUrl = link.FaviconUrl ?? "", ListId = link.ListId,
                    CreatedAt = link.CreatedAt, UpdatedAt = link.UpdatedAt
                };
            }

            if (e.ClickCount == 2)
            {
                viewModel.ShowDetailCommand.Execute(targetLink);
                e.Handled = true;
                return;
            }

            ClearFolderSelection();
            viewModel.LinkViewModel.ToggleSelectCommand.Execute(targetLink);
            _currentSelectedLink = targetLink.IsSelected ? targetLink : null;
            RefreshDetailPanel();
            e.Handled = true;
        };

        return card;
    }

    private static FolderNode? FindFolderNode(ObservableCollection<FolderNode> nodes, int id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindFolderNode(node.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && (name == null || element.Name == name))
                return element;
            var result = FindVisualChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void LinksPage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.LinkViewModel != null)
        {
            DependencyObject? hit = e.OriginalSource as DependencyObject;
            DependencyObject? walk = hit;
            while (walk != null)
            {
                if (walk is Border b)
                {
                    if ("FolderCard".Equals(b.Tag as string)) return;
                    if ("SubFolderCard".Equals(b.Tag as string)) return;
                }
                walk = VisualTreeHelper.GetParent(walk);
            }

            LinkItem? clickedLink = null;
            walk = hit;
            while (walk != null)
            {
                if (walk is Border border && border.Tag?.ToString() == "LinkCard")
                {
                    clickedLink = border.DataContext as LinkItem;
                    break;
                }
                walk = VisualTreeHelper.GetParent(walk);
            }

            if (clickedLink == null)
            {
                viewModel.LinkViewModel.ClearSelectionCommand.Execute(null);
                _currentSelectedLink = null;
                ClearFolderSelection();
                RefreshDetailPanel();
                return;
            }

            ClearFolderSelection();

            Dispatcher.BeginInvoke(() =>
            {
                _currentSelectedLink = clickedLink.IsSelected ? clickedLink : null;
                RefreshDetailPanel();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void RootGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MainView.Visibility != Visibility.Visible) return;

        DependencyObject? hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;

        DependencyObject? current = hit;
        while (current != null)
        {
            if (current == LinksPage) return;
            if (current == RightSidebarBorder) return;
            if (current == DetailPanel) return;
            current = VisualTreeHelper.GetParent(current);
        }

        current = hit;
        while (current != null)
        {
            if (current is Border border)
            {
                if ("LinkCard".Equals(border.Tag as string)) return;
                if ("FolderCard".Equals(border.Tag as string)) return;
                if (border.Tag is int) return;
            }
            if (current is System.Windows.Controls.Primitives.ButtonBase) return;
            current = VisualTreeHelper.GetParent(current);
        }

        if (DataContext is MainViewModel vm && vm.LinkViewModel != null)
        {
            vm.LinkViewModel.ClearSelectionCommand.Execute(null);
            _currentSelectedLink = null;
            ClearFolderSelection();
            RefreshDetailPanel();
        }
    }
}

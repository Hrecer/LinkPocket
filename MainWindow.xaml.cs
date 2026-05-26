using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly HashSet<int> _sidebarExpandedFolders = new() { 0 };
    private readonly HashSet<int> _mainExpandedFolders = new() { 0 };
    private readonly Dictionary<int, Border> _sidebarLinkBorders = new();
    private readonly Dictionary<int, Border> _mainListCardBorders = new();
    private readonly Dictionary<int, Border> _sidebarFolderBorders = new();
    private readonly Dictionary<int, Border> _mainListFolderBorders = new();
    private bool _updatingSelectionVisuals;
    private readonly Managers.SelectionManager _selectionManager = new();
    private readonly Managers.ClipboardManager _clipboardManager = new();

    private Border? _selectedSearchCard;
    private LinkItem? _selectedSearchItem;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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

    private void NewFolderOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelCreateFolderCommand.Execute(null);
    }

    private void NewFolderDialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void NewFolderNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(vm.NewFolderName))
                vm.ConfirmCreateFolderCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel vm)
                vm.CancelCreateFolderCommand.Execute(null);
            e.Handled = true;
        }
    }

    public void FocusNewFolderDialog()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NewFolderNameBox?.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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

    private static readonly Dictionary<string, string> SortFieldLabels = new()
    {
        { "title", "按名称" }, { "updated_at", "最后更新" },
        { "last_visited_at", "最后查看" }, { "visit_count", "累计查看次数" }, { "created_at", "创建时间" }
    };

    private void LinkSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        UpdateLinkSortMenu(vm);
        LinkSortMenu.PlacementTarget = LinkSortButton;
        LinkSortMenu.IsOpen = true;
    }

    private void UpdateLinkSortMenu(MainViewModel vm)
    {
        foreach (MenuItem item in LinkSortMenu.Items)
        {
            var field = item.Tag as string;
            if (field == null) continue;
            var isActive = field == vm.LinkSortField;
            var arrow = isActive ? (vm.LinkSortOrder == "asc" ? " ↑" : " ↓") : "";
            var check = isActive ? "✓ " : "   ";
            item.Header = $"{check}{SortFieldLabels.GetValueOrDefault(field, field)}{arrow}";
        }
        if (SortFieldLabels.TryGetValue(vm.LinkSortField, out var label))
            LinkSortButtonText.Text = label;
        else
            LinkSortButtonText.Text = "排序";
        LinkSortOrderText.Text = vm.LinkSortOrder == "asc" ? "↑ 升序" : "↓ 降序";
        LinkSortButton.ToolTip = $"书签排序：{label} {(vm.LinkSortOrder == "asc" ? "升序" : "降序")}";
    }

    private async void LinkSortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string field) return;
        if (DataContext is not MainViewModel vm) return;
        await vm.SetLinkSortAsync(field);
        UpdateLinkSortMenu(vm);
    }

    private async void FolderSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        await vm.ToggleFolderSortAsync();
        FolderSortOrderText.Text = vm.FolderSortOrder == "asc" ? "↑ 升序" : "↓ 降序";
        FolderSortButton.ToolTip = $"文件夹按名称{(vm.FolderSortOrder == "asc" ? "升序" : "降序")}排列";
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
            UpdateLinkSortMenu(viewModel);
            FolderSortOrderText.Text = viewModel.FolderSortOrder == "asc" ? "↑ 升序" : "↓ 降序";
            if (viewModel.LinkViewModel != null)
            {
                viewModel.LinkViewModel.SelectionChanged += LinkViewModel_SelectionChanged;
                await viewModel.LinkViewModel.LoadLinksAsync();
            }
            PrefetchFavicons(viewModel);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isMultiSelect = false;
        if (DataContext is MainViewModel vm && vm.LinkViewModel != null)
        {
            isMultiSelect = vm.LinkViewModel.HasSelectedItems;
        }

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (IsInTextInput()) return;
            CopySelectedLink();
            e.Handled = true;
        }
        else if (e.Key == Key.X && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (IsInTextInput()) return;
            CutSelectedLink();
            e.Handled = true;
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (IsInTextInput()) return;
            if (_selectionManager.SelectedFolderId < 0) return;
            _ = PasteLinksAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (DataContext is MainViewModel mvm)
            {
                if (isMultiSelect && mvm.LinkViewModel != null)
                {
                    mvm.LinkViewModel.DeleteSelectedCommand.Execute(null);
                    _ = RefreshMainListAsync();
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel escVm)
            {
                if (_clipboardManager.HasClipboard)
                {
                    _clipboardManager.Clear();
                    ClearCutVisuals();
                }
                if (escVm.LinkViewModel?.HasSelectedItems == true)
                {
                    escVm.LinkViewModel.ClearSelectionCommand.Execute(null);
                    _selectionManager.NotifyMultiSelectEnded();
                    UpdateMainListSelectionVisuals();
                }
                if (_selectionManager.CurrentSelectedLink != null)
                {
                    _selectionManager.ClearCurrentSelectedLink();
                    ClearDetailPanel();
                }
                if (_selectedSearchCard != null)
                {
                    _selectedSearchCard.BorderBrush = (Brush)FindResource("MaterialDesignDivider");
                    _selectedSearchCard = null;
                    _selectedSearchItem = null;
                    ResetDetailPanelPlaceholder(SearchFixedSidebar);
                    SearchJumpToLinkBtn.IsEnabled = false;
                }
            }
            e.Handled = true;
        }
    }

    private static bool IsInTextInput()
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox;
    }

    private void CopySelectedLink()
    {
        if (DataContext is MainViewModel vm && vm.LinkViewModel != null && vm.LinkViewModel.HasSelectedItems)
        {
            _clipboardManager.Copy(vm.LinkViewModel.SelectedItems.ToList());
            return;
        }

        var currentLink = _selectionManager.CurrentSelectedLink;
        if (currentLink == null) return;
        _clipboardManager.Copy(new List<LinkItem> { currentLink });
    }

    private void CutSelectedLink()
    {
        if (DataContext is MainViewModel vm && vm.LinkViewModel != null && vm.LinkViewModel.HasSelectedItems)
        {
            var items = vm.LinkViewModel.SelectedItems.ToList();
            _clipboardManager.Cut(items);
            foreach (var cutItem in items)
            {
                if (_mainListCardBorders.TryGetValue(cutItem.Id, out var cutCard))
                    cutCard.Opacity = 0.4;
                if (_sidebarLinkBorders.TryGetValue(cutItem.Id, out var cutSidebar))
                    cutSidebar.Opacity = 0.4;
            }
            return;
        }

        var currentLink = _selectionManager.CurrentSelectedLink;
        if (currentLink == null) return;
        _clipboardManager.Cut(new List<LinkItem> { currentLink });

        currentLink.IsCut = true;
        if (_mainListCardBorders.TryGetValue(currentLink.Id, out var card))
            card.Opacity = 0.4;
        if (_sidebarLinkBorders.TryGetValue(currentLink.Id, out var sidebarCard))
            sidebarCard.Opacity = 0.4;
    }

    private async Task PasteLinksAsync()
    {
        var links = _clipboardManager.ClipboardLinks;
        if (links == null || links.Count == 0) return;
        if (DataContext is not MainViewModel vm) return;

        int sourceFolder = links[0].ListId ?? 0;
        int targetFolder = _selectionManager.SelectedFolderId;

        if (sourceFolder == targetFolder)
        {
            if (_clipboardManager.IsCut)
            {
                MessageBox.Show("源目录与目标目录相同，无法剪切到同一目录", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else
            {
                var result = MessageBox.Show("目标目录与源目录相同，是否继续复制？", "提示",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }
        }

        ExpandFolder(targetFolder);
        await vm.PasteLinksToFolderAsync(links, _clipboardManager.IsCut);

        await RefreshMainListAsync();

        if (_clipboardManager.IsCut)
        {
            ClearCutVisuals();
        }
        _clipboardManager.AfterPaste();
        _selectionManager.NotifyMultiSelectEnded();
    }

    private void ClearCutVisuals()
    {
        foreach (var kvp in _mainListCardBorders)
        {
            kvp.Value.Opacity = 1.0;
        }
        foreach (var kvp in _sidebarLinkBorders)
        {
            kvp.Value.Opacity = 1.0;
        }
    }

    private void UpdateCutVisuals()
    {
        foreach (var link in GetAllLinkItems())
        {
            if (link.IsCut && _mainListCardBorders.TryGetValue(link.Id, out var card))
            {
                card.Opacity = 0.4;
            }
        }
    }

    private void UpdateMainListSelectionVisuals()
    {
        if (DataContext is not MainViewModel vm || vm.LinkViewModel == null) return;

        var defaultBrush = (Brush)FindResource("MaterialDesignDivider");
        var selectedBrush = new SolidColorBrush(Color.FromRgb(98, 0, 238));
        foreach (var linkItem in vm.LinkViewModel.Links)
        {
            if (_mainListCardBorders.TryGetValue(linkItem.Id, out var card))
            {
                if (linkItem.IsSelected || _selectionManager.CurrentSelectedLink?.Id == linkItem.Id)
                {
                    card.BorderBrush = selectedBrush;
                }
                else
                {
                    card.BorderBrush = defaultBrush;
                }
            }
        }
    }

    private List<LinkItem> GetAllLinkItems()
    {
        var items = new List<LinkItem>();
        CollectLinkItems(MainListContentPanel, items);
        return items;
    }

    private static void CollectLinkItems(Panel panel, List<LinkItem> items)
    {
        foreach (var child in panel.Children)
        {
            if (child is Border border && border.Tag?.ToString() == "LinkCard" && border.DataContext is LinkItem link)
            {
                items.Add(link);
            }
            else if (child is Panel childPanel)
            {
                CollectLinkItems(childPanel, items);
            }
        }
    }

    private static async void PrefetchFavicons(MainViewModel viewModel)
    {
        try
        {
            var links = await viewModel.GetAllLinksAsync();
            var urls = links.Select(l => l.FaviconUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct();
            foreach (var url in urls)
            {
                await FaviconService.PrefetchAndCacheAsync(url);
            }
        }
        catch { }
    }

    public void RefreshSidebar(MainViewModel viewModel)
    {
        FolderListPanel.Children.Clear();
        _sidebarLinkBorders.Clear();
        _sidebarFolderBorders.Clear();

        var folderItems = viewModel.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            RenderFolderNode(folder, FolderListPanel, 0, viewModel);
        }

        UpdateSidebarSelectionVisuals();
    }

    private void RenderFolderNode(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _sidebarExpandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectionManager.SelectedFolderId;

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

        if (folder.TotalLinkCount > 0)
        {
            stack.Children.Add(new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(30, 98, 0, 238)),
                Child = new TextBlock
                {
                    Text = folder.TotalLinkCount.ToString(), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        row.Child = stack;
        _sidebarFolderBorders[folder.Id] = row;

        chevronBorder.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int folderId)
            {
                if (_sidebarExpandedFolders.Contains(folderId))
                    _sidebarExpandedFolders.Remove(folderId);
                else
                    _sidebarExpandedFolders.Add(folderId);

                RefreshSidebar(viewModel);
            }
            e.Handled = true;
        };

        row.MouseLeftButtonDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                return;
            }
            if (s is FrameworkElement fe && fe.Tag is int folderId)
            {
                if (_selectionManager.SelectedFolderId == folderId)
                    ClearFolderSelection();
                else
                    SetFolderSelection(folderId);
            }
            e.Handled = true;
        };

        row.MouseEnter += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid && fid != _selectionManager.SelectedFolderId)
                b.Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
        };

        row.MouseLeave += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid && fid != _selectionManager.SelectedFolderId)
                b.Background = new SolidColorBrush(Colors.Transparent);
        };

        row.MouseRightButtonUp += (s, e) =>
        {
            _selectionManager.SelectFolder(folder.Id);
            if (DataContext is MainViewModel vm)
                vm.SelectFolder(folder.Id);
            var ctxMenu = new ContextMenu();

            if (_clipboardManager.HasClipboard)
            {
                var pasteItem = new MenuItem { Header = "粘贴到此处", Icon = new PackIcon { Kind = PackIconKind.ContentPaste, Width = 16, Height = 16 } };
                pasteItem.Click += async (cs, ce) =>
                {
                    await PasteLinksAsync();
                    ctxMenu.IsOpen = false;
                };
                ctxMenu.Items.Add(pasteItem);
            }
            else
            {
                var emptyItem = new MenuItem { Header = "（剪贴板为空）", IsEnabled = false };
                ctxMenu.Items.Add(emptyItem);
            }

            ctxMenu.PlacementTarget = row;
            ctxMenu.IsOpen = true;
            e.Handled = true;
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

        var faviconBmp = TryLoadFavicon(link.FaviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (faviconBmp == null)
            faviconImg.Visibility = Visibility.Collapsed;

        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
            Width = 11, Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        if (faviconBmp != null)
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
            if (s is Border br && br.Tag is int lid)
            {
                var targetLink = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == lid);
                if (targetLink == null)
                {
                    targetLink = new LinkItem
                    {
                        Id = lid, Url = link.Url ?? "", Title = link.Title ?? "",
                        Description = link.Description ?? "", FaviconUrl = link.FaviconUrl ?? "",
                        ListId = link.ListId, CreatedAt = link.CreatedAt, UpdatedAt = link.UpdatedAt
                    };
                }

                if (e.ClickCount == 2)
                {
                    viewModel.ShowDetailCommand.Execute(targetLink);
                    e.Handled = true;
                    return;
                }

                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    var prevSelectedBeforePromotion = _selectionManager.CurrentSelectedLink;
                    var ctrlResult = _selectionManager.HandleCtrlClick(targetLink);
                    if (ctrlResult == Managers.SelectionManager.CtrlClickResult.BlockedCrossDirectory)
                    {
                        e.Handled = true;
                        return;
                    }

                    if (ctrlResult == Managers.SelectionManager.CtrlClickResult.Promoted && prevSelectedBeforePromotion != null)
                    {
                        var promotedLink = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == prevSelectedBeforePromotion.Id);
                        if (promotedLink != null && !promotedLink.IsSelected)
                        {
                            promotedLink.IsSelected = true;
                            if (_sidebarLinkBorders.TryGetValue(promotedLink.Id, out var prevSidebar))
                                prevSidebar.Background = new SolidColorBrush(Color.FromArgb(25, 98, 0, 238));
                        }
                        viewModel.NotifyLinkSelected(false);
                        ClearDetailPanel();
                    }

                    targetLink.IsSelected = !targetLink.IsSelected;
                    viewModel.LinkViewModel?.NotifySelectionStateChanged();
                    viewModel.NotifyLinkSelected(viewModel.LinkViewModel?.HasSelectedItems == true);
                    if (viewModel.LinkViewModel?.HasSelectedItems == false)
                        _selectionManager.NotifyMultiSelectEnded();
                    UpdateMainListSelectionVisuals();
                    e.Handled = true;
                    return;
                }

                _clipboardManager.Clear();
                ClearCutVisuals();

                if (viewModel.LinkViewModel?.HasSelectedItems == true)
                {
                    viewModel.LinkViewModel.ClearSelectionCommand.Execute(null);
                    _selectionManager.NotifyMultiSelectEnded();
                }

                _selectionManager.HandleSingleClick(targetLink);
                UpdateSidebarSelectionVisuals();
                UpdateMainListSelectionVisuals();
                RefreshDetailPanel();
                viewModel.NotifyLinkSelected(_selectionManager.CurrentSelectedLink != null);
                e.Handled = true;
            }
        };

        itemRow.MouseEnter += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid)
            {
                if (_selectionManager.CurrentSelectedLink?.Id == fid) return;
                if (viewModel.LinkViewModel?.Links.Any(l => l.IsSelected && l.Id == fid) == true) return;
                b.Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
            }
        };

        itemRow.MouseLeave += (s, e) =>
        {
            var b = (Border)s;
            if (b.Tag is int fid)
            {
                if (_selectionManager.CurrentSelectedLink?.Id == fid) return;
                if (viewModel.LinkViewModel?.Links.Any(l => l.IsSelected && l.Id == fid) == true) return;
                b.Background = new SolidColorBrush(Colors.Transparent);
            }
        };

        return itemRow;
    }

    private void LinkViewModel_SelectionChanged(object? sender, EventArgs e)
    {
        UpdateSidebarSelectionVisuals();
    }

    private void UpdateSidebarSelectionVisuals()
    {
        if (_updatingSelectionVisuals) return;
        _updatingSelectionVisuals = true;
        try
        {
            foreach (var kvp in _sidebarFolderBorders)
            {
                var border = kvp.Value;
                var folderId = kvp.Key;
                bool isSelected = folderId == _selectionManager.SelectedFolderId;
                border.Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                    : new SolidColorBrush(Colors.Transparent);
            }

            foreach (var kvp in _sidebarLinkBorders)
            {
                var border = kvp.Value;
                var linkId = kvp.Key;
                bool isSelected = _selectionManager.CurrentSelectedLink?.Id == linkId;
                if (!isSelected && DataContext is MainViewModel svm && svm.LinkViewModel != null)
                {
                    isSelected = svm.LinkViewModel.Links.Any(l => l.IsSelected && l.Id == linkId);
                }
                border.Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                    : new SolidColorBrush(Colors.Transparent);
            }

            foreach (var kvp in _mainListFolderBorders)
            {
                var border = kvp.Value;
                var folderId = kvp.Key;
                bool isSelected = folderId == _selectionManager.SelectedFolderId;
                border.Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(25, 98, 0, 238))
                    : new SolidColorBrush(Colors.Transparent);
            }

            foreach (var kvp in _mainListCardBorders)
            {
                var border = kvp.Value;
                var linkId = kvp.Key;
                bool isSelected = _selectionManager.CurrentSelectedLink?.Id == linkId;
                if (!isSelected && DataContext is MainViewModel mvm && mvm.LinkViewModel != null)
                {
                    isSelected = mvm.LinkViewModel.Links.Any(l => l.IsSelected && l.Id == linkId);
                }
                border.BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(98, 0, 238))
                    : (Brush)FindResource("MaterialDesignDivider");
            }
        }
        finally
        {
            _updatingSelectionVisuals = false;
        }
    }

    public async Task RefreshSidebarAsync(MainViewModel viewModel)
    {
        await viewModel.LoadFolderTreeAsync();
        Application.Current.Dispatcher.Invoke(() => RefreshSidebar(viewModel));
    }

    public void RefreshDetailPanel()
    {
        if (_selectionManager.CurrentSelectedLink == null)
        {
            ResetDetailPanelPlaceholder(DetailPanel);
            return;
        }

        var link = _selectionManager.CurrentSelectedLink;
        var folderName = link.ListId.HasValue ? FindFolderNameForLink(link.ListId.Value) : "根目录";
        PopulateDetailPanel(DetailPanel, link.Url, link.Title, link.Description, link.FaviconUrl,
            link.UpdatedAt, link.LastVisitedAt, link.VisitCount, link.CreatedAt, link.LinkId, folderName);
    }

    private static void PopulateDetailPanel(Panel panel, string url, string? title, string? description,
        string? faviconUrl, DateTime updatedAt, DateTime? lastVisitedAt, int visitCount,
        DateTime createdAt, string? linkId, string folderName)
    {
        panel.Children.Clear();

        var topIconRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };

        var linkVariantIcon = new PackIcon
        {
            Kind = PackIconKind.LinkVariant, Width = 32, Height = 32,
            Foreground = Application.Current.FindResource("PrimaryHueMidBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(98, 0, 238)),
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
        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
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

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        folderRow.Children.Add(new PackIcon { Kind = PackIconKind.FolderOutline, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)), Opacity = 0.6 });
        folderRow.Children.Add(new TextBlock { Text = folderName ?? "根目录", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
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
            Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 12, Height = 12, Foreground = Brushes.Black },
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

    private static string FindFolderNameForLink(int? listId)
    {
        if (!listId.HasValue || listId.Value == 0)
            return "根目录";

        if (Application.Current.MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
            return FindFolderNameInNodes(vm.FolderItems, listId.Value);

        return "未知目录";
    }

    private static string FindFolderNameInNodes(ObservableCollection<FolderNode> nodes, int folderId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == folderId)
                return node.Name;
            if (node.Children != null && node.Children.Count > 0)
            {
                var name = FindFolderNameInNodes(node.Children, folderId);
                if (name != null)
                    return name;
            }
        }
        return "未知目录";
    }

    public void UpdateDetailPanel(LinkItem link)
    {
        _selectionManager.HandleSingleClick(link);
        UpdateSidebarSelectionVisuals();
        RefreshDetailPanel();
        if (DataContext is MainViewModel vm)
            vm.NotifyLinkSelected(link != null);
    }

    public void ClearDetailPanel()
    {
        _selectionManager.ClearCurrentSelectedLink();
        UpdateSidebarSelectionVisuals();
        RefreshDetailPanel();
        if (DataContext is MainViewModel vm)
            vm.NotifyLinkSelected(false);
    }

    public LinkItem? GetSelectedLink() => _selectionManager.CurrentSelectedLink;

    private async void SetFolderSelection(int folderId)
    {
        _selectionManager.SelectFolder(folderId);

        if (DataContext is MainViewModel vm)
        {
            vm.SelectFolder(folderId);
            if (vm.LinkViewModel != null)
            {
                vm.LinkViewModel.ClearSelectionCommand.Execute(null);
                vm.NotifyLinkSelected(false);
                UpdateSidebarSelectionVisuals();
                RefreshDetailPanel();
            }
            await RefreshMainListAsync();
            RefreshSidebar(vm);
        }
    }

    public void ExpandFolder(int folderId)
    {
        if (folderId >= 0)
        {
            if (!_sidebarExpandedFolders.Contains(folderId))
                _sidebarExpandedFolders.Add(folderId);
            if (!_mainExpandedFolders.Contains(folderId))
                _mainExpandedFolders.Add(folderId);
        }
    }

    public async void ClearFolderSelection()
    {
        _selectionManager.ClearFolderSelection();

        if (DataContext is MainViewModel vm)
        {
            vm.ClearFolderSelectionVM();
            UpdateSidebarSelectionVisuals();
            RefreshDetailPanel();
            await RefreshMainListAsync();
            RefreshSidebar(vm);
        }
    }

    public async Task RefreshMainListAsync()
    {
        if (MainListContentPanel == null) return;
        MainListContentPanel.Children.Clear();
        _mainListCardBorders.Clear();
        _mainListFolderBorders.Clear();
        ClearCutVisuals();

        if (DataContext is not MainViewModel vm) return;
        var folderItems = vm.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            await RenderMainListFolderNodeAsync(folder, MainListContentPanel, 0, vm);
        }

        UpdateSidebarSelectionVisuals();
        UpdateCutVisuals();
        UpdateMainListSelectionVisuals();
    }

    public void RefreshMainList()
    {
        if (MainListContentPanel == null) return;
        MainListContentPanel.Children.Clear();
        _mainListCardBorders.Clear();
        _mainListFolderBorders.Clear();
        ClearCutVisuals();

        if (DataContext is not MainViewModel vm) return;
        var folderItems = vm.FolderItems;
        if (folderItems == null) return;

        foreach (var folder in folderItems)
        {
            RenderMainListFolderNode(folder, MainListContentPanel, 0, vm);
        }

        UpdateSidebarSelectionVisuals();
        UpdateCutVisuals();
        UpdateMainListSelectionVisuals();
    }

    private async Task RenderMainListFolderNodeAsync(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _mainExpandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectionManager.SelectedFolderId;

        var row = CreateMainListFolderRow(folder, isExpanded, isSelected, depth, viewModel);
        container.Children.Add(row);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };

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
        bool isExpanded = _mainExpandedFolders.Contains(folder.Id);
        bool isSelected = folder.Id == _selectionManager.SelectedFolderId;

        var row = CreateMainListFolderRow(folder, isExpanded, isSelected, depth, viewModel);
        container.Children.Add(row);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };

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

        if (folder.TotalLinkCount > 0)
        {
            stack.Children.Add(new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(30, 98, 0, 238)),
                Child = new TextBlock
                {
                    Text = folder.TotalLinkCount.ToString(), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        row.Child = stack;
        _mainListFolderBorders[folder.Id] = row;

        chevronBorder.PreviewMouseLeftButtonDown += async (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int fid)
            {
                if (_mainExpandedFolders.Contains(fid))
                    _mainExpandedFolders.Remove(fid);
                else
                    _mainExpandedFolders.Add(fid);
                await RefreshMainListAsync();
            }
            e.Handled = true;
        };

        row.MouseLeftButtonDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                return;
            }
            if (_selectionManager.SelectedFolderId == folder.Id)
                ClearFolderSelection();
            else
                SetFolderSelection(folder.Id);
            e.Handled = true;
        };

        row.MouseEnter += (s, e) =>
        {
            if (s is Border b && _selectionManager.SelectedFolderId != folder.Id)
                b.Background = new SolidColorBrush(Color.FromArgb(10, 0, 0, 0));
        };

        row.MouseLeave += (s, e) =>
        {
            if (s is Border b && _selectionManager.SelectedFolderId != folder.Id)
                b.Background = new SolidColorBrush(Colors.Transparent);
        };

        row.MouseRightButtonUp += (s, e) =>
        {
            _selectionManager.SelectFolder(folder.Id);
            if (DataContext is MainViewModel vm)
                vm.SelectFolder(folder.Id);
            var ctxMenu = new ContextMenu();

            if (_clipboardManager.HasClipboard)
            {
                var pasteItem = new MenuItem { Header = "粘贴到此处", Icon = new PackIcon { Kind = PackIconKind.ContentPaste, Width = 16, Height = 16 } };
                pasteItem.Click += async (cs, ce) =>
                {
                    await PasteLinksAsync();
                    ctxMenu.IsOpen = false;
                };
                ctxMenu.Items.Add(pasteItem);
            }
            else
            {
                var emptyItem = new MenuItem { Header = "（剪贴板为空）", IsEnabled = false };
                ctxMenu.Items.Add(emptyItem);
            }

            ctxMenu.PlacementTarget = row;
            ctxMenu.IsOpen = true;
            e.Handled = true;
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
            Cursor = Cursors.Hand, Width = 720, HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("MaterialDesignCardBackground"),
            BorderThickness = new Thickness(2), Padding = new Thickness(16, 12, 16, 12)
        };

        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("MaterialDesignDivider")));
        style.Setters.Add(new Setter(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.08 }));
        style.Triggers.Add(new Trigger { Property = Border.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.15 }) }
        });
        card.Style = style;

        _mainListCardBorders[link.Id] = card;

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

        var faviconBmp = TryLoadFavicon(link.FaviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (faviconBmp == null)
            faviconImg.Visibility = Visibility.Collapsed;

        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
            Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };
        if (faviconBmp != null)
            earthIcon.Visibility = Visibility.Collapsed;

        iconGrid.Children.Add(faviconImg);
        iconGrid.Children.Add(earthIcon);

        if (!string.IsNullOrWhiteSpace(link.FaviconUrl) && faviconBmp == null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FaviconService.PrefetchAndCacheAsync(link.FaviconUrl);
                    var cached = FaviconService.LoadFromCache(link.FaviconUrl);
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

        var textStack = StackPanelWithTextTrimming(link);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        card.Child = grid;

        var linkItemRef = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == link.Id);
        card.MouseRightButtonUp += (s, e) =>
        {
            var ctxMenu = new ContextMenu();
            var targetLink = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == link.Id) ?? linkItemRef;
            if (targetLink == null) return;

            _selectionManager.HandleSingleClick(targetLink);
            UpdateMainListSelectionVisuals();
            UpdateSidebarSelectionVisuals();
            RefreshDetailPanel();

            var copyItem = new MenuItem { Header = "复制", Icon = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 16, Height = 16 } };
            copyItem.Click += (cs, ce) =>
            {
                _clipboardManager.Copy(new List<LinkItem> { targetLink });
                ctxMenu.IsOpen = false;
            };
            ctxMenu.Items.Add(copyItem);

            var cutItem = new MenuItem { Header = "剪切", Icon = new PackIcon { Kind = PackIconKind.Scissors, Width = 16, Height = 16 } };
            cutItem.Click += (cs, ce) =>
            {
                CutSelectedLink();
                ctxMenu.IsOpen = false;
            };
            ctxMenu.Items.Add(cutItem);

            ctxMenu.Items.Add(new Separator());

            if (_clipboardManager.HasClipboard)
            {
                var pasteItem = new MenuItem { Header = "粘贴到选中文件夹", Icon = new PackIcon { Kind = PackIconKind.ContentPaste, Width = 16, Height = 16 } };
                pasteItem.Click += async (cs, ce) =>
                {
                    await PasteLinksAsync();
                    ctxMenu.IsOpen = false;
                };
                pasteItem.IsEnabled = _selectionManager.SelectedFolderId >= 0;
                pasteItem.ToolTip = _selectionManager.SelectedFolderId < 0 ? "请先选择一个目标文件夹" : "";
                ctxMenu.Items.Add(pasteItem);
            }

            ctxMenu.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "删除", Icon = new PackIcon { Kind = PackIconKind.Delete, Width = 16, Height = 16 } };
            deleteItem.Click += (cs, ce) =>
            {
                if (viewModel.LinkViewModel != null)
                    viewModel.LinkViewModel.DeleteSelectedCommand.Execute(null);
                ctxMenu.IsOpen = false;
                _ = RefreshMainListAsync();
            };
            ctxMenu.Items.Add(deleteItem);

            ctxMenu.PlacementTarget = card;
            ctxMenu.IsOpen = true;
            e.Handled = true;
        };

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            var targetLink = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == link.Id);
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

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                var prevSelectedBeforePromotion = _selectionManager.CurrentSelectedLink;
                var ctrlResult = _selectionManager.HandleCtrlClick(targetLink);
                if (ctrlResult == Managers.SelectionManager.CtrlClickResult.BlockedCrossDirectory)
                {
                    e.Handled = true;
                    return;
                }

                if (_selectionManager.ActiveFolderId < 0)
                {
                    viewModel.ClearFolderSelectionVM();
                }

                if (ctrlResult == Managers.SelectionManager.CtrlClickResult.Promoted && prevSelectedBeforePromotion != null)
                {
                    var promotedLink = viewModel.LinkViewModel?.Links.FirstOrDefault(l => l.Id == prevSelectedBeforePromotion.Id);
                    if (promotedLink != null && !promotedLink.IsSelected)
                    {
                        promotedLink.IsSelected = true;
                        if (_mainListCardBorders.TryGetValue(promotedLink.Id, out var prevCard))
                            prevCard.BorderBrush = new SolidColorBrush(Color.FromRgb(98, 0, 238));
                    }
                    viewModel.NotifyLinkSelected(false);
                    ClearDetailPanel();
                }

                targetLink.IsSelected = !targetLink.IsSelected;
                card.BorderBrush = targetLink.IsSelected
                    ? new SolidColorBrush(Color.FromRgb(98, 0, 238))
                    : (Brush)FindResource("MaterialDesignDivider");
                viewModel.LinkViewModel?.NotifySelectionStateChanged();
                viewModel.NotifyLinkSelected(viewModel.LinkViewModel?.HasSelectedItems == true);
                if (viewModel.LinkViewModel?.HasSelectedItems == false)
                {
                    _selectionManager.NotifyMultiSelectEnded();
                }
                UpdateMainListSelectionVisuals();
                e.Handled = true;
                return;
            }

            if (viewModel.LinkViewModel?.HasSelectedItems == true)
            {
                viewModel.LinkViewModel.ClearSelectionCommand.Execute(null);
                _selectionManager.NotifyMultiSelectEnded();
            }

            _clipboardManager.Clear();
            ClearCutVisuals();

            _selectionManager.HandleSingleClick(targetLink);
            UpdateMainListSelectionVisuals();
            UpdateSidebarSelectionVisuals();
            RefreshDetailPanel();
            viewModel.NotifyLinkSelected(_selectionManager.CurrentSelectedLink != null);
            e.Handled = true;
        };

        return card;
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
        SearchResultsPanel.Children.Clear();
        SearchResultsPanel.Children.Add(new TextBlock
        {
            Text = "输入关键词开始搜索", FontSize = 14, Opacity = 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0)
        });
        if (DataContext is MainViewModel sortVm)
            UpdateSearchSortMenu(sortVm);
        SearchBox.Focus();
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
        SearchResultsPanel.Children.Clear();
        SearchResultsPanel.Children.Add(new TextBlock
        {
            Text = "输入关键词开始搜索", FontSize = 14, Opacity = 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0)
        });
        SearchBox.Focus();
    }

    private async Task ExecuteTitleSearchAsync(MainViewModel vm, string query)
    {
        SearchResultsPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultsPanel.Children.Add(new TextBlock
            {
                Text = "输入关键词开始搜索", FontSize = 14, Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0)
            });
            return;
        }

        SearchResultsPanel.Children.Add(new TextBlock
        {
            Text = "搜索中...", FontSize = 14, Opacity = 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0)
        });

        try
        {
            var results = await vm.SearchLinksByTitleAsync(query);
            SearchResultsPanel.Children.Clear();
            _selectedSearchCard = null;
            _selectedSearchItem = null;
            ResetDetailPanelPlaceholder(SearchFixedSidebar);
            SearchJumpToLinkBtn.IsEnabled = false;

            if (results.Count == 0)
            {
                var notFoundPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
                notFoundPanel.Children.Add(new PackIcon { Kind = PackIconKind.EmoticonSadOutline, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.15 });
                notFoundPanel.Children.Add(new TextBlock { Text = $"未找到包含 \"{query}\" 的书签", FontSize = 14, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
                SearchResultsPanel.Children.Add(notFoundPanel);
                return;
            }

            foreach (var link in results)
            {
                var card = CreateSearchResultCard(link, vm);
                SearchResultsPanel.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            SearchResultsPanel.Children.Clear();
            SearchResultsPanel.Children.Add(new TextBlock
            {
                Text = $"搜索出错: {ex.Message}", FontSize = 14, Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0)
            });
        }
    }

    private Border CreateSearchResultCard(LinkItem item, MainViewModel vm)
    {
        var card = new Border
        {
            Tag = "SearchCard", Margin = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand, Width = 720, HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("MaterialDesignCardBackground"),
            BorderThickness = new Thickness(2), Padding = new Thickness(16, 12, 16, 12)
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

        var faviconBmp = TryLoadFavicon(item.FaviconUrl);
        var faviconImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = faviconBmp,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (faviconBmp == null)
            faviconImg.Visibility = Visibility.Collapsed;

        var earthIcon = new PackIcon
        {
            Kind = PackIconKind.Earth,
            Width = 20, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
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

        var displayTitle = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url;
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = displayTitle, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textStack.Children.Add(new TextBlock
        {
            Text = item.Url, FontSize = 11, Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        card.Child = grid;

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (_selectedSearchCard != null && _selectedSearchCard != card)
                _selectedSearchCard.BorderBrush = (Brush)FindResource("MaterialDesignDivider");

            _selectedSearchCard = card;
            _selectedSearchItem = item;
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(98, 0, 238));

            PopulateDetailPanel(SearchFixedSidebar, item.Url, item.Title, item.Description, item.FaviconUrl,
                item.UpdatedAt, item.LastVisitedAt, item.VisitCount, item.CreatedAt, item.LinkId,
                FindFolderNameForLink(item.ListId));

            SearchJumpToLinkBtn.IsEnabled = true;

            if (e.ClickCount == 2)
            {
                vm.ShowDetailCommand.Execute(item);
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
            _selectedSearchCard.BorderBrush = (Brush)FindResource("MaterialDesignDivider");
            _selectedSearchCard = null;
            _selectedSearchItem = null;
            ResetDetailPanelPlaceholder(SearchFixedSidebar);
            SearchJumpToLinkBtn.IsEnabled = false;
        }
    }

    private void SearchJumpToLinkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchItem != null)
            JumpToLinkInMainList(_selectedSearchItem.Id);
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

    private void JumpToLinkInMainList(int linkId)
    {
        if (DataContext is not MainViewModel vm) return;

        _selectedSearchCard = null;
        _selectedSearchItem = null;

        var targetLink = vm.LinkViewModel?.Links.FirstOrDefault(l => l.Id == linkId);
        if (targetLink == null) return;

        vm.CurrentNavId = "links";

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(100);

            if (vm.LinkViewModel == null) return;

            if (!vm.FolderItems.Any(f => f.Id == targetLink.ListId))
            {
                await vm.RefreshFolderTreeAndUIAsync();
            }

            if (targetLink.ListId.HasValue)
            {
                var targetNode = FindFolderNode(vm.FolderItems, targetLink.ListId.Value);
                if (targetNode != null)
                {
                    vm.SelectFolder(targetNode.Id);
                    await Task.Delay(150);
                }
            }
            else
            {
                vm.SelectFolder(0);
                await Task.Delay(150);
            }

            _selectionManager.HandleSingleClick(targetLink);
            RefreshDetailPanel();
            UpdateMainListSelectionVisuals();

            if (_mainListCardBorders.TryGetValue(targetLink.Id, out var card))
            {
                card.BringIntoView();
            }
        }));
    }

    private void LinksPage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideGridSplitter(e.OriginalSource as DependencyObject)) return;
        if (DataContext is MainViewModel viewModel && viewModel.LinkViewModel != null)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return;

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
                _selectionManager.ClearCurrentSelectedLink();
                viewModel.NotifyLinkSelected(false);
                ClearFolderSelection();
                UpdateSidebarSelectionVisuals();
                RefreshDetailPanel();
                return;
            }

            viewModel.ClearFolderSelectionVM();
        }
    }

    private void RootGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MainView.Visibility != Visibility.Visible) return;
        if (IsInsideGridSplitter(e.OriginalSource as DependencyObject)) return;

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
            _selectionManager.ClearCurrentSelectedLink();
            vm.NotifyLinkSelected(false);
            ClearFolderSelection();
            UpdateSidebarSelectionVisuals();
            RefreshDetailPanel();
        }
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

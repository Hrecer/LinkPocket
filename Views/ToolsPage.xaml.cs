using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Globalization;
using LinkPocket.Data;
using LinkPocket.Services;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.Views
{
    public partial class ToolsPage : UserControl
    {
        private class ToolItem
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Icon { get; set; } = string.Empty;
        }

        private readonly List<ToolItem> _tools = new()
        {
            new() { Id = "dedup", Name = "链接去重", Icon = "ContentDuplicate" },
            new() { Id = "idjump", Name = "ID跳转", Icon = "TextBoxSearchOutline" },
        };

        private bool _hasRunDedup;

        private StackPanel? _dedupHeaderRow;
        private Button? _dedupActionBtn;
        private TextBlock? _dedupActionText;
        private PackIcon? _dedupActionIcon;
        private Button? _dedupClearBtn;
        private TextBlock? _dedupDesc;
        private TextBlock? _dedupSummaryTb;

        private readonly HashSet<string> _selectedLinkIds = new();
        private List<Link>? _currentGroupLinks;
        private Dictionary<string, string>? _currentPathCache;
        private string? _currentGroupUrl;

        private TextBox? _idJumpInput;
        private TextBlock? _idJumpErrorHint;

        public ToolsPage()
        {
            InitializeComponent();
            this.IsVisibleChanged += (s, e) =>
            {
                if ((bool)e.NewValue)
                    ClearIdJumpInput();
            };
        }

        private void ToolsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ToolListbox.ItemsSource = _tools;
            ToolListbox.SelectedIndex = 0;
            ShowToolPanel("dedup");

            if (DataContext is ViewModels.MainViewModel vm)
                vm.OnToolsDataChanged += OnDataChanged;
        }

        private async void OnDataChanged(object? sender, EventArgs e)
        {
            if (_hasRunDedup && DetailView.Visibility != Visibility.Visible)
                await RunDedup();
        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            ClearIdJumpInput();
            if (DataContext is ViewModels.MainViewModel vm)
                vm.OnToolsDataChanged -= OnDataChanged;
        }

        private void ClearIdJumpInput()
        {
            if (_idJumpInput != null)
            {
                _idJumpInput.Text = "";
                _idJumpInput = null;
            }
            _idJumpErrorHint = null;
        }

        private void ToolListbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ToolListbox.SelectedItem is not ToolItem tool) return;
            _hasRunDedup = false;
            ExitDetailView();
            ShowToolPanel(tool.Id);
        }

        private void ShowToolPanel(string toolId)
        {
            ClearIdJumpInput();

            ToolContentPanel.Children.Clear();
            switch (toolId)
            {
                case "dedup":
                    BuildDedupLayout();
                    break;
                case "idjump":
                    BuildIdJumpLayout();
                    break;
            }
        }

        private void BuildDedupLayout()
        {
            _dedupHeaderRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var header = new TextBlock
            {
                Text = "链接去重",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _dedupHeaderRow.Children.Add(header);

            _dedupActionIcon = new PackIcon { Kind = PackIconKind.ContentDuplicate, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            _dedupActionText = new TextBlock { Text = "开始查重", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

            _dedupActionBtn = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dedupActionIcon, _dedupActionText } },
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(16, 0, 8, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238))
            };
            _dedupActionBtn.Click += async (s, e) => await RunDedup();
            _dedupHeaderRow.Children.Add(_dedupActionBtn);

            _dedupClearBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new PackIcon { Kind = PackIconKind.CloseCircleOutline, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "清除结果", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) }
                    }
                },
                Padding = new Thickness(10, 5, 10, 5),
                Cursor = Cursors.Hand,
                IsEnabled = false,
                Opacity = 0.35,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.Gray
            };
            _dedupClearBtn.Click += (s, e) => ClearDedupResults();
            _dedupHeaderRow.Children.Add(_dedupClearBtn);

            ToolContentPanel.Children.Add(_dedupHeaderRow);

            _dedupDesc = new TextBlock
            {
                Text = "检测完全相同的 URL，以组的形式展示重复的链接。",
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 20),
                TextWrapping = TextWrapping.Wrap
            };
            ToolContentPanel.Children.Add(_dedupDesc);
        }

        private void BuildIdJumpLayout()
        {
            var header = new TextBlock
            {
                Text = "ID跳转",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 0, 0, 20)
            };
            ToolContentPanel.Children.Add(header);

            var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            typePanel.Children.Add(new TextBlock { Text = "跳转类型：", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black });

            var linkRadio = new RadioButton { Content = "书签", GroupName = "JumpType", IsChecked = true, FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var folderRadio = new RadioButton { Content = "文件夹", GroupName = "JumpType", FontSize = 13, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            typePanel.Children.Add(linkRadio);
            typePanel.Children.Add(folderRadio);
            ToolContentPanel.Children.Add(typePanel);

            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16), VerticalAlignment = VerticalAlignment.Center };

            var idInput = new TextBox
            {
                Name = "IdJumpInput",
                Width = 280,
                FontSize = 14,
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                Foreground = Brushes.Black
            };
            _idJumpInput = idInput;
            inputRow.Children.Add(idInput);

            var jumpBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new PackIcon { Kind = PackIconKind.OpenInNew, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "跳转", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) }
                    }
                },
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(10, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)),
                VerticalAlignment = VerticalAlignment.Center
            };
            jumpBtn.Click += (s, e) => IdJump_Click(idInput, linkRadio, folderRadio);
            inputRow.Children.Add(jumpBtn);

            ToolContentPanel.Children.Add(inputRow);

            var errorHint = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)),
                Margin = new Thickness(0, 2, 0, 0),
                Opacity = 0,
                Height = 16
            };
            _idJumpErrorHint = errorHint;
            ToolContentPanel.Children.Add(errorHint);

            var tip = new TextBlock
            {
                Text = "输入书签ID或文件夹ID，快速定位到目标位置",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            ToolContentPanel.Children.Add(tip);
        }

        private async void IdJump_Click(TextBox idInput, RadioButton linkRadio, RadioButton folderRadio)
        {
            var id = idInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                await ShowIdError("请输入ID");
                return;
            }

            if (DataContext is not ViewModels.MainViewModel vm || vm.LinkNavigator == null) return;

            try
            {
                if (linkRadio.IsChecked == true)
                {
                    var links = await vm.GetAllLinksForToolsAsync();
                    var target = links.FirstOrDefault(l => l.LinkId == id);
                    if (target == null)
                    {
                        await ShowIdError("未找到匹配的书签ID");
                        return;
                    }
                    idInput.Text = "";
                    vm.LinkNavigator.NavigateToLinkById(id, target.ListId);
                }
                else
                {
                    if (!FolderExists(vm.FolderItems, id))
                    {
                        await ShowIdError("未找到匹配的文件夹ID");
                        return;
                    }
                    idInput.Text = "";
                    vm.LinkNavigator.NavigateToFolderById(id);
                }

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (mw.FindName("NavigationTabs") is System.Windows.Controls.ItemsControl navTabs)
                        navTabs.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private async Task ShowIdError(string message)
        {
            if (_idJumpErrorHint == null) return;
            _idJumpErrorHint.Text = message;
            _idJumpErrorHint.Opacity = 1;
            _idJumpErrorHint.BeginAnimation(UIElement.OpacityProperty, null);
            await Task.Delay(500);
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            _idJumpErrorHint.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static bool FolderExists(System.Collections.ObjectModel.ObservableCollection<ViewModels.FolderNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) return true;
                if (node.Children != null && node.Children.Count > 0 && FolderExists(node.Children, id))
                    return true;
            }
            return false;
        }

        private async Task RunDedup()
        {
            while (ToolContentPanel.Children.Count > 2)
                ToolContentPanel.Children.RemoveAt(ToolContentPanel.Children.Count - 1);

            if (_dedupSummaryTb != null)
                _dedupSummaryTb.Text = "";

            var loadingBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 16) };
            loadingBar.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 3, Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)) });
            loadingBar.Children.Add(new TextBlock { Text = "正在扫描重复链接...", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            ToolContentPanel.Children.Add(loadingBar);

            if (DataContext is not ViewModels.MainViewModel vm) return;
            List<Link> links;
            try
            {
                links = await vm.GetAllLinksForToolsAsync();
            }
            catch
            {
                ShowDedupError("读取数据失败");
                return;
            }

            while (ToolContentPanel.Children.Count > 2)
                ToolContentPanel.Children.RemoveAt(ToolContentPanel.Children.Count - 1);

            _dedupActionIcon!.Kind = PackIconKind.Refresh;
            _dedupActionText!.Text = "重新查重";
            _dedupClearBtn!.IsEnabled = true;
            _dedupClearBtn.Opacity = 1.0;
            _dedupClearBtn.Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238));

            var groups = links
                .GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .ToList();

            var summaryRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };

            var summaryText = groups.Count == 0
                ? "没有发现重复的链接"
                : $"发现 {groups.Count} 组重复链接，共 {groups.Sum(g => g.Count())} 条";

            _dedupSummaryTb = new TextBlock
            {
                Text = summaryText,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                VerticalAlignment = VerticalAlignment.Center
            };
            summaryRow.Children.Add(_dedupSummaryTb);
            ToolContentPanel.Children.Add(summaryRow);
            _hasRunDedup = true;

            if (groups.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "✓ 所有链接 URL 均不重复",
                    FontSize = 14,
                    Foreground = Brushes.Green,
                    Margin = new Thickness(0, 30, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                ToolContentPanel.Children.Add(empty);
                return;
            }

            var pathCache = new Dictionary<string, string>();
            foreach (var link in groups.SelectMany(g => g))
            {
                if (!pathCache.ContainsKey(link.LinkId))
                    pathCache[link.LinkId] = await vm.ResolveLinkPathAsync(link.ListId);
            }

            foreach (var group in groups)
            {
                var groupCard = CreateDedupGroupCard(group.Key, group.ToList(), pathCache);
                ToolContentPanel.Children.Add(groupCard);
            }
        }

        private UIElement CreateDedupGroupCard(string url, List<Link> links, Dictionary<string, string> pathCache)
        {
            var outerBorder = new Border
            {
                Tag = Tuple.Create(url, links, pathCache),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 1,
                    Opacity = 0.08,
                    Color = Colors.Black
                }
            };

            var sp = new StackPanel();

            var titleBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var icon = new PackIcon
            {
                Kind = PackIconKind.ContentDuplicate,
                Width = 18,
                Height = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Children.Add(icon);

            var countBadge = new TextBlock
            {
                Text = $" ×{links.Count}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Children.Add(countBadge);
            sp.Children.Add(titleBar);

            var urlText = new TextBlock
            {
                Text = url.Length > 120 ? url[..117] + "..." : url,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(24, 0, 0, 8)
            };
            sp.Children.Add(urlText);

            foreach (var link in links)
            {
                var itemSp = new StackPanel { Margin = new Thickness(24, 3, 0, 5) };

                var leftPart = new Grid
                {
                    VerticalAlignment = VerticalAlignment.Center
                };
                leftPart.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                leftPart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var titleArea = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(titleArea, 0);
                leftPart.Children.Add(titleArea);

                var iconGrid = new Grid
                {
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                var faviconBmp = FaviconService.LoadFromCache(link.FaviconUrl ?? link.Url);
                var faviconImg = new Image
                {
                    Stretch = Stretch.Uniform,
                    Source = faviconBmp,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                if (faviconBmp == null)
                    faviconImg.Visibility = Visibility.Collapsed;

                var webIcon = new PackIcon
                {
                    Kind = PackIconKind.Web,
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.55
                };
                if (faviconBmp != null)
                    webIcon.Visibility = Visibility.Collapsed;

                iconGrid.Children.Add(faviconImg);
                iconGrid.Children.Add(webIcon);
                titleArea.Children.Add(iconGrid);

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
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    faviconImg.Source = cached;
                                    faviconImg.Visibility = Visibility.Visible;
                                    webIcon.Visibility = Visibility.Collapsed;
                                });
                            }
                        }
                        catch { }
                    });
                }

                var titleText = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(link.Title) ? "(无标题)" : link.Title,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                titleArea.Children.Add(titleText);

                var folderPath = pathCache.TryGetValue(link.LinkId, out var resolved) ? resolved : "全部书签";
                var pathText = new TextBox
                {
                    Text = $" · {folderPath}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    IsReadOnly = true,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Focusable = true,
                    ContextMenu = null,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                Grid.SetColumn(pathText, 1);
                leftPart.Children.Add(pathText);

                itemSp.Children.Add(leftPart);
                sp.Children.Add(itemSp);
            }

            outerBorder.Child = sp;

            outerBorder.MouseLeftButtonUp += DedupGroup_Click;
            return outerBorder;
        }

        private void DedupGroup_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not Tuple<string, List<Link>, Dictionary<string, string>> data) return;
            EnterDetailView(data.Item1, data.Item2, data.Item3);
        }

        private void EnterDetailView(string groupUrl, List<Link> links, Dictionary<string, string> pathCache)
        {
            MainScrollView.Visibility = Visibility.Collapsed;
            LeftSidebarBorder.Visibility = Visibility.Collapsed;
            MainSplitter.Visibility = Visibility.Collapsed;

            if (DataContext is ViewModels.MainViewModel vm)
                vm.IsInSecondaryPage = true;

            Grid.SetColumn(DetailView, 0);
            Grid.SetColumnSpan(DetailView, 3);

            _currentGroupUrl = groupUrl;
            _currentGroupLinks = links;
            _currentPathCache = pathCache;
            _selectedLinkIds.Clear();

            DedupGroupUrlBox.Text = groupUrl;
            DedupGroupCountLabel.Text = $"共 {links.Count} 条重复链接";

            DedupDetailCardsPanel.Children.Clear();
            foreach (var link in links)
            {
                var card = CreateDetailCard(link, pathCache);
                DedupDetailCardsPanel.Children.Add(card);
            }

            UpdateDeleteBtnState();
            DetailView.Visibility = Visibility.Visible;
            Keyboard.Focus(DetailView);
        }

        private void ExitDetailView()
        {
            _selectedLinkIds.Clear();
            _currentGroupLinks = null;
            _currentPathCache = null;
            _currentGroupUrl = null;

            DetailView.Visibility = Visibility.Collapsed;
            Grid.SetColumn(DetailView, 2);
            Grid.SetColumnSpan(DetailView, 1);
            MainScrollView.Visibility = Visibility.Visible;
            LeftSidebarBorder.Visibility = Visibility.Visible;
            MainSplitter.Visibility = Visibility.Visible;

            if (DataContext is ViewModels.MainViewModel vm)
                vm.IsInSecondaryPage = false;
        }

        private void DedupBack_Click(object sender, RoutedEventArgs e)
        {
            ExitDetailView();
        }

        private void DedupCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(DedupGroupUrlBox.Text);
        }

        private void DetailCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button or PackIcon or TextBlock) return;
            if (sender is not Border card || card.Tag is not string linkId) return;
            if (_currentGroupLinks == null) return;

            int maxSelect = _currentGroupLinks.Count - 1;

            if (_selectedLinkIds.Contains(linkId))
            {
                _selectedLinkIds.Remove(linkId);
            }
            else
            {
                if (_selectedLinkIds.Count >= maxSelect) return;
                _selectedLinkIds.Add(linkId);
            }

            UpdateCardSelectionVisual(card, _selectedLinkIds.Contains(linkId));
            UpdateDeleteBtnState();
            e.Handled = true;
        }

        private void UpdateCardSelectionVisual(Border card, bool selected)
        {
            if (selected)
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x62, 0x00, 0xEE));
                card.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xE5, 0xF5));
            }
            else
            {
                card.BorderBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider");
                card.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            }
        }

        private void UpdateDeleteBtnState()
        {
            if (DedupDeleteBtn == null) return;
            DedupDeleteBtn.IsEnabled = _selectedLinkIds.Count > 0 && _currentGroupLinks != null;
        }

        private void ClearAllSelections()
        {
            if (_selectedLinkIds.Count == 0) return;
            _selectedLinkIds.Clear();
            foreach (var child in DedupDetailCardsPanel.Children)
            {
                if (child is Border card)
                    UpdateCardSelectionVisual(card, false);
            }
            UpdateDeleteBtnState();
        }

        private void DetailView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearAllSelections();
                e.Handled = true;
            }
        }

        private void DetailScrollView_BlankClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is ScrollViewer)
                ClearAllSelections();
        }

        private async void DedupDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLinkIds.Count == 0 || string.IsNullOrEmpty(_currentGroupUrl)) return;
            if (DataContext is not ViewModels.MainViewModel vm) return;

            var toDelete = _selectedLinkIds.ToList();
            _selectedLinkIds.Clear();

            try
            {
                foreach (var linkId in toDelete)
                {
                    await vm.LinkViewModel!.DeleteLinkAsync(linkId);
                }

                var newGroups = await FindDedupGroupForUrl(_currentGroupUrl);
                if (newGroups != null && newGroups.Count > 1)
                {
                    var pathCache = await BuildPathCache(newGroups);
                    _currentGroupLinks = newGroups;
                    _currentPathCache = pathCache;
                    DedupGroupCountLabel.Text = $"共 {newGroups.Count} 条重复链接";

                    DedupDetailCardsPanel.Children.Clear();
                    foreach (var link in newGroups)
                    {
                        var card = CreateDetailCard(link, pathCache);
                        DedupDetailCardsPanel.Children.Add(card);
                    }

                    UpdateDeleteBtnState();
                }
                else
                {
                    ExitDetailView();
                    await RunDedup();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<List<Link>?> FindDedupGroupForUrl(string url)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return null;
            var allLinks = await vm.GetAllLinksForToolsAsync();
            var group = allLinks.Where(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();
            return group.Count > 1 ? group : null;
        }

        private async Task<Dictionary<string, string>> BuildPathCache(List<Link> links)
        {
            var cache = new Dictionary<string, string>();
            if (DataContext is not ViewModels.MainViewModel vm) return cache;
            foreach (var link in links)
            {
                if (!cache.ContainsKey(link.LinkId))
                    cache[link.LinkId] = await vm.ResolveLinkPathAsync(link.ListId);
            }
            return cache;
        }

        private UIElement CreateDetailCard(Link link, Dictionary<string, string> pathCache)
        {
            var card = new Border
            {
                Tag = link.LinkId,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 10, 10),
                Width = 330,
                Height = 420,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 5,
                    ShadowDepth = 1,
                    Opacity = 0.08,
                    Color = Colors.Black
                },
                Cursor = Cursors.Hand
            };
            card.PreviewMouseLeftButtonUp += DetailCard_Click;

            var sp = new StackPanel();

            var folderPath = pathCache.TryGetValue(link.LinkId, out var resolved) ? resolved : "全部书签";

            var jumpBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new PackIcon { Kind = PackIconKind.OpenInNew, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "跳转", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) }
                    }
                },
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            var capturedLinkId = link.LinkId;
            var capturedListId = link.ListId;
            jumpBtn.Click += (s, e) =>
            {
                ExitDetailView();
                if (DataContext is ViewModels.MainViewModel vm && vm.LinkNavigator != null)
                    vm.LinkNavigator.NavigateToLinkById(capturedLinkId, capturedListId);
            };

            var headerGrid = new Grid { Height = 48 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathClip = new Border { ClipToBounds = true };
            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var folderIcon = new PackIcon { Kind = PackIconKind.FolderOutline, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) };
            Grid.SetColumn(folderIcon, 0);
            pathGrid.Children.Add(folderIcon);

            var pathTb = new TextBox
            {
                Text = folderPath,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = true,
                ContextMenu = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsHitTestVisible = false,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(pathTb, 1);
            pathGrid.Children.Add(pathTb);
            pathClip.Child = pathGrid;

            Grid.SetColumn(pathClip, 0);
            headerGrid.Children.Add(pathClip);

            Grid.SetColumn(jumpBtn, 1);
            headerGrid.Children.Add(jumpBtn);

            sp.Children.Add(headerGrid);

            var iconGrid = new Grid { Width = 32, Height = 32, Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
            var favBmp = FaviconService.LoadFromCache(link.FaviconUrl ?? link.Url);
            var favImg = new Image { Source = favBmp, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (favBmp == null) favImg.Visibility = Visibility.Collapsed;
            var earthIcon = new PackIcon { Kind = PackIconKind.Earth, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
            if (favBmp != null) earthIcon.Visibility = Visibility.Collapsed;
            iconGrid.Children.Add(favImg);
            iconGrid.Children.Add(earthIcon);
            sp.Children.Add(iconGrid);

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl) && favBmp == null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FaviconService.PrefetchAndCacheAsync(link.FaviconUrl);
                        var cached = FaviconService.LoadFromCache(link.FaviconUrl);
                        if (cached != null)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                favImg.Source = cached;
                                favImg.Visibility = Visibility.Visible;
                                earthIcon.Visibility = Visibility.Collapsed;
                            });
                        }
                    }
                    catch { }
                });
            }

            sp.Children.Add(new TextBox
            {
                Text = string.IsNullOrWhiteSpace(link.Title) ? "(无标题)" : link.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.NoWrap,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = true,
                ContextMenu = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsHitTestVisible = false,
                Margin = new Thickness(0, 2, 0, 0)
            });

            sp.Children.Add(new TextBox
            {
                Text = link.Url,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = true,
                ContextMenu = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsHitTestVisible = false,
                Margin = new Thickness(0, 3, 0, 0)
            });

            var (descDisplay, descTruncated, descLineHeight) = TruncateToLines(link.Description ?? "", 298, 4, 11);
            var descBoxHeight = Math.Ceiling(descLineHeight * 4) + 4;
            var descBox = new Border { Height = descBoxHeight, Margin = new Thickness(0, 8, 0, 0) };
            descBox.Child = new TextBox
            {
                Text = descDisplay,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = true,
                ContextMenu = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsHitTestVisible = false
            };
            sp.Children.Add(descBox);

            var hintArea = new Border { Height = 14, Margin = new Thickness(0, 1, 0, 0) };
            hintArea.Child = new TextBlock
            {
                Text = descTruncated ? "(未全部显示)" : "",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = descTruncated ? new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)) : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            sp.Children.Add(hintArea);

            sp.Children.Add(new Separator { Opacity = 0.18, Margin = new Thickness(0, 6, 0, 4) });

            var labelStyle = new Action<string, string, int>((labelText, valueText, marginBottom) =>
            {
                sp.Children.Add(new TextBlock { Text = labelText, FontSize = 10, Opacity = 0.5, Margin = new Thickness(0, 2, 0, 1) });
                sp.Children.Add(new TextBox
                {
                    Text = valueText,
                    FontSize = 10,
                    IsReadOnly = true,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Focusable = true,
                    ContextMenu = null,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    IsHitTestVisible = false,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(0, 0, 0, marginBottom)
                });
            });

            labelStyle("最后更新", link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), 3);
            labelStyle("最后查看", link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未", 3);
            labelStyle("累计查看次数", link.VisitCount == 0 ? "0 次" : $"{link.VisitCount} 次", 3);
            labelStyle("创建时间", link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), 3);

            sp.Children.Add(new TextBlock { Text = "ID", FontSize = 10, Opacity = 0.5, Margin = new Thickness(0, 2, 0, 1) });

            var idRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            idRow.Children.Add(new TextBox
            {
                Text = link.LinkId,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Opacity = 0.7,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = true,
                ContextMenu = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            });

            var copyIdBtn = new Button
            {
                Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 11, Height = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) },
                Padding = new Thickness(3, 1, 3, 1),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "复制ID",
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = true
            };
            var capturedId = link.LinkId;
            copyIdBtn.Click += (s, e) => Clipboard.SetText(capturedId);
            Grid.SetColumn(copyIdBtn, 1);
            idRow.Children.Add(copyIdBtn);

            sp.Children.Add(idRow);

            card.Child = sp;
            return card;
        }

        private static (string text, bool truncated, double lineHeight) TruncateToLines(string text, double availableWidth, int maxLines, double fontSize)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                var typeface0 = new Typeface(new FontFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var dpi0 = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
                var single0 = new FormattedText("A", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface0, fontSize, Brushes.Black, dpi0.DpiScaleY);
                single0.MaxTextWidth = availableWidth;
                return ("", false, single0.Height);
            }

            var typeface = new Typeface(new FontFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
            var pixelsPerDip = dpi.DpiScaleY;

            var singleLine = new FormattedText("A", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pixelsPerDip);
            singleLine.MaxTextWidth = availableWidth;
            double lh = singleLine.Height;
            double maxAllowedHeight = lh * maxLines;

            var fullFormatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pixelsPerDip);
            fullFormatted.MaxTextWidth = availableWidth;

            if (fullFormatted.Height <= maxAllowedHeight)
                return (text, false, lh);

            int low = 0, high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                var partial = new FormattedText(text[..mid], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pixelsPerDip);
                partial.MaxTextWidth = availableWidth;
                if (partial.Height <= maxAllowedHeight)
                    low = mid;
                else
                    high = mid - 1;
            }

            return (text[..low], true, lh);
        }

        private void ClearDedupResults()
        {
            _hasRunDedup = false;
            ExitDetailView();
            if (_dedupActionIcon != null) _dedupActionIcon.Kind = PackIconKind.ContentDuplicate;
            if (_dedupActionText != null) _dedupActionText.Text = "开始查重";
            if (_dedupClearBtn != null) { _dedupClearBtn.IsEnabled = false; _dedupClearBtn.Opacity = 0.35; _dedupClearBtn.Foreground = Brushes.Gray; }
            while (ToolContentPanel.Children.Count > 2)
                ToolContentPanel.Children.RemoveAt(ToolContentPanel.Children.Count - 1);
        }

        private void ShowDedupError(string message)
        {
            while (ToolContentPanel.Children.Count > 2)
                ToolContentPanel.Children.RemoveAt(ToolContentPanel.Children.Count - 1);

            var errorTb = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Brushes.Red,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolContentPanel.Children.Add(errorTb);

            var retryBtn = new Button
            {
                Content = "重试",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand
            };
            retryBtn.Click += async (s, e) => await RunDedup();
            ToolContentPanel.Children.Add(retryBtn);
        }
    }
}
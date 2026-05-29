using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
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
        };

        private bool _hasRunDedup;

        private StackPanel? _dedupHeaderRow;
        private Button? _dedupActionBtn;
        private TextBlock? _dedupActionText;
        private PackIcon? _dedupActionIcon;
        private Button? _dedupClearBtn;
        private TextBlock? _dedupDesc;
        private TextBlock? _dedupSummaryTb;

        public ToolsPage()
        {
            InitializeComponent();
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
            if (_hasRunDedup)
                await RunDedup();
        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm)
                vm.OnToolsDataChanged -= OnDataChanged;
        }

        private void ToolListbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ToolListbox.SelectedItem is not ToolItem tool) return;
            _hasRunDedup = false;
            ShowToolPanel(tool.Id);
        }

        private void ShowToolPanel(string toolId)
        {
            ToolContentPanel.Children.Clear();
            switch (toolId)
            {
                case "dedup":
                    BuildDedupLayout();
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
                Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238))
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

        private void ClearDedupResults()
        {
            _hasRunDedup = false;
            if (_dedupActionIcon != null) _dedupActionIcon.Kind = PackIconKind.ContentDuplicate;
            if (_dedupActionText != null) _dedupActionText.Text = "开始查重";
            if (_dedupClearBtn != null) { _dedupClearBtn.IsEnabled = false; _dedupClearBtn.Opacity = 0.35; }
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

        private UIElement CreateDedupGroupCard(string url, List<Link> links, Dictionary<string, string> pathCache)
        {
            var outerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14),
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
                var itemSp = new StackPanel { Margin = new Thickness(24, 4, 0, 6) };

                var nameRow = new Grid();
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var leftPart = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

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
                leftPart.Children.Add(iconGrid);

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
                leftPart.Children.Add(titleText);

                var folderPath = pathCache.TryGetValue(link.LinkId, out var resolved) ? resolved : "全部书签";
                var pathText = new TextBlock
                {
                    Text = $" · {folderPath}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                leftPart.Children.Add(pathText);

                Grid.SetColumn(leftPart, 0);
                nameRow.Children.Add(leftPart);

                var jumpBtn = new Button
                {
                    Content = new PackIcon { Kind = PackIconKind.OpenInNew, Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)) },
                    Padding = new Thickness(5, 3, 5, 3),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    ToolTip = "跳转到此书签",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                var capturedLinkId = link.LinkId;
                var capturedListId = link.ListId;
                jumpBtn.Click += (s, e) =>
                {
                    if (DataContext is ViewModels.MainViewModel vm2 && vm2.LinkNavigator != null)
                        vm2.LinkNavigator.NavigateToLinkById(capturedLinkId, capturedListId);
                };
                Grid.SetColumn(jumpBtn, 1);
                nameRow.Children.Add(jumpBtn);

                itemSp.Children.Add(nameRow);

                var idRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(22, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var idLabel = new TextBlock
                {
                    Text = "ID:",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center
                };
                idRow.Children.Add(idLabel);

                var idValue = new TextBlock
                {
                    Text = link.LinkId,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                idRow.Children.Add(idValue);

                var copyBtn = new Button
                {
                    Content = new PackIcon { Kind = PackIconKind.ContentCopy, Width = 12, Height = 12, Foreground = Brushes.Gray },
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    ToolTip = "复制 ID",
                    VerticalAlignment = VerticalAlignment.Center
                };
                var capturedId = link.LinkId;
                copyBtn.Click += (s, e) => Clipboard.SetText(capturedId);
                idRow.Children.Add(copyBtn);

                itemSp.Children.Add(idRow);
                sp.Children.Add(itemSp);
            }

            outerBorder.Child = sp;
            return outerBorder;
        }
    }
}
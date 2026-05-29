using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.Views
{
    public partial class SmartListsPage : UserControl
    {
        private readonly Dictionary<string, Border> _resultCardBorders = new();
        private LinkItem? _selectedResultItem;

        public SmartListsPage()
        {
            InitializeComponent();
            Focusable = true;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SmartListViewModel != null)
                vm.SmartListViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel == null) return;
            var slVm = vm.SmartListViewModel;

            if (e.PropertyName == nameof(slVm.ShowResult))
            {
                if (slVm.ShowResult)
                {
                    vm.IsInSecondaryPage = true;
                    CardPanel.Visibility = Visibility.Collapsed;
                    ResultPanel.Visibility = Visibility.Visible;
                    RenderResultCards(slVm.ResultViewModel);
                }
                else
                {
                    vm.IsInSecondaryPage = false;
                    ResultPanel.Visibility = Visibility.Collapsed;
                    CardPanel.Visibility = Visibility.Visible;
                    ClearResultSelection();
                }
            }
        }

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not string listId) return;
            if (DataContext is MainViewModel vm && vm.SmartListViewModel != null)
                vm.SmartListViewModel.OpenSmartList(listId);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SmartListViewModel != null)
                vm.SmartListViewModel.GoBack();
        }

        private void RenderResultCards(SmartListResultViewModel? resultVm)
        {
            if (resultVm == null) return;

            ResultCardsPanel.Children.Clear();
            _resultCardBorders.Clear();

            if (!resultVm.HasData)
            {
                EmptyPlaceholder.Visibility = Visibility.Visible;
                LoadingIndicator.Visibility = Visibility.Collapsed;
                JumpToLinkBtn.IsEnabled = false;
                return;
            }

            EmptyPlaceholder.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Collapsed;

            foreach (var item in resultVm.Items)
                ResultCardsPanel.Children.Add(CreateResultCard(item));
        }

        private Border CreateResultCard(LinkItem item)
        {
            var card = new Border
            {
                Tag = "SmartResultCard",
                Width = 720,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.FindResource("MaterialDesignCardBackground"),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 2, 4, 2),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, Application.Current.FindResource("MaterialDesignDivider")));
            style.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.08 }));
            style.Triggers.Add(new Trigger { Property = Border.IsMouseOverProperty, Value = true,
                Setters = { new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.15 }) }
            });
            card.Style = style;

            card.MouseLeftButtonUp += ResultCard_Click;

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBorder = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                Margin = new Thickness(0, 0, 12, 0),
                ClipToBounds = true
            };

            var iconGrid = new Grid();

            var faviconBmp = FaviconService.LoadFromCache(item.FaviconUrl);
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
                Width = 20, Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.55
            };
            if (faviconBmp != null)
                webIcon.Visibility = Visibility.Collapsed;

            iconGrid.Children.Add(faviconImg);
            iconGrid.Children.Add(webIcon);

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

            iconBorder.Child = iconGrid;
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url,
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = item.Url, FontSize = 11, Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            card.Child = grid;

            _resultCardBorders[item.LinkId] = card;
            return card;
        }

        private void ResultCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel?.ResultViewModel == null) return;

            ClearResultVisualSelection();

            if (sender is not Border card || !"SmartResultCard".Equals(card.Tag as string)) return;

            var item = FindItemByCard(card);
            if (item == null) return;

            _selectedResultItem = item;
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(98, 0, 238));
            card.BorderThickness = new Thickness(2);
            JumpToLinkBtn.IsEnabled = true;
        }

        private LinkItem? FindItemByCard(Border card)
        {
            foreach (var kvp in _resultCardBorders)
            {
                if (kvp.Value == card && DataContext is MainViewModel vm && vm.SmartListViewModel?.ResultViewModel != null)
                    return vm.SmartListViewModel.ResultViewModel.Items.FirstOrDefault(i => i.LinkId == kvp.Key);
            }
            return null;
        }

        private void ClearResultVisualSelection()
        {
            var defaultBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider");
            foreach (var kvp in _resultCardBorders)
            {
                kvp.Value.BorderBrush = defaultBrush;
                kvp.Value.BorderThickness = new Thickness(2);
            }
        }

        private void ClearResultSelection()
        {
            ClearResultVisualSelection();
            _selectedResultItem = null;
            JumpToLinkBtn.IsEnabled = false;
        }

        private void JumpToLink_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedResultItem == null || DataContext is not MainViewModel vm || vm.LinkNavigator == null) return;
            vm.LinkNavigator.NavigateToLinkInMainList(_selectedResultItem.LinkId);
        }

        private void ResultArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_selectedResultItem == null) return;
            if (e.Source is not Border b || !"SmartResultCard".Equals(b.Tag as string))
                ClearResultSelection();
        }
    }
}

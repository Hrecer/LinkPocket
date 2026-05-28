using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Models;
using LinkPocket.ViewModels;

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
                MinHeight = 72,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.FindResource("MaterialDesignCardBackground"),
                BorderBrush = (Brush)Application.Current.FindResource("MaterialDesignDivider"),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 2, 4, 2),
                Padding = new Thickness(16, 12, 16, 12)
            };

            card.MouseLeftButtonUp += ResultCard_Click;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var faviconBorder = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(faviconBorder, 0);

            var faviconText = new TextBlock
            {
                Text = item.TitleLetter,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(98, 0, 238)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            faviconBorder.Child = faviconText;

            var infoStack = new StackPanel
            {
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(infoStack, 1);

            var titleText = new TextBlock
            {
                Text = item.DisplayTitle.Length > 50 ? item.DisplayTitle[..47] + "..." : item.DisplayTitle,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var metaText = new TextBlock
            {
                Text = $"{item.VisitCountText} · {item.LastVisitedText}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                Margin = new Thickness(0, 3, 0, 0)
            };

            infoStack.Children.Add(titleText);
            infoStack.Children.Add(metaText);

            grid.Children.Add(faviconBorder);
            grid.Children.Add(infoStack);
            card.Child = grid;

            _resultCardBorders[item.LinkId] = card;
            return card;
        }

        private void ResultCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel?.ResultViewModel == null) return;

            ClearResultVisualSelection();

            if (sender is not Border card || card.Tag != "SmartResultCard") return;

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
            if (e.Source is not Border b || b.Tag != "SmartResultCard")
                ClearResultSelection();
        }
    }
}

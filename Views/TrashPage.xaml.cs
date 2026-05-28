using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    public partial class TrashPage : UserControl
    {
        private readonly Dictionary<string, Border> _trashCardBorders = new();

        public TrashPage()
        {
            InitializeComponent();
            Focusable = true;
            Loaded += (_, _) => Keyboard.Focus(this);
        }

        public Task RefreshAsync()
        {
            if (DataContext is not MainViewModel vm || vm.RecycleBinViewModel == null) return Task.CompletedTask;

            TrashContentPanel.Children.Clear();
            _trashCardBorders.Clear();

            var recycleVm = vm.RecycleBinViewModel;
            if (!recycleVm.HasItems)
            {
                TrashContentPanel.Children.Add(new TextBlock
                {
                    Text = "回收站为空", FontSize = 14, Opacity = 0.4,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0)
                });
                TrashRestoreBtn.IsEnabled = false;
                TrashDeleteBtn.IsEnabled = false;
                return Task.CompletedTask;
            }

            foreach (var item in recycleVm.Items)
                TrashContentPanel.Children.Add(CreateTrashLinkCard(item, recycleVm));

            SyncButtonStates(recycleVm);
            return Task.CompletedTask;
        }

        private Border CreateTrashLinkCard(LinkItem item, RecycleBinViewModel recycleVm)
        {
            var card = new Border
            {
                Tag = "TrashCard",
                Margin = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(10),
                Cursor = Cursors.Hand,
                Width = 720,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = (Brush)FindResource("MaterialDesignCardBackground"),
                BorderBrush = (Brush)FindResource("MaterialDesignDivider"),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.08 }));
            style.Triggers.Add(new Trigger { Property = Border.IsMouseOverProperty, Value = true,
                Setters = { new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.15 }) }
            });
            card.Style = style;

            _trashCardBorders[item.LinkId] = card;

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBorder = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0)
            };
            var iconGrid = new Grid();
            if (!string.IsNullOrEmpty(item.FaviconUrl))
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = FaviconService.LoadFromCache(item.FaviconUrl), Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                };
                iconGrid.Children.Add(img);
            }
            else
            {
                iconGrid.Children.Add(new PackIcon { Kind = PackIconKind.Web, Width = 20, Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 });
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
            textStack.Children.Add(new TextBlock
            {
                Text = $"删除于回收站",
                FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            card.Child = grid;

            var capturedId = item.LinkId;
            card.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    recycleVm.ToggleMultiSelect(capturedId);
                else
                    recycleVm.SelectSingle(capturedId);
                UpdateTrashSelectionVisuals(recycleVm);
                e.Handled = true;
            };

            return card;
        }

        public void UpdateTrashSelectionVisuals(RecycleBinViewModel recycleVm)
        {
            var selectedBrush = new SolidColorBrush(Color.FromRgb(98, 0, 238));
            var defaultBrush = (Brush)FindResource("MaterialDesignDivider");
            foreach (var kvp in _trashCardBorders)
                kvp.Value.BorderBrush = recycleVm.IsSelected(kvp.Key) ? selectedBrush : defaultBrush;
            SyncButtonStates(recycleVm);
        }

        private void SyncButtonStates(RecycleBinViewModel recycleVm)
        {
            bool hasSelection = recycleVm.SelectedIds.Count > 0;
            TrashRestoreBtn.IsEnabled = hasSelection;
            TrashDeleteBtn.IsEnabled = hasSelection;
        }

        private async void TrashRestore_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.RecycleBinViewModel == null) return;
            var recycleVm = vm.RecycleBinViewModel;
            if (recycleVm.SelectedIds.Count == 0) return;

            await recycleVm.RestoreSelectedAsync();
            await recycleVm.LoadAsync();
            await RefreshAsync();
        }

        private async void TrashPermanentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.RecycleBinViewModel == null) return;
            var recycleVm = vm.RecycleBinViewModel;
            if (recycleVm.SelectedIds.Count == 0) return;

            await recycleVm.PermanentDeleteSelectedAsync();
            await recycleVm.LoadAsync();
            await RefreshAsync();
        }

        private void TrashPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.RecycleBinViewModel == null) return;
            var recycleVm = vm.RecycleBinViewModel;

            if (e.Key == Key.Delete && recycleVm.SelectedIds.Count > 0)
            {
                TrashPermanentDelete_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                recycleVm.ClearSelection();
                UpdateTrashSelectionVisuals(recycleVm);
                e.Handled = true;
            }
        }

        private void TrashPage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.RecycleBinViewModel == null) return;
            var recycleVm = vm.RecycleBinViewModel;
            if (recycleVm.SelectedIds.Count == 0) return;

            DependencyObject? hit = e.OriginalSource as DependencyObject;
            while (hit != null)
            {
                if (hit is Border b && "TrashCard".Equals(b.Tag as string))
                    return;
                hit = VisualTreeHelper.GetParent(hit);
            }

            recycleVm.ClearSelection();
            UpdateTrashSelectionVisuals(recycleVm);
        }
    }
}

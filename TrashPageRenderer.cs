using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Data;
using LinkPocket.ViewModels;
using MaterialDesignThemes.Wpf;

namespace LinkPocket;

public class TrashPageRenderer
{
    private readonly HashSet<int> _expandedFolders = new() { -1 };
    private readonly StackPanel _contentPanel;

    public TrashPageRenderer(StackPanel contentPanel)
    {
        _contentPanel = contentPanel;
    }

    public async Task RefreshAsync(MainViewModel viewModel)
    {
        _contentPanel.Children.Clear();
        if (viewModel.TrashItems == null) return;

        foreach (var folder in viewModel.TrashItems)
        {
            await RenderFolderNodeAsync(folder, _contentPanel, 0, viewModel);
        }
    }

    private async Task RenderFolderNodeAsync(FolderNode folder, Panel container, int depth, MainViewModel viewModel)
    {
        bool isExpanded = _expandedFolders.Contains(folder.Id);
        var row = CreateFolderRow(folder, isExpanded, depth, viewModel);
        container.Children.Add(row);

        if (isExpanded)
        {
            var childPanel = new StackPanel { Margin = new Thickness(depth == 0 ? 0 : 20, 0, 0, 0) };

            foreach (var child in folder.Children)
            {
                await RenderFolderNodeAsync(child, childPanel, depth + 1, viewModel);
            }

            List<Link> links;
            if (folder.Id == -1)
                links = await viewModel.GetTrashRootLinksAsync();
            else
                links = await viewModel.GetTrashLinksForFolderAsync(folder.Id);

            if (links != null)
            {
                foreach (var link in links)
                {
                    childPanel.Children.Add(CreateLinkCard(link));
                }
            }

            container.Children.Add(childPanel);
        }
    }

    private Border CreateFolderRow(FolderNode folder, bool isExpanded, int depth, MainViewModel viewModel)
    {
        var row = new Border
        {
            Tag = "TrashFolderCard",
            Padding = new Thickness(8 + depth * 16, 6, 8, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Arrow
        };

        var hStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var chevronBorder = new Border
        {
            Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            Tag = folder.Id
        };
        chevronBorder.Child = new PackIcon
        {
            Kind = isExpanded ? PackIconKind.ChevronDown : PackIconKind.ChevronRight,
            Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };

        chevronBorder.PreviewMouseLeftButtonDown += async (s, e) =>
        {
            if (s is FrameworkElement fe && fe.Tag is int fid)
            {
                if (_expandedFolders.Contains(fid))
                    _expandedFolders.Remove(fid);
                else
                    _expandedFolders.Add(fid);
                await RefreshAsync(viewModel);
            }
            e.Handled = true;
        };

        hStack.Children.Add(chevronBorder);

        hStack.Children.Add(new PackIcon
        {
            Kind = folder.Id == -1 ? PackIconKind.Delete : PackIconKind.Folder,
            Width = 14, Height = 14, Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = folder.Id == -1
                ? new SolidColorBrush(Color.FromRgb(211, 47, 47))
                : new SolidColorBrush(Color.FromRgb(255, 183, 77))
        });

        hStack.Children.Add(new TextBlock
        {
            Text = folder.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        });

        row.Child = hStack;
        return row;
    }

    private Border CreateLinkCard(Link link)
    {
        var card = new Border
        {
            Tag = "TrashLinkCard",
            Margin = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            Width = 720, HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.FindResource("MaterialDesignCardBackground"),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(16, 12, 16, 12)
        };

        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, Application.Current.FindResource("MaterialDesignDivider")));
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

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = !string.IsNullOrEmpty(link.Title) ? link.Title : link.Url,
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
        });
        textStack.Children.Add(new TextBlock
        {
            Text = link.Url, FontSize = 11, Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        card.Child = grid;

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                try { Process.Start(new ProcessStartInfo(link.Url ?? "") { UseShellExecute = true }); } catch { }
                e.Handled = true;
            }
        };

        return card;
    }
}

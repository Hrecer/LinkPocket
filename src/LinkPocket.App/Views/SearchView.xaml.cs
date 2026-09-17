using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views;

/// <summary>
/// 搜索页视图（阶段 9 MVVM 启用）：只做视图层装配——
/// 结果表列定义/单元格工厂/空态渲染/关键词高亮在这里，查询执行、范围守卫、
/// 选中态与页面动作命令全部在 <see cref="SearchViewModel"/>（构造注入组合根）。
/// </summary>
public partial class SearchView : UserControl
{
    private SearchViewModel? _vm;

    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ReferenceEquals(_vm, DataContext)) return;
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as SearchViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.ResetRequested += (_, _) => SearchBox.Focus();
                SetupTable(_vm);
                // 迟挂的 DataContext：把 VM 当前的空态/结果同步到表上
                ApplyEmptyState();
                ApplyResults();
            }
        };
    }

    // —— 表格装配：列定义 = 数据 + 排序键 + 单元格工厂，表头/行/排序全部由控件驱动 ——

    private void SetupTable(SearchViewModel vm)
    {
        ResultsTable.SortField = "title";   // 默认名称升序（与主栏一致，表头初始即显示 ▲）
        ResultsTable.SortAscending = true;
        ResultsTable.Columns = new[]
        {
            new DataTableColumn
            {
                // 名称列占 2 份剩余空间：标题下方还有 URL，必须留出可见宽度
                // （用户 2026-09-16 反馈"URL 被大幅压缩"）——空间来自右侧四列压到极限
                Field = "title", Label = "名称", Width = -2,
                SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                    ? ((LinkItem)r).Url : ((LinkItem)r).Title),
                CellFactory = r => BuildSearchNameCell((LinkItem)r)
            },
            new DataTableColumn
            {
                // 位置列占 3 份剩余空间（名称 2 份）：层级路径最长、最需要宽度；
                // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                // 的实测宽 105 + 9 列间余量（探针实测值；改小会截断成省略号，或让相邻列贴在一起）
                // 省下的宽度全部让给名称/位置（用户 2026-09-16 要求 URL 不再被压缩）
                Field = "path", Label = "位置", Width = -3,
                SortKey = r => (IComparable)(vm.ResolveFolderPath(((LinkItem)r).ListId)),
                CellFactory = r => TextCell(vm.ResolveFolderPath(((LinkItem)r).ListId), 12.5)
            },
            new DataTableColumn
            {
                Field = "updated_at", Label = "最后更新", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
                CellFactory = r => TextCell(((LinkItem)r).UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12.5)
            },
            new DataTableColumn
            {
                Field = "last_visited_at", Label = "最后查看", Width = 114,
                SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
                CellFactory = r => TextCell(
                    ((LinkItem)r).LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未", 12.5)
            },
            new DataTableColumn
            {
                Field = "visit_count", Label = "查看次数", Width = 72,
                SortKey = r => (IComparable)((LinkItem)r).VisitCount,
                CellFactory = r => TextCell($"{((LinkItem)r).VisitCount} 次", 12.5)
            },
            new DataTableColumn
            {
                Field = "created_at", Label = "创建时间", Width = 114,
                SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
                CellFactory = r => TextCell(((LinkItem)r).CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12.5)
            },
        };

        ResultsTable.RowClick += (_, item) => vm.SelectItem((LinkItem)item);
        ResultsTable.RowDoubleClick += (_, item) => vm.JumpCommand.Execute(null);
    }

    // —— VM 状态 → 视图渲染 ——

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.Results):
                ApplyResults();
                break;
            case nameof(SearchViewModel.EmptyState):
                ApplyEmptyState();
                break;
            case nameof(SearchViewModel.SelectedItem):
                ApplySelection();
                break;
        }
    }

    private void ApplyResults() => ResultsTable.ItemsSource = _vm?.Results;

    private void ApplyEmptyState()
    {
        if (_vm == null) return;
        if (_vm.EmptyState != null)
            ResultsTable.EmptyContent = BuildSearchState(_vm.EmptyState.IconKind, _vm.EmptyState.Title,
                _vm.EmptyState.Subtitle, _vm.EmptyState.ContainerBrush, _vm.EmptyState.OnContainerBrush);
        if (_vm.Results == null)
            ResultsTable.ItemsSource = null;
    }

    /// <summary>选中同步：VM 恢复的选中回写到表格（RowClick 反向不需要，表格自己已选中）。</summary>
    private void ApplySelection()
    {
        if (_vm == null) return;
        if (_vm.SelectedItem == null)
        {
            ResultsTable.ClearSelection();
        }
        else if (!ReferenceEquals(ResultsTable.SelectedItem, _vm.SelectedItem))
        {
            ResultsTable.SelectItem(_vm.SelectedItem);
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    // —— 渲染原语（纯视图） ——

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
        RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
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
        AddHighlightedRuns(titleBlock, displayTitle, _vm?.LastQuery ?? "", (Brush)FindResource("OnSurface"));
        textStack.Children.Add(titleBlock);

        var urlBlock = new TextBlock
        {
            FontSize = 11.5,
            Foreground = (Brush)FindResource("OnSurfaceVariant"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        };
        AddHighlightedRuns(urlBlock, item.Url, _vm?.LastQuery ?? "", (Brush)FindResource("OnSurfaceVariant"));
        textStack.Children.Add(urlBlock);

        panel.Children.Add(iconGrid);
        panel.Children.Add(textStack);
        return panel;
    }

    /// <summary>普通文本单元格（表格化信息列统一规格）。</summary>
    private TextBlock TextCell(string text, double fontSize)
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

    private static BitmapImage? TryLoadFavicon(string? faviconUrl)
        => FaviconService.LoadFromCache(faviconUrl);
}

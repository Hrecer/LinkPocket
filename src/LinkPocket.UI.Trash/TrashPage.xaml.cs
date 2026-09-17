using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Api;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views
{
    /// <summary>
    /// 回收站页（v2 层级化）：树（纯展示）+ 面包屑（纯展示）+ 共享数据表平铺。
    /// 表格为 SortableDataTable 默认工厂模式（搜索页同款）：名称/类型/原位置/删除时间，表头可排序。
    /// 本期无还原；「永久删除」= 单条书签 or 整个被删文件夹单元（含子树）。
    /// </summary>
    public partial class TrashPage : UserControl
    {
        public TrashPage()
        {
            InitializeComponent();
            Focusable = true;
            Loaded += (_, _) => { Keyboard.Focus(this); EnsureSidebarSubscription(); RefreshCrumbs(); };
            SetupTrashTable();

            TrashSidebar.DataContext = _sidebar;
            TrashTable.RowClick += (_, item) =>
            {
                if (Vm is RecycleBinViewModel vm && item is TrashEntryDto entry)
                    vm.SelectedEntry = entry;
            };
            // 双击：书签 = 只读详情页；文件夹单元 = 打开目录（EnterUnitCommand，失败提示在 VM）
            TrashTable.RowDoubleClick += (_, item) =>
            {
                if (item is not TrashEntryDto entry) return;
                if (entry.EntryType == "folder") Vm?.EnterUnitCommand.Execute(entry);
                else ShowLinkDetail(entry);
            };
        }

        /// <summary>阶段 10 模块化：DataContext = RecycleBinViewModel（Shell 装配注入），本视图不认识 MainViewModel。</summary>
        private RecycleBinViewModel? Vm => DataContext as RecycleBinViewModel;

        // ===== 右侧只读详情栏：跟随 SelectedEntry（VM INPC 驱动，含 purge/清空后的清空态） =====
        private readonly TrashSidebarModel _sidebar = new();
        private RecycleBinViewModel? _sidebarSubscribedVm;

        private void EnsureSidebarSubscription()
        {
            var vm = Vm;
            if (vm == null || _sidebarSubscribedVm == vm) return;
            vm.PropertyChanged += Vm_PropertyChanged;
            _sidebarSubscribedVm = vm;
            UpdateSidebar();
        }

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecycleBinViewModel.SelectedEntry))
                UpdateSidebar();
            else if (e.PropertyName == nameof(RecycleBinViewModel.IsInUnit) ||
                     e.PropertyName == nameof(RecycleBinViewModel.CurrentUnitName))
                RefreshCrumbs();
        }

        private void UpdateSidebar()
        {
            var entry = Vm?.SelectedEntry;
            if (entry == null) _sidebar.Clear();
            else _sidebar.Show(entry);
        }

        // ===== 打开目录：进入被删文件夹单元（动作命令在 RecycleBinViewModel，面包屑由 INPC 驱动） =====

        private void UnitBack_Click(object sender, RoutedEventArgs e)
        {
            Vm?.BackCommand.Execute(null);
        }

        /// <summary>面包屑随浏览层级切换：根 = [回收站]；单元内 = [回收站, 单元名]。</summary>
        private void RefreshCrumbs()
        {
            var vm = Vm;
            if (vm == null) return;
            TrashCrumbs.Breadcrumbs = vm.IsInUnit
                ? new BreadcrumbSegment[]
                {
                    new() { Name = "回收站" },
                    new() { Name = vm.CurrentUnitName, IsLast = true }
                }
                : new BreadcrumbSegment[]
                {
                    new() { Name = "回收站", IsLast = true }
                };
        }

        // ===== 只读详情页（回收站书签）：整页覆盖，无任何编辑入口 =====

        private TrashEntryDto? _detailEntry;

        private void ShowLinkDetail(TrashEntryDto entry)
        {
            _detailEntry = entry;

            DetailName.Text = EntryName(entry);
            DetailUrl.Text = entry.Url ?? string.Empty;
            DetailOrigin.Text = string.IsNullOrWhiteSpace(entry.OriginPath) ? "全部书签" : entry.OriginPath;
            DetailDeletedAt.Text = entry.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            DetailId.Text = entry.Id;

            var favicon = FaviconService.LoadFromCache(entry.FaviconUrl);
            DetailFavicon.Source = favicon;
            DetailFavicon.Visibility = favicon != null ? Visibility.Visible : Visibility.Collapsed;
            DetailFaviconFallback.Visibility = favicon == null ? Visibility.Visible : Visibility.Collapsed;

            LinkDetailOverlay.Visibility = Visibility.Visible;
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e)
            => LinkDetailOverlay.Visibility = Visibility.Collapsed;

        private void DetailCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(DetailUrl.Text); } catch { /* 剪贴板被占用时不阻断 */ }
        }

        /// <summary>表格列定义（工厂模式：CellFactory + SortKey，表头可点击排序）。</summary>
        private void SetupTrashTable()
        {
            TrashTable.Columns = new[]
            {
                new DataTableColumn
                {
                    Field = "name", Label = "名称", Width = -1,
                    SortKey = r => (IComparable)EntryName((TrashEntryDto)r),
                    CellFactory = r => BuildNameCell((TrashEntryDto)r)
                },
                new DataTableColumn
                {
                    Field = "type", Label = "类型", Width = 90,
                    SortKey = r => (IComparable)((TrashEntryDto)r).EntryType,
                    CellFactory = r => TextCell(((TrashEntryDto)r).EntryType == "folder" ? "文件夹" : "链接", 12.5)
                },
                new DataTableColumn
                {
                    Field = "origin_path", Label = "原位置", Width = -2,
                    SortKey = r => ((TrashEntryDto)r).OriginPath ?? "",
                    CellFactory = r => TextCell(((TrashEntryDto)r).OriginPath ?? "全部书签", 12.5)
                },
                new DataTableColumn
                {
                    Field = "deleted_at", Label = "删除时间", Width = 150,
                    SortKey = r => (IComparable)((TrashEntryDto)r).DeletedAt,
                    CellFactory = r => TextCell(
                        ((TrashEntryDto)r).DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 12.5)
                },
            };
        }

        private static string EntryName(TrashEntryDto e) => string.IsNullOrEmpty(e.Name) ? (e.Url ?? "") : e.Name;

        /// <summary>名称列：图标（文件夹琥珀灰化 / favicon 或链接图标）+ 名称。</summary>
        private FrameworkElement BuildNameCell(TrashEntryDto entry)
        {
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal };

            var iconGrid = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

            if (entry.EntryType == "folder")
            {
                iconGrid.Children.Add(new M3Icon
                {
                    Kind = "folder", Width = 16, Height = 16,
                    Foreground = (Brush)FindResource("OnSurfaceVariant"),
                    Opacity = 0.55,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            else
            {
                var faviconBmp = FaviconService.LoadFromCache(entry.FaviconUrl);
                var faviconImg = new Image
                {
                    Stretch = Stretch.Uniform,
                    Source = faviconBmp,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
                if (faviconBmp == null) faviconImg.Visibility = Visibility.Collapsed;

                var linkIcon = new M3Icon
                {
                    Kind = "link-variant", Width = 15, Height = 15,
                    Foreground = (Brush)FindResource("OnSurfaceVariant"),
                    Opacity = 0.55,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (faviconBmp != null) linkIcon.Visibility = Visibility.Collapsed;

                iconGrid.Children.Add(faviconImg);
                iconGrid.Children.Add(linkIcon);
            }

            var text = new TextBlock
            {
                Text = EntryName(entry),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("OnSurface"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(iconGrid);
            panel.Children.Add(text);
            return panel;
        }

        private TextBlock TextCell(string text, double fontSize) => new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = (Brush)FindResource("OnSurfaceVariant"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        /// <summary>加载 + 渲染：VM 装载后刷新空态/引导/面包屑/侧栏（表行由 ItemsSource 绑定自动更新）。</summary>
        public async Task RefreshAsync()
        {
            if (Vm == null) return;
            EnsureSidebarSubscription();
            await Vm.LoadAsync();
            UpdateSidebar();
            RefreshCrumbs();
            RenderStates();
        }

        private void RenderStates()
        {
            var vm = Vm;
            if (vm == null) return;

            TrashTable.EmptyContent = vm.HasItems
                ? BuildState("delete-outline", "选择条目进行操作", null)
                : BuildState("delete-outline", "回收站是空的",
                    "删除的书签和文件夹会出现在这里，并保留删除时的位置");
        }

        /// <summary>空态/引导占位（MD3E 徽章）。</summary>
        private FrameworkElement BuildState(string iconKind, string title, string? subtitle)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 56, 0, 0) };
            var badge = new Border
            {
                Width = 96, Height = 96, CornerRadius = new CornerRadius(32),
                Background = (Brush)FindResource("SecondaryContainer"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.Child = new M3Icon
            {
                Kind = iconKind, Width = 40, Height = 40,
                Foreground = (Brush)FindResource("OnSecondaryContainer"),
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

        /// <summary>永久删除：确认/失败提示都在 RecycleBinViewModel.PurgeCommand（对话框端口）。</summary>
        private void TrashPurge_Click(object sender, RoutedEventArgs e)
        {
            Vm?.PurgeCommand.Execute(null);
        }

        private void TrashPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;

            if (e.Key == Key.Escape)
            {
                // Esc 优先级：详情页 > 单元浏览 > 清除选中
                if (LinkDetailOverlay.Visibility == Visibility.Visible)
                {
                    LinkDetailOverlay.Visibility = Visibility.Collapsed;
                }
                else if (vm.IsInUnit)
                {
                    UnitBack_Click(this, new RoutedEventArgs());
                }
                else
                {
                    vm.SelectedEntry = null;
                    TrashTable.ClearSelection();
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete && vm.HasSelection && LinkDetailOverlay.Visibility != Visibility.Visible)
            {
                TrashPurge_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }
}

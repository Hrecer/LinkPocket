using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views
{
    /// <summary>
    /// 智能列表页（v2 完全重做，页面自包含）：与搜索页/回收站页同构 ——
    /// 入口卡片 → 结果视图（共享 <see cref="SortableDataTable"/> 数据表 + 可复用 <see cref="DetailSidebar"/>）。
    /// 交互口径与搜索页一致：行单击选中更新详情栏、双击进入浏览页详情页，
    /// 侧栏动作（详情/打开/编辑/删除）同一套语义；删除 = 确认后移入回收站并重载当前列表。
    /// 不依赖 MainWindow 注入：路径解析/页面切换都走 MainViewModel 公开契约。
    /// </summary>
    public partial class SmartListsPage : UserControl
    {
        /// <summary>组合根（MainWindow 构造时赋值）；本页的后端访问经它。</summary>
        public Services.AppHost Host { get; set; } = null!;

        private SearchDetailsViewModel? _details;
        private LinkItem? _selectedItem;
        private bool _wired;       // 装配守卫：只在成功路径置位（DataContext 中间态不会误锁）
        private bool _opening;     // 开卡重入守卫：防连点同一/不同卡片并发开两次
        private bool _isDeleting;  // 删除重入守卫

        public SmartListsPage()
        {
            InitializeComponent();
            SetupTable();
            Focusable = true;
            PreviewKeyDown += SmartListsPage_PreviewKeyDown;
            DataContextChanged += (_, __) => WireOnce();
            Loaded += (_, __) => WireOnce();
        }

        /// <summary>装配详情栏 + 订阅 VM（DataContext 就绪后执行一次；失败不置位，下个事件重试）。</summary>
        private void WireOnce()
        {
            if (_wired) return;
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel == null) return;
            _wired = true;

            _details = new SearchDetailsViewModel();
            SmartSidebar.DataContext = _details;
            WireDetailsCommands();

            vm.SmartListViewModel.PropertyChanged += SmartListViewModel_PropertyChanged;
            // 若装配时已处于结果页（切页往返/热重载），恢复正确状态
            ApplyShowResult(vm.SmartListViewModel.ShowResult);
        }

        private void SmartListViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel == null) return;
            if (e.PropertyName == nameof(SmartListViewModel.ShowResult))
                ApplyShowResult(vm.SmartListViewModel.ShowResult);
        }

        // ============================================================
        // —— 面板切换 ——
        // ============================================================

        private void ApplyShowResult(bool showResult)
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel == null) return;

            if (showResult)
            {
                CardPanel.Visibility = Visibility.Collapsed;
                ResultPanel.Visibility = Visibility.Visible;
                RebindResultTable();
            }
            else
            {
                ResultPanel.Visibility = Visibility.Collapsed;
                CardPanel.Visibility = Visibility.Visible;
                _selectedItem = null;
                _details?.Clear();
            }
        }

        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (_opening) return;
            if (sender is not FrameworkElement fe || fe.Tag is not string listId) return;
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel == null) return;

            _opening = true;
            try
            {
                vm.SmartListViewModel.OpenSmartList(listId); // async void：完成后经 ShowResult 驱动面板切换
            }
            finally
            {
                // 下一帧解除守卫：既挡住同刻连点，又不影响后续正常打开
                Dispatcher.BeginInvoke(new Action(() => _opening = false),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SmartListViewModel != null)
                vm.SmartListViewModel.GoBack();
        }

        // ============================================================
        // —— 数据表（共享 SortableDataTable，工厂模式，搜索页同口径） ——
        // ============================================================

        private void SetupTable()
        {
            SmartTable.Columns = new[]
            {
                new DataTableColumn
                {
                    // 名称列占 2 份剩余空间：标题下方还有 URL，必须留出可见宽度
                    // （用户 2026-09-16 反馈 URL 被大幅压缩）——空间来自右侧四列压到极限
                    Field = "title", Label = "名称", Width = -2,
                    SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                        ? ((LinkItem)r).Url : ((LinkItem)r).Title),
                    CellFactory = r => BuildNameCell((LinkItem)r)
                },
                new DataTableColumn
                {
                    // 位置列占 3 份剩余空间（名称 2 份）：路径最长、最需要宽度；
                    // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                    // 的实测宽 105 + 9 列间余量（探针实测值；改小会截断成省略号，或让相邻列贴在一起）
                    // 省下的宽度全部让给名称/位置（用户 2026-09-16 要求 URL 不再被压缩）
                    Field = "path", Label = "位置", Width = -3,
                    SortKey = r => (IComparable)ResolveFolderName(((LinkItem)r).ListId),
                    CellFactory = r => TextCell(ResolveFolderName(((LinkItem)r).ListId), 12.5)
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

            SmartTable.RowClick += (_, item) =>
            {
                _selectedItem = (LinkItem)item;
                _details?.UpdateFrom(_selectedItem, ResolveFolderName(_selectedItem.ListId));
            };
            SmartTable.RowDoubleClick += (_, item) => OpenSelectedInBrowserPage((LinkItem)item);
            // 表头点击排序 → 同步更新"当前排序"文案（列表内排序由控件自身完成）
            SmartTable.SortChanged += (_, e) => UpdateSortHint(e.Field, e.Ascending);
        }

        /// <summary>把当前结果集绑到表格：重设默认排序（按列表语义）+ ItemsSource + 空态 + 清选中。</summary>
        private void RebindResultTable()
        {
            if (DataContext is not MainViewModel vm || vm.SmartListViewModel?.ResultViewModel is not { } resultVm) return;

            ApplyDefaultSort(resultVm.ListId);

            // 空态先就位（ItemsSource = null 时也不至于露出旧空态），再绑数据
            SmartTable.EmptyContent = BuildSmartState("bookmark-off-outline", resultVm.EmptyMessage,
                "书签的变动会实时汇集到这里");
            SmartTable.ItemsSource = null;
            SmartTable.ItemsSource = resultVm.Items;

            _selectedItem = null;
            SmartTable.ClearSelection();   // 表格选中态与详情栏必须同步（否则残留高亮无处对应）
            _details?.Clear();
        }

        /// <summary>
        /// 默认排序 = **名称升序**（用户硬性要求：打开任何智能列表都必须有排序，且默认按名称）
        /// —— 与搜索页同一口径。用户点表头后由控件内部排序接管，文案随之更新。
        /// </summary>
        private void ApplyDefaultSort(string listId)
        {
            _ = listId; // 四个列表统一默认；保留参数以便未来按列表定制
            SmartTable.SortField = "title";
            SmartTable.SortAscending = true;
            UpdateSortHint("title", true);
        }

        /// <summary>把"当前按什么排序"写成一句可见文案（默认排序不再只靠列头小箭头表达）。</summary>
        private void UpdateSortHint(string field, bool ascending)
        {
            var label = field switch
            {
                "title" => "名称",
                "path" => "位置",
                "updated_at" => "最后更新",
                "last_visited_at" => "最后查看",
                "visit_count" => "查看次数",
                "created_at" => "创建时间",
                _ => "",
            };
            SortHintText.Text = label.Length == 0
                ? ""
                : $"· 按{label}{(ascending ? "升序" : "降序")}";
        }

        /// <summary>名称列：favicon + 标题 + URL 副行（favicon 未命中缓存时异步补拉、原位刷新）。</summary>
        private FrameworkElement BuildNameCell(LinkItem item)
        {
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal };

            var iconGrid = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var faviconBmp = FaviconService.LoadFromCache(item.FaviconUrl);
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
            textStack.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("OnSurface"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = item.Url, FontSize = 11.5,
                Foreground = (Brush)FindResource("OnSurfaceVariant"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
            });

            panel.Children.Add(iconGrid);
            panel.Children.Add(textStack);
            return panel;
        }

        /// <summary>普通文本单元格（表格化信息列统一规格，与搜索页一致）。</summary>
        private TextBlock TextCell(string text, double fontSize)
            => new()
            {
                Text = text,
                FontSize = fontSize,
                Foreground = (Brush)FindResource("OnSurfaceVariant"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

        /// <summary>位置解析：与搜索页「位置」列同一口径（沿文件夹树解析；根链接 = 全部书签）。</summary>
        private string ResolveFolderName(string? listId)
        {
            if (string.IsNullOrEmpty(listId)) return "全部书签";
            if (DataContext is MainViewModel vm)
                return MainViewModel.FindFolderPathInNodes(vm.FolderItems, listId) ?? "未知目录";
            return "未知目录";
        }

        // ============================================================
        // —— 右侧详情栏动作（数据模型复用搜索页，动作在本页注入） ——
        // ============================================================

        private void WireDetailsCommands()
        {
            if (_details == null) return;
            _details.OpenCommand = new RelayCommand(
                () => { if (_selectedItem != null) OpenSelectedInBrowserPage(_selectedItem); },
                () => _selectedItem != null);
            _details.RenameCommand = new RelayCommand(
                () => { if (_selectedItem != null) OpenSelectedInBrowserPage(_selectedItem); },
                () => _selectedItem != null);
            _details.OpenWebsiteCommand = new RelayCommand(
                () => _ = OpenSelectedWebsiteAsync(),
                () => _selectedItem != null);
            _details.DeleteCommand = new RelayCommand(
                () => _ = DeleteSelectedAsync(),
                () => _selectedItem != null);
        }

        /// <summary>进入「浏览」页的链接详情页（详情页自带完整编辑与删除入口）。</summary>
        private void OpenSelectedInBrowserPage(LinkItem item)
        {
            if (string.IsNullOrEmpty(item?.LinkId)) return;
            if (DataContext is not MainViewModel vm) return;
            vm.SelectNavCommand.Execute("browser");
            _ = vm.BrowserViewModel.OpenDetailPageByIdAsync(item.LinkId);
        }

        /// <summary>「打开网站」：默认浏览器打开并记录一次访问（与搜索页侧栏同口径）。</summary>
        private async Task OpenSelectedWebsiteAsync()
        {
            var item = _selectedItem;
            if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
            catch { /* 无法打开时保持静默 */ }
            try
            {
                await Host.Api.RecordVisitAsync(item.LinkId);
                // 统计行原位刷新（代次校验：选中未变才写回）
                if (_details != null && _selectedItem?.LinkId == item.LinkId)
                    _details.UpdateFrom(item, ResolveFolderName(item.ListId));
            }
            catch { /* 记账失败不打断 */ }
        }

        /// <summary>删除 = 确认后移入回收站（可恢复），随后重载当前列表刷新结果。</summary>
        private async Task DeleteSelectedAsync()
        {
            if (_isDeleting) return;
            var item = _selectedItem;
            if (item == null) return;

            var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
            if (!ConfirmDialog.Show("删除链接", $"将链接「{name}」移入回收站吗？", "删除", "delete-outline")) return;

            _isDeleting = true;
            try
            {
                await Host.Api.TrashLinkAsync(item.LinkId);
                _selectedItem = null;
                _details?.Clear();

                // 重载当前列表（结果集直接从 API 重拉，杜绝本地残留）
                if (DataContext is MainViewModel vm && vm.SmartListViewModel?.ResultViewModel is { } resultVm)
                {
                    await resultVm.LoadAsync();
                    RebindResultTable();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("智能列表删除链接失败", ex);
            }
            finally
            {
                _isDeleting = false;
            }
        }

        // ============================================================
        // —— 键盘与空态 ——
        // ============================================================

        private void SmartListsPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ResultPanel.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Delete && _selectedItem != null)
            {
                _ = DeleteSelectedAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (DataContext is MainViewModel vm && vm.SmartListViewModel != null)
                    vm.SmartListViewModel.GoBack();
                e.Handled = true;
            }
        }

        /// <summary>MD3E 空态视图：大圆角色块徽章 + 引导性文案（与搜索页同一规格）。</summary>
        private static FrameworkElement BuildSmartState(string iconKind, string title, string? subtitle)
        {
            var sp = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 56, 0, 0),
                IsHitTestVisible = false
            };
            var badge = new Border
            {
                Width = 96, Height = 96, CornerRadius = new CornerRadius(32),
                Background = (Brush)Application.Current.FindResource("TintPanel"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.Child = new M3Icon
            {
                Kind = iconKind, Width = 40, Height = 40,
                Foreground = (Brush)Application.Current.FindResource("OnSurface"),
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(badge);
            sp.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("OnSurface"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0)
            });
            if (!string.IsNullOrEmpty(subtitle))
                sp.Children.Add(new TextBlock
                {
                    Text = subtitle, FontSize = 12,
                    Foreground = (Brush)Application.Current.FindResource("OnSurfaceVariant"),
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
                });
            return sp;
        }
    }
}

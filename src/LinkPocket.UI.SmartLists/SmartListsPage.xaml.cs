using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views
{
    /// <summary>
    /// 智能列表页（v2 完全重做，页面自包含）：与搜索页/回收站页同构 ——
    /// 入口卡片 → 结果视图（共享 <see cref="SortableDataTable"/> 数据表 + 可复用 <see cref="DetailSidebar"/>）。
    /// MVVM：选中态、详情栏与页面动作命令（详情/打开网站/删除）在
    /// <see cref="SmartListViewModel"/>/<see cref="SmartListResultViewModel"/>；
    /// 模块化：DataContext = SmartListViewModel（Shell 装配注入），本视图不认识 MainViewModel。
    /// 交互口径与搜索页一致：行单击选中更新详情栏、双击进入浏览页详情页。
    /// </summary>
    public partial class SmartListsPage : UserControl
    {
        private SmartListResultViewModel? _boundResult;   // 当前订阅了 Reloaded 的结果 VM
        private bool _wired;       // 装配守卫：只在成功路径置位（DataContext 中间态不会误锁）
        private bool _openingGuard;    // 开卡重入守卫：防连点同一/不同卡片并发开两次
        private int _cellGen;      // 表格代次：重绑自增，favicon 异步补拉回来时校验行是否已废弃（#12）

        public SmartListsPage()
        {
            InitializeComponent();
            SetupTable();
            Focusable = true;
            DataContextChanged += (_, __) => WireOnce();
            Loaded += (_, __) => WireOnce();
            // 切到本页 → 焦点收进页内（快捷键按焦点路由；焦点掉出页面则 Esc 静默失效）
            IsVisibleChanged += (_, _) => { if (IsVisible) FocusPage(); };
        }

        private ShortcutHost? _shortcutHost;

        /// <summary>把键盘焦点收进页面根（Focusable=True；与浏览页/回收站同一套焦点不变式）。</summary>
        private void FocusPage()
        {
            if (IsLoaded && IsVisible) Keyboard.Focus(this);
        }

        /// <summary>装配订阅（DataContext 就绪后执行一次；失败不置位，下个事件重试）。</summary>
        private void WireOnce()
        {
            if (_wired) return;
            if (DataContext is not SmartListViewModel slVm) return;
            _wired = true;

            slVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SmartListViewModel.ShowResult))
                    ApplyShowResult(slVm.ShowResult);
            };
            // 若装配时已处于结果页（切页往返/热重载），恢复正确状态
            ApplyShowResult(slVm.ShowResult);

            // 快捷键：键位在 ShortcutCatalog（本页只有一条 —— 结果页 Esc 返回卡片列表）；
            // 本文件不出现任何键位声明，只做「动作 id → 命令」接线。
            _shortcutHost?.Detach();
            _shortcutHost = new ShortcutHost(
                ShortcutCatalog.Build(ShortcutPage.SmartLists,
                    new ShortcutCommandMap().Add(ShortcutAction.SmartListsBack, slVm.GoBackCommand)),
                () => ShortcutScope.SmartLists);
            _shortcutHost.Attach(this);
        }

        // ============================================================
        // —— 面板切换 ——
        // ============================================================

        private void ApplyShowResult(bool showResult)
        {
            if (DataContext is not SmartListViewModel slVm) return;

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
                // ⚠️ GoBack 后 ResultViewModel 已是 null：必须清「本页最后一次绑定的结果 VM」
                // （详情栏 DataContext 就是它的 Details），否则返回卡片页后详情栏残留选中。
                _boundResult?.ClearSelection();
                SmartTable.ClearSelection();   // 表格选中态与详情栏必须同步（否则残留高亮无处对应）
            }
        }

        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (_openingGuard) return;
            if (sender is not FrameworkElement fe || fe.Tag is not string listId) return;
            if (DataContext is not SmartListViewModel slVm) return;

            _openingGuard = true;
            try
            {
                slVm.OpenSmartList(listId); // async void：完成后经 ShowResult 驱动面板切换
            }
            finally
            {
                // 下一帧解除守卫：既挡住同刻连点，又不影响后续正常打开
                Dispatcher.BeginInvoke(new Action(() => _openingGuard = false),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // 与 Esc 同一条命令（键位在 ShortcutCatalog；按钮与快捷键不各写一份）
            if (DataContext is SmartListViewModel slVm)
            {
                slVm.GoBackCommand.Execute(null);
                FocusPage();
            }
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

            SmartTable.RowClick += (_, item) => ResultVm?.SelectItem((LinkItem)item);
            SmartTable.RowDoubleClick += (_, item) => ResultVm?.OpenInBrowserCommand.Execute(null);
            // 表头点击排序 → 同步更新"当前排序"文案（列表内排序由控件自身完成）
            SmartTable.SortChanged += (_, e) => UpdateSortHint(e.Field, e.Ascending);
        }

        private SmartListResultViewModel? ResultVm
            => (DataContext as SmartListViewModel)?.ResultViewModel;

        /// <summary>把当前结果集绑到表格：重设默认排序（按列表语义）+ ItemsSource + 空态 + 清选中。</summary>
        private void RebindResultTable()
        {
            if (ResultVm is not { } resultVm) return;
            _cellGen++;   // 表格代次自增：重绑后到达的 favicon 补拉结果一律作废（行已重建）

            // 删除重载 → 重绑（排序复位 + 行集替换 + 清表格选中）；换列表时旧订阅先解绑
            if (!ReferenceEquals(_boundResult, resultVm))
            {
                if (_boundResult != null) _boundResult.Reloaded -= OnResultReloaded;
                _boundResult = resultVm;
                resultVm.Reloaded += OnResultReloaded;
            }

            ApplyDefaultSort(resultVm.ListId);

            // 空态先就位（ItemsSource = null 时也不至于露出旧空态），再绑数据
            SmartTable.EmptyContent = BuildSmartState("bookmark-off-outline", resultVm.EmptyMessage,
                "书签的变动会实时汇集到这里");
            SmartTable.ItemsSource = null;
            SmartTable.ItemsSource = resultVm.Items;

            resultVm.ClearSelection();
            SmartTable.ClearSelection();   // 表格选中态与详情栏必须同步（否则残留高亮无处对应）
            SmartSidebar.DataContext = resultVm.Details;
        }

        private void OnResultReloaded(object? sender, EventArgs e) => RebindResultTable();

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

        /// <summary>名称列：favicon + 标题 + URL 副行（favicon 未命中缓存时异步补拉、原位刷新）。
        /// 补拉回写前校验表格代次：行可能已随重绑/换列表被回收（#12）。</summary>
        private FrameworkElement BuildNameCell(LinkItem item)
        {
            var cellGen = _cellGen;   // 捕获当前代次（Rebind 已自增）
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
                        if (cached == null || cellGen != _cellGen) return;   // 表格已重绑/行已回收：丢弃补拉结果
                        Dispatcher.Invoke(() =>
                        {
                            if (cellGen != _cellGen) return;   // 主线程再核验一次（重绑可能恰在排队期间发生）
                            faviconImg.Source = cached;
                            faviconImg.Visibility = Visibility.Visible;
                            earthIcon.Visibility = Visibility.Collapsed;
                        });
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

        /// <summary>位置解析：与搜索页「位置」列同一口径（VM 注入的组合根解析器；根链接 = 全部书签）。</summary>
        private string ResolveFolderName(string? listId)
        {
            if (string.IsNullOrEmpty(listId)) return "全部书签";
            if (DataContext is SmartListViewModel slVm)
                return slVm.ResolveFolderPath(listId);
            return "未知目录";
        }

        // ============================================================
        // —— 空态 ——
        // ============================================================

        /// <summary>MD3E 空态视图：大圆角色块徽章 + 引导性文案（与搜索页同一规格）。
        /// 实例方法 + FindResource：不依赖静态 Application.Current（无头/单测环境中 Application 可能为 null，#13）。</summary>
        private FrameworkElement BuildSmartState(string iconKind, string title, string? subtitle)
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
                Background = (Brush)FindResource("TintPanel"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.Child = new M3Icon
            {
                Kind = iconKind, Width = 40, Height = 40,
                Foreground = (Brush)FindResource("OnSurface"),
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(badge);
            sp.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("OnSurface"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0)
            });
            if (!string.IsNullOrEmpty(subtitle))
                sp.Children.Add(new TextBlock
                {
                    Text = subtitle, FontSize = 12,
                    Foreground = (Brush)FindResource("OnSurfaceVariant"),
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
                });
            return sp;
        }
    }
}

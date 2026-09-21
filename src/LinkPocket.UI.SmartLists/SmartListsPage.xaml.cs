using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;
using LinkPocket.UIKit;

using LinkPocket.I18n;

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
        private int _cellGen;      // 表格代次：重绑自增，favicon 异步补拉回来时校验行是否已废弃

        public SmartListsPage()
        {
            InitializeComponent();
            SetupTable();
            Focusable = true;
            DataContextChanged += (_, __) => WireOnce();
            Loaded += (_, __) => WireOnce();
            // 切到本页 → 焦点收进页内（快捷键按焦点路由；焦点掉出页面则 Esc 静默失效）
            // 焦点不变式 = UIKit `Views.PageFocus`（**唯一实现**，与浏览页/回收站/搜索页/工具页共用）
            IsVisibleChanged += (_, _) => { if (IsVisible) PageFocus.Take(this); };
        }

        private ShortcutHost? _shortcutHost;

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

            // 点空白 = 清选中 + 焦点收回页内（BlankClick 唯一实现；命令端在视图收口——
            // "把焦点收回页内"是视图职责，与浏览页 ClearPageSelection/ActivatePane 同口径）
            BlankClick.SetCommand(ResultContentArea, new RelayCommand(() =>
            {
                ResultVm?.ClearSelectionCommand.Execute(null);
                PageFocus.Restore(this);
            }));

            // 快捷键：键位在 ShortcutCatalog（结果页 = 只读集：↑/↓/End/Esc 分层/Enter 打开/F5 查询）；
            // 本文件不出现任何键位声明，只做「动作 id → 命令」接线（命令体委托给当前结果 VM）。
            _shortcutHost?.Detach();
            _shortcutHost = new ShortcutHost(
                ShortcutCatalog.Build(ShortcutPage.SmartLists, new ShortcutCommandMap()
                    .Add(ShortcutAction.SmartListsBack, new RelayCommand(() =>
                    {
                        slVm.EscapeOrBack();
                        if (!slVm.ShowResult) PageFocus.Take(this);
                    }))
                    .Add(ShortcutAction.SmartListsMoveUp, new RelayCommand(() => ResultVm?.MoveSelectionCommand.Execute("up")))
                    .Add(ShortcutAction.SmartListsMoveDown, new RelayCommand(() => ResultVm?.MoveSelectionCommand.Execute("down")))
                    .Add(ShortcutAction.SmartListsSelectLast, new RelayCommand(() => ResultVm?.SelectLastCommand.Execute(null)))
                    .Add(ShortcutAction.SmartListsOpen, new RelayCommand(() => ResultVm?.OpenInBrowserCommand.Execute(null)))
                    .Add(ShortcutAction.SmartListsRefresh, new RelayCommand(() => ResultVm?.RefreshCommand.Execute(null)))),
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
                PageFocus.Take(this);
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
                    // 名称列占 2 份剩余空间：标题下方还有 URL，必须留出可见宽度，空间来自右侧四列压到极限
                    Field = "title", LabelKey = "ui.noun.name", Width = -2,
                    SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkItem)r).Title)
                        ? ((LinkItem)r).Url : ((LinkItem)r).Title),
                    CellFactory = r => BuildNameCell((LinkItem)r)
                },
                new DataTableColumn
                {
                    // 位置列占 3 份剩余空间（名称 2 份）：路径最长、最需要宽度；
                    // 右侧四列压到刚好容纳内容 —— 日期列 114 = 12.5px 字号下 yyyy-MM-dd HH:mm
                    // 的实测宽 105 + 9 列间余量（探针实测值；改小会截断成省略号，或让相邻列贴在一起）
                    // 省下的宽度全部让给名称/位置，URL 不得被压缩
                    Field = "path", LabelKey = "ui.noun.location", Width = -3,
                    SortKey = r => (IComparable)ResolveFolderName(((LinkItem)r).ListId).Resolve(),
                    CellFactory = r => TextCell(ResolveFolderName(((LinkItem)r).ListId), 12.5)
                },
                new DataTableColumn
                {
                    Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 114,
                    SortKey = r => (IComparable)((LinkItem)r).UpdatedAt,
                    CellFactory = r => TextCell(UiClock.Text(((LinkItem)r).UpdatedAt.ToLocalTime()), 12.5)
                },
                new DataTableColumn
                {
                    Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 114,
                    SortKey = r => (IComparable)(((LinkItem)r).LastVisitedAt ?? DateTime.MinValue),
                    CellFactory = r => ((LinkItem)r).LastVisitedAt is { } visited
                        ? TextCell(UiClock.Text(visited.ToLocalTime()), 12.5)
                        : TextCell(Loc.K("clock.never"), 12.5)
                },
                new DataTableColumn
                {
                    Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 72,
                    SortKey = r => (IComparable)((LinkItem)r).VisitCount,
                    CellFactory = r => TextCell(Loc.K("count.viewsN", ((LinkItem)r).VisitCount), 12.5)
                },
                new DataTableColumn
                {
                    Field = "created_at", LabelKey = "ui.noun.createdAt", Width = 114,
                    SortKey = r => (IComparable)((LinkItem)r).CreatedAt,
                    CellFactory = r => TextCell(UiClock.Text(((LinkItem)r).CreatedAt.ToLocalTime()), 12.5)
                },
            };

            // 外部托管选中（SelectionEnabled=False）：单选中由结果 VM 的 ListSelection 承载
            SmartTable.RowClick += (_, item) => ResultVm?.ClickItem((LinkItem)item);
            SmartTable.RowDoubleClick += (_, item) =>
            {
                ResultVm?.ClickItem((LinkItem)item);
                ResultVm?.OpenInBrowserCommand.Execute(null);
            };
            // 表头点击排序 → 同步更新"当前排序"文案（列表内排序由控件自身完成）
            SmartTable.SortChanged += (_, e) => UpdateSortHint(e.Field, e.Ascending);
        }

        private SmartListResultViewModel? ResultVm
            => (DataContext as SmartListViewModel)?.ResultViewModel;

        /// <summary>把当前结果集绑到表格：重设默认排序（按列表语义）+ ItemsSource + 空态 + 恢复选中。</summary>
        private void RebindResultTable()
        {
            if (ResultVm is not { } resultVm) return;
            _cellGen++;   // 表格代次自增：重绑后到达的 favicon 补拉结果一律作废（行已重建）

            // 换列表 = 清选中（旧选中已无意义）；**同一列表重绑**（F5 重查 / 删除后重载）= 保留选中，
            // 只剔除已不在结果里的 ID —— 刷新后仍存在的项保留选中。
            var switchedList = !ReferenceEquals(_boundResult, resultVm);
            if (switchedList)
            {
                if (_boundResult != null)
                {
                    _boundResult.Reloaded -= OnResultReloaded;
                    _boundResult.FocusRowRequested -= OnResultFocusRowRequested;
                    _boundResult.Selection.Changed -= OnResultSelectionChanged;
                    _boundResult.RefreshCompleted -= OnResultRefreshCompleted;
                }
                _boundResult = resultVm;
                resultVm.Reloaded += OnResultReloaded;
                resultVm.FocusRowRequested += OnResultFocusRowRequested;
                resultVm.Selection.Changed += OnResultSelectionChanged;
                resultVm.RefreshCompleted += OnResultRefreshCompleted;
            }
            // 视觉顺序注入（↑/↓、Ctrl+A 语义据此计算；单一来源 = 共享表格当前排序）
            resultVm.OrderProvider = () => SmartTable.OrderedItems().OfType<LinkItem>().Select(i => i.LinkId).ToList();

            ApplyDefaultSort(resultVm.ListId);

            // 空态先就位（ItemsSource = null 时也不至于露出旧空态），再绑数据
            SmartTable.EmptyContent = BuildSmartState("bookmark-off-outline", resultVm.EmptyMessage,
                Loc.K("smartlists.liveHint"));
            SmartTable.ItemsSource = null;
            SmartTable.ItemsSource = resultVm.Items;

            if (switchedList) resultVm.ClearSelection();   // 换列表：旧选中已无意义
            else resultVm.Selection.RemoveMissing(id => resultVm.Items.Any(i => i.LinkId == id));   // 重绑：保留仍在结果里的
            OnResultSelectionChanged();   // 行重建后重新投影选中（外部托管：绘制随 ItemsSource 重建清零）
            SmartSidebar.DataContext = resultVm.Details;
        }

        private void OnResultReloaded(object? sender, EventArgs e) => RebindResultTable();

        /// <summary>用户发起的重查（F5）结束：播行入场动画（UIKit 唯一实现）+ 焦点收回页内。</summary>
        private void OnResultRefreshCompleted(object? sender, EventArgs e)
        {
            RowEntrance.Play(SmartTable.RowsList);
            PageFocus.Restore(this);
        }

        /// <summary>选中投影（**外部托管**）：把结果 VM 的选中集合画到表上——覆盖式更新，绝不累积。</summary>
        private void OnResultSelectionChanged()
        {
            if (ResultVm is not { } vm) return;
            SmartTable.ApplySelection(vm.Items.Where(i => vm.Selection.Contains(i.LinkId)).Cast<object>());
        }

        /// <summary>移动选中后把该行滚入视口（与浏览页 FocusRowRequested 同一语义）。</summary>
        private void OnResultFocusRowRequested(object? sender, LinkItem item)
            => SmartTable.ScrollItemIntoView(item);

        /// <summary>
        /// 默认排序 = **名称升序**（打开任何智能列表都必须有排序，且默认按名称）
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
                "title" => Loc.K("ui.noun.name"),
                "path" => Loc.K("ui.noun.location"),
                "updated_at" => Loc.K("ui.noun.updated"),
                "last_visited_at" => Loc.K("ui.noun.lastVisited"),
                "visit_count" => Loc.K("ui.noun.visitCount"),
                "created_at" => Loc.K("ui.noun.createdAt"),
                _ => LocValue.Empty,
            };
            SortHintText.SetText(label.IsEmpty
                ? LocValue.Empty
                : Loc.K("smartlists.sortHint", label, Loc.K(ascending ? "sort.asc" : "sort.desc")));
        }

        /// <summary>名称列：favicon + 标题 + URL 副行（favicon 未命中缓存时异步补拉、原位刷新）。
        /// 补拉回写前校验表格代次：行可能已随重绑/换列表被回收。</summary>
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
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            // 颜色走**资源引用**：一次性 FindResource 取画刷赋值会固化，换主题后停在旧主题
            earthIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Muted");
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
            var titleText = new TextBlock
            {
                Text = !string.IsNullOrEmpty(item.Title) ? item.Title : item.Url,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
            textStack.Children.Add(titleText);
            var urlText = new TextBlock
            {
                Text = item.Url, FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
            };
            urlText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            textStack.Children.Add(urlText);

            panel.Children.Add(iconGrid);
            panel.Children.Add(textStack);
            return panel;
        }

        /// <summary>数据单元格（时间戳这类用户数据，永不翻译）。</summary>
        private static TextBlock TextCell(string text, double fontSize)
        {
            var cell = BuildCell(fontSize);
            cell.Text = text;
            return cell;
        }

        /// <summary>
        /// 两个长度形态的单元格（日期这类**结构化列**）：走 <c>LocFit</c> 的降级链——
        /// 放不下时换短式（去年份），最后才截断；<b>绝不缩字号</b>（同行字号必须一致，见 UI-SPEC §3）。
        /// </summary>
        private static TextBlock TextCell(LinkPocket.I18n.LocText text, double fontSize)
            => LocFitResolver.BuildCell(text, fontSize);

        /// <summary>文案单元格（键 + 参数；语言一变自己重算）。</summary>
        private static TextBlock TextCell(LocValue text, double fontSize)
        {
            var cell = BuildCell(fontSize);
            cell.SetText(text);
            return cell;
        }

        /// <summary>普通文本单元格（表格化信息列统一规格，与搜索页一致）。</summary>
        private static TextBlock BuildCell(double fontSize)
        {
            var cell = new TextBlock
            {
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            cell.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            return cell;
        }

        /// <summary>位置解析：与搜索页「位置」列同一口径（VM 注入的组合根解析器；根链接 = 全部书签）。</summary>
        private LocValue ResolveFolderName(string? listId)
        {
            if (string.IsNullOrEmpty(listId)) return Loc.K("nav.root.bookmarks");
            if (DataContext is SmartListViewModel slVm)
                return slVm.ResolveFolderPath(listId);
            return Loc.K("path.unknown");
        }

        // ============================================================
        // —— 空态 ——
        // ============================================================

        /// <summary>MD3E 空态视图：大圆角色块徽章 + 引导性文案（与搜索页同一规格）。
        /// 颜色一律经 <c>element.SetResourceReference</c> 挂**资源引用**：既不固化（换主题跟随），
        /// 也不依赖静态 Application.Current（无头/单测环境中 Application 可能为 null）。</summary>
        private FrameworkElement BuildSmartState(string iconKind, LocValue title, LocValue? subtitle)
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
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.SetResourceReference(Border.BackgroundProperty, Theming.Tokens.AppTokens.SurfacePanel);
            var badgeIcon = new M3Icon
            {
                Kind = iconKind, Width = 40, Height = 40,
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
            badge.Child = badgeIcon;
            sp.Children.Add(badge);
            var titleText = new TextBlock
            {
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0)
            };
            titleText.SetText(title);
            titleText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
            sp.Children.Add(titleText);
            if (subtitle is { IsEmpty: false })
            {
                var subtitleText = new TextBlock
                {
                    FontSize = 12,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
                };
                subtitleText.SetText(subtitle.Value);
                subtitleText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
                sp.Children.Add(subtitleText);
            }
            return sp;
        }
    }
}

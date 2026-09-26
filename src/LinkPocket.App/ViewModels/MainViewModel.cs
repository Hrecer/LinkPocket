using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using Material3.Wpf;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels
{
    /// <summary>导航页标识（C# 侧唯一事实来源；XAML 的数据触发器仍按字面量匹配）。</summary>
    public static class NavIds
    {
        public const string Browser = "browser";
        public const string Search = "search";
        public const string SmartLists = "smartlists";
        public const string Ai = "ai";
        public const string Tools = "tools";
        public const string Trash = "trash";
        public const string Settings = "settings";
    }

    public class MainViewModel : ObservableObject
    {
        /// <summary>路径回溯的层数上限（防御异常数据造成的环）。</summary>
        private const int MaxFolderDepth = 20;

        /// <summary>
        /// 切页 / 刷新耗时的日志分类（观测面）：现场日志按它取"从设置页跳浏览页卡在哪一段"。
        /// </summary>
        /// <remarks>
        /// 记的是**阶段的墙钟耗时**（<c>LpLog.Write(…, elapsedMs:)</c> 落到 JSONL 顶层 <c>ms</c>）：
        /// <c>switch-&gt;</c> 切页总时长、<c>visibility-&gt;</c> 显隐翻转→布局跑完（见 MainWindow）、
        /// <c>refresh:</c> 防抖驱动的活跃页刷新、<c>tree:</c> 目录树重载。
        /// </remarks>
        private const string NavLogCategory = "app.nav";

        /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
        private readonly EngineClient _client;
        private readonly Services.UiEventHub _events;
        private readonly Services.UiPortProvider _ports;

        private string _currentNavId = NavIds.Browser;
        /// <summary>导航条选中项（SlidingNavStrip.SelectedItem 双向绑定）。</summary>
        private NavigationItem? _selectedNavItem;
        private ObservableCollection<NavigationItem> _navigationItems = new();
        private ObservableCollection<FolderNode> _folderItems = new();

        /// <summary>资源管理器式浏览页（P4）：由 MainWindow 取用并设为 BrowserView 的 DataContext。</summary>
        public BrowserViewModel BrowserViewModel { get; }

        // （原 _isInSecondaryPage / IsInSecondaryPage 已整体移除：它唯一的作用是让全局导航胶囊
        //   在二级视图时 Collapsed，与「导航常驻」原则冲突。页面内的视图切换由各页面自持状态。）

        public MainViewModel(EngineClient client, Services.UiEventHub events,
            Services.UiPortProvider ports,
            Services.IContentLocator? locator = null)
        {
            _client = client;
            _events = events;
            _ports = ports;

            InitializeNavigationItems();

            TrashViewModel = new TrashViewModel(client, _ports);
            SettingsViewModel = new SettingsViewModel();
            SmartListViewModel = new SmartListViewModel(client, _ports, FolderPathValue, locator);   // 结果页「跳转」= 进目录 + 选中行（定位组件，与 ID 跳转同一套语义）
            BrowserViewModel = new BrowserViewModel(client, _ports, locator);   // 共享端口槽位：对话框/导航走 IDialogService；locator = 侧栏「跳转」

            SelectNavCommand = new RelayCommand<object>(param => SelectNav(param?.ToString() ?? NavIds.Browser));

            // 导航条（SlidingNavStrip）的选中项 = SelectedNavItem（TwoWay）；启动即指向默认首页（无副作用）
            SelectedNavItem = NavigationItems.FirstOrDefault(i => i.Id == _currentNavId);

            FolderItems = new ObservableCollection<FolderNode>
            {
                new FolderNode { IsRoot = true, Name = BookmarkPath.RootToken, IconKind = "bookmark-outline", LinkCount = 0 }
            };

            // 事件推送：UiEventHub 是后端数据变更抵达界面的唯一 300ms 防抖通道，
            // 本 VM 只按当前活跃视图路由刷新（防抖在枢纽内完成）。
            _events.RefreshRequested += OnBackendRefresh;
        }

        private void OnBackendRefresh()
        {
            // 必须保留选中：写操作（重命名/新建/移动…）自己刚恢复的选中，
            // 会被防抖后的刷新抹掉 —— 表现为"刚重命名完是选中的，立马又没了"（浏览器页内处理）。
            // ⚠️ 数据闸纪律：本处理器不得同步回派协议命令；await 续体统一经 Dispatcher 执行
            // （与原内联定时器 Tick 的 async void 形态逐字等价，绝不 Task.Run 离开 UI 线程）。
            _ = RefreshActiveViewAsync();
            // 路径解析树的同步（与活跃页路由无关）：搜索页「位置」列 / 智能列表位置列 / 工具页路径
            // 都读这份快照——改名 / 移动 / 新建后，任何页面再解析都必须看到新名字。
            _ = LoadFolderTreeAsync();
        }

        private async Task RefreshActiveViewAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                switch (_currentNavId)
                {
                    case NavIds.Browser:
                        await BrowserViewModel.RefreshPreservingSelectionAsync();
                        break;
                    case NavIds.Trash:
                        await RefreshTrashAsync();
                        break;
                    case NavIds.Search:
                        SearchRefreshRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    // 事件防抖刷新补齐三页——此前只在浏览器/回收站/搜索里路由，
                    // 跨页操作（如浏览页删链接后切到智能列表/工具）会看到陈旧快照。
                    case NavIds.SmartLists:
                        await SmartListViewModel.RefreshCurrentAsync();
                        break;
                    case NavIds.Tools:
                        ToolsDataChanged?.Invoke(this, EventArgs.Empty);   // ToolsView.OnExternalDataChanged（页内重跑守卫）
                        break;
                    case NavIds.Settings:
                        break;   // 设置页无数据面，无需刷新
                }
            }
            catch (Exception ex)
            {
                // 事件驱动的刷新失败不应打断 UI——记录并暴露（观测面纪律），不再纯静默
                LpLog.Error($"debounced refresh of the active page failed ({_currentNavId})", ex);
            }
            finally
            {
                // 300ms 防抖之后的活跃页刷新耗时（cat=app.nav）：批脚本场景下这条会连续出现，
                // "防抖首次全量"是否成立看它。
                LpLog.Write(LogLevel.Info, NavLogCategory,
                    $"refresh:{_currentNavId} (debounced)", elapsedMs: watch.ElapsedMilliseconds);
            }
        }

        public string CurrentNavId
        {
            get => _currentNavId;
            set
            {
                if (_currentNavId != value)
                {
                    var oldId = _currentNavId;
                    _currentNavId = value;
                    OnPropertyChanged();
                    if (value == NavIds.Search)
                        NavigatedToSearch?.Invoke(this, EventArgs.Empty);
                    if (oldId == NavIds.Search)
                        NavigatedFromSearch?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler? NavigatedToSearch;
        public event EventHandler? NavigatedFromSearch;
        public event EventHandler? SearchRefreshRequested;
        /// <summary>外部数据变更（Shell 转发给工具页做入口对齐）。</summary>
        public event EventHandler? ToolsDataChanged;
        /// <summary>进入工具页（Shell 转发到 ToolsView.OnNavigatedTo）：去重结果的入口对齐信号。</summary>
        public event EventHandler? NavigatedToTools;

        private void RaiseToolsDataChanged() => ToolsDataChanged?.Invoke(this, EventArgs.Empty);

        public ObservableCollection<NavigationItem> NavigationItems
        {
            get => _navigationItems;
            set { _navigationItems = value; OnPropertyChanged(); }
        }

        public ObservableCollection<FolderNode> FolderItems
        {
            get => _folderItems;
            set { _folderItems = value; OnPropertyChanged(); }
        }

        /// <summary>回收站页视图模型（与浏览页 BrowserViewModel 同构的"回收站浏览器"）。</summary>
        public TrashViewModel TrashViewModel { get; }

        /// <summary>设置页视图模型。</summary>
        public SettingsViewModel SettingsViewModel { get; }

        /// <summary>智能列表页视图模型。</summary>
        public SmartListViewModel SmartListViewModel { get; }

        public ICommand SelectNavCommand { get; }

        private void InitializeNavigationItems()
        {
            NavigationItems = new ObservableCollection<NavigationItem>
            {
                new() { Id = NavIds.Browser, LabelKey = "nav.item.browser", IconKind = "folder-open-outline" },
                new() { Id = NavIds.Search, LabelKey = "nav.item.search", IconKind = "magnify" },
                new() { Id = NavIds.SmartLists, LabelKey = "nav.item.smartLists", IconKind = "trending-up" },
                new() { Id = NavIds.Ai, LabelKey = "nav.item.ai", IconKind = "auto-fix" },
                new() { Id = NavIds.Tools, LabelKey = "nav.item.tools", IconKind = "wrench-outline" },
                new() { Id = NavIds.Trash, LabelKey = "nav.item.trash", IconKind = "delete-outline" },
                new() { Id = NavIds.Settings, LabelKey = "nav.item.settings", IconKind = "cog-outline" }
            };
        }

        private async void SelectNav(string navId)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var mode = "none";
            try
            {
                CurrentNavId = navId;
                SelectedNavItem = NavigationItems.FirstOrDefault(i => i.Id == navId);

                // P4 浏览页：首次进入从根目录加载；已加载则原地重载（**入口对齐**——防抖刷新只送达
                // "事件发生时的活跃页"，非活跃期间的变更必须在这里补：去重删除 / 书签导入 / 备份导入 /
                // 回收站还原都会改这一页；页面显隐由 Shell 按视图注册表重投影）
                if (navId == NavIds.Browser)
                {
                    mode = BrowserViewModel.Rows.Count == 0 ? "firstLoad" : "refresh";
                    if (BrowserViewModel.Rows.Count == 0)
                        _ = BrowserViewModel.LoadAsync(null);
                    else
                        _ = BrowserViewModel.RefreshPreservingSelectionAsync();
                }

                if (SmartListViewModel.ShowResult)
                {
                    SmartListViewModel.GoBack();
                }

                if (navId == NavIds.Trash)
                {
                    // 切页进入 = 导航加载（亮遮罩 + 入场动画）：由页面入口装载（页面还要同步只读详情栏）
                    var navigation = _ports.Navigation;
                    if (navigation != null) await navigation.RefreshTrashPageAsync();
                }

                if (navId == NavIds.Tools)
                {
                    // 入口对齐：去重结果（主表 / 明细）可能被其它页面的变更置于陈旧——页内按视图状态决定重跑
                    NavigatedToTools?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                // async void 里未捕获的异常会被全局 handler 吞掉且后续代码不执行——
                // 这里就地记录 + 暴露，不让「切页失败」静默
                LpLog.Error($"navigation switch to {navId} failed", ex);
            }
            finally
            {
                // 耗时留痕（cat=app.nav）：浏览页的装载是 fire-and-forget（上面 `_ =`），
                // 它的真实开销看 BrowserViewModel 的 `refresh:` 行；本行 = 切页入口本身 +
                // 回收站/工具页那两个 await 的耗时。
                LpLog.Write(LogLevel.Info, NavLogCategory,
                    $"switch->{navId} mode={mode} rows={BrowserViewModel.Rows.Count} treeNodes={CountFolderNodes(FolderItems)}",
                    elapsedMs: watch.ElapsedMilliseconds);
            }
        }

        /// <summary>目录树的节点总数（日志读数用：判断"树是否随库增长"）。</summary>
        private static int CountFolderNodes(ObservableCollection<FolderNode> nodes)
        {
            var total = 0;
            foreach (var node in nodes)
            {
                total++;
                if (node.Children is { Count: > 0 } children)
                    total += CountFolderNodes(children);
            }
            return total;
        }

        /// <summary>导航条选中项（SlidingNavStrip.SelectedItem 双向绑定）：点选变化即切换页面；
        /// 程序内切页（SelectNavCommand / 端口）反写本属性让药丸滑过去——单一来源、闭环。</summary>
        public NavigationItem? SelectedNavItem
        {
            get => _selectedNavItem;
            set
            {
                if (ReferenceEquals(_selectedNavItem, value)) return;
                _selectedNavItem = value;
                OnPropertyChanged();
                if (value != null && value.Id != CurrentNavId)
                    SelectNav(value.Id);   // 递归安全：SelectNav 回写同实例 → setter 命中 ReferenceEquals 短路
            }
        }

        /// <summary>事件驱动的回收站刷新（静默：后台刷新不亮遮罩——与浏览页同口径）。</summary>
        /// <summary>目录树 / 计数重载 + 通知工具页入口对齐（工具页回调与备份导入后刷新共用同一条流水线）。</summary>
        public async Task RefreshFolderTreeAndUIAsync()
        {
            await LoadFolderTreeAsync();
            RaiseToolsDataChanged();
        }

        public async Task RefreshTrashAsync() => await TrashViewModel.LoadAsync();

        // 搜索页已迁往 SearchViewModel（MVVM）：查询执行/范围守卫在页面 VM，
        // 本类只保留 CurrentNavId 的 search 路由事件（SearchRefreshRequested 等）。

        public async Task LoadFolderTreeAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var counts = await _client.LinkStatsAsync();
                var allFolders = await _client.FolderTreeAsync();

                var folderNodes = new ObservableCollection<FolderNode>();

                var rootNode = new FolderNode
                {
                    IsRoot = true, Name = BookmarkPath.RootToken, LinkCount = counts.RootLevel,
                    IconKind = "bookmark-outline",
                    Children = new BulkObservableCollection<FolderNode>()
                };
                folderNodes.Add(rootNode);

                var lookup = new Dictionary<string, FolderNode>();
                foreach (var folder in allFolders)
                {
                    var count = counts.ByFolder.TryGetValue(folder.FolderId, out var c) ? c : 0;
                    var node = new FolderNode
                    {
                        Id = folder.FolderId, FolderId = folder.FolderId, Name = folder.Name, LinkCount = count,
                        ParentId = folder.ParentId,
                        IconKind = count > 0 ? "folder" : "folder-outline",
                        Children = new BulkObservableCollection<FolderNode>()
                    };
                    lookup[folder.FolderId] = node;
                }

                foreach (var folder in allFolders)
                {
                    if (!string.IsNullOrEmpty(folder.ParentId) && lookup.TryGetValue(folder.ParentId, out var parentNode))
                        parentNode.Children.Add(lookup[folder.FolderId]);
                    else
                        rootNode.Children.Add(lookup[folder.FolderId]);
                }

                SortFolderNodes(rootNode.Children);
                foreach (var node in lookup.Values)
                    SortFolderNodes(node.Children);

                // LoadFolderTreeAsync 的所有调用路径都在 UI 线程（命令/事件/Loaded），
                // Dispatcher.Invoke 冗余——直接赋值（FolderItems setter 已 OnPropertyChanged）
                FolderItems = folderNodes;
            }
            catch (Exception ex)
            {
                LpLog.Error("folder tree load failed", ex);
            }
            finally
            {
                // 目录树重载 = 两条引擎读（统计 + 全量树）+ 建节点（cat=app.nav）。
                // 它挂在 300ms 防抖上（与活跃页刷新并行发），是"树全量重建"嫌疑的取证点。
                LpLog.Write(LogLevel.Info, NavLogCategory,
                    $"tree:loaded nodes={CountFolderNodes(FolderItems)}", elapsedMs: watch.ElapsedMilliseconds);
            }
        }

        private void SortFolderNodes(ObservableCollection<FolderNode> nodes)
        {
            // 根节点恒在最前，其余按名升序：根段现在是 token，混进名称排序会改变目录树次序
            var sorted = nodes.OrderBy(n => !n.IsRoot).ThenBy(n => n.Name, NameOrder.Comparer).ToList();
            nodes.Clear();
            foreach (var n in sorted)
                nodes.Add(n);
        }

        /// <summary>
        /// 目录的显示路径（键 + 参数）：根段是<b>文案</b>（<c>nav.root.bookmarks</c>）、用户文件夹名是<b>数据</b>，
        /// 拼在渲染边界发生——切语言时路径里的根名自己会换，宿主不需要记得重建。
        /// </summary>
        public LocValue FolderPathValue(string? folderId)
        {
            if (string.IsNullOrEmpty(folderId)) return Loc.K("nav.root.bookmarks");
            var treePath = FindFolderPathInNodes(FolderItems, folderId);
            return treePath is null
                ? Loc.K("path.unknown")
                : Loc.K("path.joined", Loc.K("nav.root.bookmarks"), treePath);
        }

        /// <summary>
        /// 在目录树里找某目录的**用户段路径**（不含根段；根段由调用方用 <c>path.joined</c> 拼一次）。
        /// </summary>
        /// <remarks>
        /// 根节点（<see cref="FolderNode.IsRoot"/>，`@root` / `@trash`）**不占段**：树里的根节点带着
        /// canonical token，若把它也拼进去，调用方再前缀一次根名就会出现"全部书签 &gt; 全部书签 &gt; …"
        /// （实测：搜索页「位置」列与右栏「位置」行都这么被拼出过双根）。
        /// </remarks>
        public static string? FindFolderPathInNodes(ObservableCollection<FolderNode> nodes, string folderId, string? parentPath = null)
        {
            foreach (var node in nodes)
            {
                // 根节点的段由调用方拼（见上面的说明）；其余节点是用户文件夹名，投影后拼进路径。
                var currentPath = parentPath;
                if (!node.IsRoot)
                {
                    var shown = BookmarkDisplay.Segment(node.Name);
                    currentPath = parentPath == null ? shown : $"{parentPath} > {shown}";
                }
                if (node.Id == folderId)
                    return currentPath;
                if (node.Children != null && node.Children.Count > 0)
                {
                    var result = FindFolderPathInNodes(node.Children, folderId, currentPath);
                    if (result != null)
                        return result;
                }
            }
            return null;
        }

        /// <summary>
        /// 目录的显示路径（键 + 参数）：根段是<b>文案</b>、用户文件夹名是<b>数据</b>，
        /// 交给取词在渲染边界拼——于是切语言时路径里的根名自己会换。
        /// </summary>
        public async Task<LocValue> ResolveLinkPathAsync(string? listId)
        {
            if (string.IsNullOrEmpty(listId)) return Loc.K("nav.root.bookmarks");
            var treePath = FindFolderPathInNodes(FolderItems, listId);
            if (treePath != null) return Loc.K("path.joined", Loc.K("nav.root.bookmarks"), treePath);
            try
            {
                var allFolders = await _client.FolderTreeAsync();
                var dict = allFolders.ToDictionary(f => f.FolderId);
                // 未找到的目录必须如实标记「未知目录」，不得伪装成根
                //（与 EfTreeService.PathCanonicalAsync 同口径）
                if (!dict.ContainsKey(listId)) return Loc.K("path.unknown");
                var pathParts = new List<string>();
                var currentId = listId;
                for (var i = 0; i < MaxFolderDepth && !string.IsNullOrEmpty(currentId); i++)
                {
                    if (!dict.TryGetValue(currentId, out var folder)) break;
                    pathParts.Add(folder.Name ?? Loc.T("folder.untitled"));
                    currentId = folder.ParentId ?? "";
                }
                pathParts.Reverse();
                return Loc.K("path.joined", Loc.K("nav.root.bookmarks"), string.Join(" > ", pathParts));
            }
            catch (Exception ex)
            {
                LpLog.Warn($"failed to resolve the folder of a link (listId={listId})", ex);
                return Loc.K("path.unknown");
            }
        }

        public async Task ReinitializeDatabaseAsync()
        {
            // maintenance.reinit（Destructive 两阶段确认）：引擎整库重置 = 批量删除后全新空库
            //（旧 resetData 参数的「删文件 vs 只清数据」差异已收敛为引擎的「清空重建」——参数恒无意义，已删）
            await EngineConfirm.RunAsync(token => _client.MaintenanceReinitAsync(new CallOptions { ConfirmToken = token }));

            await ResetUiAfterDatabaseResetAsync();
        }

        /// <summary>整库重置后的 UI 收尾（清树回根）：库已清空，浏览页必须强制回根
        /// 重载，否则旧目录行一直挂着；选中回到「全部书签」。此方法不触达引擎。</summary>
        public async Task ResetUiAfterDatabaseResetAsync()
        {
            await LoadFolderTreeAsync();

            // 库已清空：浏览页必须强制回到根并重载，
            // 否则旧目录的行会一直挂在浏览页上，直到手动点「全部书签」才刷新——实测已复现。
            await BrowserViewModel.LoadAsync(null);

            ToolsDataChanged?.Invoke(this, EventArgs.Empty);

            // 无论当前在哪个页（清空动作发生在设置页），选中都回到「全部书签」：
            // 旧选中若指向已删除的文件夹则是无意义状态，且会阻碍浏览器页数据刷新。
            // ⚠️ 清的是**浏览页真正的选中集合**（ListSelection 是选中的唯一事实来源）：
            // 旧写法走一个早已没有读者的 SelectionManager，等于什么都没清。
            BrowserViewModel.ClearSelection();
        }

    }
}
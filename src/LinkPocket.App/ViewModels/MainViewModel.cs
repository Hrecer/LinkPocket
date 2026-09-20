using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using Material3.Wpf;

namespace LinkPocket.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly Managers.SelectionManager _selectionManager;

        /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
        private readonly EngineClient _client;
        private readonly Services.UiEventHub _events;
        private readonly Services.UiPortProvider _ports;

        private string _currentNavId = "browser";
        /// <summary>导航条选中项（SlidingNavStrip.SelectedItem 双向绑定）。</summary>
        private NavigationItem? _selectedNavItem;
        private ObservableCollection<NavigationItem> _navigationItems = new();
        private ObservableCollection<FolderNode> _folderItems = new();

        /// <summary>资源管理器式浏览页（P4）：由 MainWindow 取用并设为 BrowserView 的 DataContext。</summary>
        public BrowserViewModel BrowserViewModel { get; }
        private TrashViewModel? _trashViewModel;
        private SettingsViewModel? _settingsViewModel;
        private SmartListViewModel? _smartListViewModel;

        // （原 _isInSecondaryPage / IsInSecondaryPage 已整体移除：它唯一的作用是让全局导航胶囊
        //   在二级视图时 Collapsed，与「导航常驻」原则冲突。页面内的视图切换由各页面自持状态。）

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(EngineClient client, Services.UiEventHub events,
            Services.UiPortProvider ports, Managers.SelectionManager selectionManager,
            Services.IContentLocator? locator = null)
        {
            _client = client;
            _events = events;
            _ports = ports;
            _selectionManager = selectionManager;

            InitializeNavigationItems();

            _trashViewModel = new TrashViewModel(client, _ports);
            _settingsViewModel = new SettingsViewModel();
            _smartListViewModel = new SmartListViewModel(client, _ports,
                listId => string.IsNullOrEmpty(listId)
                    ? "全部书签"
                    : (FindFolderPathInNodes(FolderItems, listId) ?? "未知目录"),
                locator);   // 结果页「跳转」= 进目录 + 选中行（定位组件，与 ID 跳转同一套语义）
            BrowserViewModel = new BrowserViewModel(client, _ports, locator);   // 共享端口槽位：对话框/导航走 IDialogService；locator = 侧栏「跳转」

            SelectNavCommand = new RelayCommand<object>(param => SelectNav(param?.ToString() ?? "browser"));

            // 导航条（SlidingNavStrip）的选中项 = SelectedNavItem（TwoWay）；启动即指向默认首页（无副作用）
            SelectedNavItem = NavigationItems.FirstOrDefault(i => i.Id == _currentNavId);

            FolderItems = new ObservableCollection<FolderNode>
            {
                new FolderNode { IsRoot = true, Name = FolderIds.RootDisplayName, IconKind = "bookmark-outline", LinkCount = 0 }
            };

            // 事件推送（定稿）：UiEventHub 是后端数据变更抵达界面的唯一 300ms 防抖通道，
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
            try
            {
                switch (_currentNavId)
                {
                    case "browser":
                        await BrowserViewModel.RefreshPreservingSelectionAsync();
                        break;
                    case "trash":
                        await RefreshTrashAsync();
                        break;
                    case "search":
                        OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    // 事件防抖刷新补齐三页——此前只在浏览器/回收站/搜索里路由，
                    // 跨页操作（如浏览页删链接后切到智能列表/工具）会看到陈旧快照。
                    case "smartlists":
                        if (_smartListViewModel != null)
                            await _smartListViewModel.RefreshCurrentAsync();
                        break;
                    case "tools":
                        OnToolsDataChanged?.Invoke(this, EventArgs.Empty);   // ToolsView.OnExternalDataChanged（页内重跑守卫）
                        break;
                    case "settings":
                        break;   // 设置页无数据面，无需刷新
                }
            }
            catch (Exception ex)
            {
                // 事件驱动的刷新失败不应打断 UI——记录并暴露（观测面纪律），不再纯静默
                LpLog.Error($"防抖刷新活跃页失败（{_currentNavId}）", ex);
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
                    if (value == "search")
                        OnNavigatedToSearch?.Invoke(this, EventArgs.Empty);
                    if (oldId == "search")
                        OnNavigatedFromSearch?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler? OnNavigatedToSearch;
        public event EventHandler? OnNavigatedFromSearch;
        public event EventHandler? OnSearchRefreshRequested;
        public event EventHandler? OnToolsDataChanged;
        /// <summary>进入工具页（Shell 转发到 ToolsView.OnNavigatedTo）：去重结果的入口对齐信号。</summary>
        public event EventHandler? OnNavigatedToTools;

        public ObservableCollection<NavigationItem> NavigationItems
        {
            get => _navigationItems;
            set { _navigationItems = value; OnPropertyChanged(); }
        }

        public ObservableCollection<FolderNode> FolderItems
        {
            get => _folderItems;
            set { _folderItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFolderItems)); }
        }

        public bool HasFolderItems => FolderItems?.Count > 0;

        /// <summary>回收站页视图模型（与浏览页 BrowserViewModel 同构的"回收站浏览器"）。</summary>
        public TrashViewModel? TrashViewModel
        {
            get => _trashViewModel;
            set { _trashViewModel = value; OnPropertyChanged(); }
        }

        public SettingsViewModel? SettingsViewModel
        {
            get => _settingsViewModel;
            set { _settingsViewModel = value; OnPropertyChanged(); }
        }

        public SmartListViewModel? SmartListViewModel
        {
            get => _smartListViewModel;
            set { _smartListViewModel = value; OnPropertyChanged(); }
        }

        public ICommand SelectNavCommand { get; }

        private void InitializeNavigationItems()
        {
            NavigationItems = new ObservableCollection<NavigationItem>
            {
                new() { Id = "browser", Label = "浏览", IconKind = "folder-open-outline" },
                new() { Id = "search", Label = "搜索", IconKind = "magnify" },
                new() { Id = "smartlists", Label = "智能列表", IconKind = "auto-fix" },
                new() { Id = "tools", Label = "工具", IconKind = "wrench-outline" },
                new() { Id = "trash", Label = "回收站", IconKind = "delete-outline" },
                new() { Id = "settings", Label = "设置", IconKind = "cog-outline" }
            };
        }

        private async void SelectNav(string navId)
        {
            try
            {
                CurrentNavId = navId;
                SelectedNavItem = NavigationItems.FirstOrDefault(i => i.Id == navId);

                // P4 浏览页：首次进入从根目录加载；已加载则原地重载（**入口对齐**——防抖刷新只送达
                // "事件发生时的活跃页"，非活跃期间的变更必须在这里补：去重删除 / 书签导入 / 备份导入 /
                // 回收站还原都会改这一页；页面显隐由 MainWindow.xaml 的 CurrentNavId DataTrigger 声明式控制）
                if (navId == "browser")
                {
                    if (BrowserViewModel.Rows.Count == 0)
                        _ = BrowserViewModel.LoadAsync(null);
                    else
                        _ = BrowserViewModel.RefreshPreservingSelectionAsync();
                }

                if (_smartListViewModel != null && _smartListViewModel.ShowResult)
                {
                    _smartListViewModel.GoBack();
                }

                if (navId == "trash")
                {
                    // 切页进入 = 导航加载（亮遮罩 + 入场动画）：由页面入口装载（页面还要同步只读详情栏）
                    var navigation = _ports.Navigation;
                    if (navigation != null) await navigation.RefreshTrashPageAsync();
                }

                if (navId == "tools")
                {
                    // 入口对齐：去重结果（主表 / 明细）可能被其它页面的变更置于陈旧——页内按视图状态决定重跑
                    OnNavigatedToTools?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                // async void 里未捕获的异常会被全局 handler 吞掉且后续代码不执行——
                // 这里就地记录 + 暴露，不让「切页失败」静默
                LpLog.Error($"切换导航到 {navId} 失败", ex);
            }
        }

        /// <summary>导航条选中项（SlidingNavStrip.SelectedItem 双向绑定）：用户点选变化即切换页面；
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

        public async Task RefreshFolderTreeAndUIAsync()
        {
            await LoadFolderTreeAsync();
            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>事件驱动的回收站刷新（静默：后台刷新不亮遮罩——与浏览页同口径）。</summary>
        public async Task RefreshTrashAsync()
        {
            if (_trashViewModel != null)
                await _trashViewModel.LoadAsync();
        }

        // 搜索页已迁往 SearchViewModel（MVVM）：查询执行/范围守卫在页面 VM，
        // 本类只保留 CurrentNavId 的 search 路由事件（OnSearchRefreshRequested 等）。

        public async Task LoadFolderTreeAsync()
        {
            try
            {
                var counts = await _client.LinkStatsAsync();
                var allFolders = await _client.FolderTreeAsync();

                var folderNodes = new ObservableCollection<FolderNode>();

                var rootNode = new FolderNode
                {
                    IsRoot = true, Name = FolderIds.RootDisplayName, LinkCount = counts.RootLevel,
                    IconKind = "bookmark-outline",
                    Children = new ObservableCollection<FolderNode>()
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
                        Children = new ObservableCollection<FolderNode>()
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
                LpLog.Error("加载目录树失败", ex);
            }
        }

        private void SortFolderNodes(ObservableCollection<FolderNode> nodes)
        {
            var sorted = nodes.OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList();
            nodes.Clear();
            foreach (var n in sorted)
                nodes.Add(n);
        }

        public static string? FindFolderPathInNodes(ObservableCollection<FolderNode> nodes, string folderId, string? parentPath = null)
        {
            foreach (var node in nodes)
            {
                var currentPath = parentPath == null ? node.Name : $"{parentPath} > {node.Name}";
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

        public async Task<string> ResolveLinkPathAsync(string? listId)
        {
            if (string.IsNullOrEmpty(listId)) return "全部书签";
            var treePath = FindFolderPathInNodes(FolderItems, listId);
            if (treePath != null) return treePath;
            try
            {
                var allFolders = await _client.FolderTreeAsync();
                var dict = allFolders.ToDictionary(f => f.FolderId);
                // 未找到的目录必须如实标记「未知目录」，不得伪装成根
                //（与 EfTreeService.PathDisplayAsync 修复同口径）
                if (!dict.ContainsKey(listId)) return "未知目录";
                var pathParts = new List<string>();
                var currentId = listId;
                for (int i = 0; i < 20 && !string.IsNullOrEmpty(currentId); i++)
                {
                    if (!dict.TryGetValue(currentId, out var folder)) break;
                    pathParts.Add(folder.Name ?? "未命名文件夹");
                    currentId = folder.ParentId ?? "";
                }
                pathParts.Reverse();
                return string.Join(" > ", pathParts);
            }
            catch
            {
                return "未知目录";
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
            // 否则旧目录的行会一直挂在浏览页上，直到用户手点「全部书签」才刷新——实测踩中。
            await BrowserViewModel.LoadAsync(null);

            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);

            // 无论当前在哪个页（清空动作发生在设置页），选中都回到「全部书签」：
            // 旧选中若指向已删除的文件夹则是无意义状态，且会阻碍浏览器页数据刷新。
            _selectionManager.SelectFolder(string.Empty);
        }

        /// <summary>备份导入成功后的刷新（只刷数据不重置）：树/计数重载，
        /// 列表由各页事件防抖驱动，不清选不回根（区别于整库重置的 ResetUiAfterDatabaseResetAsync）。</summary>
        public async Task RefreshAfterImportAsync()
        {
            await LoadFolderTreeAsync();
            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
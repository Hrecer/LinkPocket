using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        private ObservableCollection<NavigationItem> _navigationItems = new();
        private ObservableCollection<FolderNode> _folderItems = new();

        /// <summary>资源管理器式浏览页（P4）：由 MainWindow 取用并设为 BrowserView 的 DataContext。</summary>
        public BrowserViewModel BrowserViewModel { get; }
        private RecycleBinViewModel? _recycleBinViewModel;
        private SettingsViewModel? _settingsViewModel;
        private SmartListViewModel? _smartListViewModel;

        private bool _isEditPageVisible;
        // （原 _isInSecondaryPage / IsInSecondaryPage 已整体移除：它唯一的作用是让全局导航胶囊
        //   在二级视图时 Collapsed，与「导航常驻」原则冲突。页面内的视图切换由各页面自持状态。）
        private bool _isEditMode;

        private string _linkSortField = "title";
        private string _linkSortOrder = "asc";
        private string _folderSortOrder = "asc";
        private string _editingLinkId = string.Empty;
        private string _editLinkUrl = string.Empty;
        private string _editLinkTitle = string.Empty;
        private string _editLinkDescription = string.Empty;
        private string _editLinkIdDisplay = string.Empty;
        private string _editLinkUpdatedAtDisplay = string.Empty;
        private string _editLinkCreatedAtDisplay = string.Empty;
        private string _editLinkLastVisitedAtDisplay = string.Empty;
        private string _editLinkVisitCountDisplay = string.Empty;
        private string _fetchedFaviconUrl = string.Empty;
        private bool _editLinkIsLoading;
        private bool _editLinkHasError;
        private string _editLinkErrorMessage = string.Empty;
        private bool _isFetchingMetadata;

        private LinkItem? _viewingLink;
        private string _detailUrl = string.Empty;
        private string _detailTitle = string.Empty;
        private string _detailDescription = string.Empty;
        private string _detailFaviconUrl = string.Empty;
        private string _detailLinkIdDisplay = string.Empty;
        private string _detailUpdatedAtDisplay = string.Empty;
        private string _detailCreatedAtDisplay = string.Empty;
        private string _detailLastVisitedAtDisplay = string.Empty;
        private string _detailVisitCountDisplay = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(EngineClient client, Services.UiEventHub events,
            Services.UiPortProvider ports, Managers.SelectionManager selectionManager)
        {
            _client = client;
            _events = events;
            _ports = ports;
            _selectionManager = selectionManager;

            InitializeNavigationItems();

            _recycleBinViewModel = new RecycleBinViewModel(client, _ports);
            _settingsViewModel = new SettingsViewModel();
            _smartListViewModel = new SmartListViewModel(client, _ports,
                listId => string.IsNullOrEmpty(listId)
                    ? "全部书签"
                    : (FindFolderPathInNodes(FolderItems, listId) ?? "未知目录"));
            BrowserViewModel = new BrowserViewModel(client, _ports);   // 共享端口槽位：对话框/导航走 IDialogService（S7）

            SelectNavCommand = new RelayCommand<object>(param => SelectNav(param?.ToString() ?? "browser"));
            ShowAddLinkCommand = new RelayCommand(ShowAddLink, () => !string.IsNullOrEmpty(_selectionManager.SelectedFolderId));
            CreateFolderCommand = new RelayCommand(OpenCreateFolderDialog, () => !string.IsNullOrEmpty(_selectionManager.SelectedFolderId));
            ConfirmCreateFolderCommand = new AsyncRelayCommand(ConfirmCreateFolderAsync, () => !string.IsNullOrWhiteSpace(NewFolderName));
            CancelCreateFolderCommand = new RelayCommand(CancelCreateFolder);
            DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, CanDeleteSelected);
            EditLinkCommand = new RelayCommand<LinkItem>(EditLink);
            CancelEditLinkCommand = new RelayCommand(CancelEditLink);
            SaveEditLinkCommand = new AsyncRelayCommand(SaveEditLinkAsync, () => !EditLinkIsLoading && !string.IsNullOrWhiteSpace(EditLinkUrl) && !string.IsNullOrWhiteSpace(EditLinkTitle));
            FetchMetadataCommand = new AsyncRelayCommand(FetchMetadataAsync, CanFetchMetadata);
            ClearFaviconCommand = new RelayCommand(ClearFavicon, CanClearFavicon);
            ShowDetailCommand = new RelayCommand<LinkItem>(ShowDetail);
            DetailEditCommand = new RelayCommand(DetailEdit);
            CancelDetailCommand = new RelayCommand(CancelDetail);

            _selectionManager.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(_selectionManager.HasSelectedLink) ||
                    e.PropertyName == nameof(_selectionManager.SelectedLinkId))
                {
                    OnPropertyChanged(nameof(HasSelectedLink));
                    OnPropertyChanged(nameof(SelectedLinkId));
                }
                if (e.PropertyName == nameof(_selectionManager.SelectedFolderId))
                {
                    OnPropertyChanged(nameof(SelectedFolderId));
                    ((RelayCommand)ShowAddLinkCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)CreateFolderCommand).RaiseCanExecuteChanged();
                }
            };

            _selectionManager.SelectedLinkChanged += (sender, e) =>
            {
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            };

            SyncNavSelection("browser");

            FolderItems = new ObservableCollection<FolderNode>
            {
                new FolderNode { IsRoot = true, Name = FolderIds.RootDisplayName, IconKind = "bookmark-outline", LinkCount = 0 }
            };

            // 事件推送（阶段 8 定稿）：UiEventHub 是后端数据变更抵达界面的唯一 300ms 防抖通道，
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
                        await LoadTrashTreeAsync();
                        break;
                    case "search":
                        OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    // 审核 1.5：事件防抖刷新补齐三页——此前只在浏览器/回收站/搜索里路由，
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
                Logger.Error($"防抖刷新活跃页失败（{_currentNavId}）", ex);
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

        public string SelectedFolderId => _selectionManager.SelectedFolderId;

        public string? SelectedLinkId
        {
            get => _selectionManager.SelectedLinkId;
            set
            {
                if (value == null)
                    _selectionManager.ClearLinkSelection();
                else
                    _selectionManager.SelectLink(value);
            }
        }

        public bool HasSelectedLink => _selectionManager.HasSelectedLink;

        public event EventHandler? OnNavigatedToSearch;
        public event EventHandler? OnNavigatedFromSearch;
        public event EventHandler? OnSearchRefreshRequested;
        public event EventHandler? OnToolsDataChanged;

        public bool IsEditPageVisible
        {
            get => _isEditPageVisible;
            set { _isEditPageVisible = value; OnPropertyChanged(); }
        }

        public bool IsEditMode
        {
            get => _isEditMode;
            set { _isEditMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(EditLinkPageTitle)); OnPropertyChanged(nameof(EditLinkPageIcon)); }
        }

        public string EditLinkPageTitle => IsEditMode ? "编辑链接" : "添加新链接";
        public string EditLinkPageIcon => IsEditMode ? "pencil-outline" : "link-plus";

        public string EditLinkUrl
        {
            get => _editLinkUrl;
            set { _editLinkUrl = value; OnPropertyChanged(); ((AsyncRelayCommand)SaveEditLinkCommand).NotifyCanExecuteChanged(); ((AsyncRelayCommand)FetchMetadataCommand).NotifyCanExecuteChanged(); }
        }

        public string EditLinkTitle
        {
            get => _editLinkTitle;
            set { _editLinkTitle = value; OnPropertyChanged(); ((AsyncRelayCommand)SaveEditLinkCommand).NotifyCanExecuteChanged(); }
        }

        public string EditLinkDescription
        {
            get => _editLinkDescription;
            set { _editLinkDescription = value; OnPropertyChanged(); }
        }

        public string EditLinkFaviconUrl
        {
            get => _fetchedFaviconUrl;
            set { _fetchedFaviconUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(EditLinkHasFavicon)); EditLinkFaviconImage = FaviconService.LoadFromCache(value); ((RelayCommand)ClearFaviconCommand).RaiseCanExecuteChanged(); }
        }

        private ImageSource? _editLinkFaviconImage;
        public ImageSource? EditLinkFaviconImage
        {
            get => _editLinkFaviconImage;
            set { _editLinkFaviconImage = value; OnPropertyChanged(); }
        }

        public bool EditLinkHasFavicon => !string.IsNullOrEmpty(_fetchedFaviconUrl);

        private bool _isNewFolderDialogVisible;
        public bool IsNewFolderDialogVisible
        {
            get => _isNewFolderDialogVisible;
            set { _isNewFolderDialogVisible = value; OnPropertyChanged(); }
        }

        private string _newFolderName = string.Empty;
        public string NewFolderName
        {
            get => _newFolderName;
            set { _newFolderName = value; OnPropertyChanged(); ((AsyncRelayCommand)ConfirmCreateFolderCommand).NotifyCanExecuteChanged(); }
        }

        public string EditLinkIdDisplay
        {
            get => _editLinkIdDisplay;
            set { _editLinkIdDisplay = value; OnPropertyChanged(); }
        }

        public string EditLinkUpdatedAtDisplay
        {
            get => _editLinkUpdatedAtDisplay;
            set { _editLinkUpdatedAtDisplay = value; OnPropertyChanged(); }
        }

        public string EditLinkCreatedAtDisplay
        {
            get => _editLinkCreatedAtDisplay;
            set { _editLinkCreatedAtDisplay = value; OnPropertyChanged(); }
        }

        public string EditLinkLastVisitedAtDisplay
        {
            get => _editLinkLastVisitedAtDisplay;
            set { _editLinkLastVisitedAtDisplay = value; OnPropertyChanged(); }
        }

        public string EditLinkVisitCountDisplay
        {
            get => _editLinkVisitCountDisplay;
            set { _editLinkVisitCountDisplay = value; OnPropertyChanged(); }
        }

        public bool EditLinkIsLoading
        {
            get => _editLinkIsLoading;
            set { _editLinkIsLoading = value; OnPropertyChanged(); ((AsyncRelayCommand)SaveEditLinkCommand).NotifyCanExecuteChanged(); }
        }

        public bool EditLinkHasError
        {
            get => _editLinkHasError;
            set { _editLinkHasError = value; OnPropertyChanged(); }
        }

        public string EditLinkErrorMessage
        {
            get => _editLinkErrorMessage;
            set { _editLinkErrorMessage = value; OnPropertyChanged(); }
        }

        public bool IsFetchingMetadata
        {
            get => _isFetchingMetadata;
            set { _isFetchingMetadata = value; OnPropertyChanged(); ((RelayCommand)ClearFaviconCommand).RaiseCanExecuteChanged(); }
        }

        private string _fetchStatusMessage = string.Empty;
        public string FetchStatusMessage
        {
            get => _fetchStatusMessage;
            set { _fetchStatusMessage = value; OnPropertyChanged(); }
        }

        private bool _isFetchOverlayVisible;
        public bool IsFetchOverlayVisible
        {
            get => _isFetchOverlayVisible;
            set { _isFetchOverlayVisible = value; OnPropertyChanged(); }
        }

        public string DetailUrl
        {
            get => _detailUrl;
            set { _detailUrl = value; OnPropertyChanged(); }
        }

        public string DetailTitle
        {
            get => _detailTitle;
            set { _detailTitle = value; OnPropertyChanged(); }
        }

        private string _detailFolderName = string.Empty;
        public string DetailFolderName
        {
            get => _detailFolderName;
            set { _detailFolderName = value; OnPropertyChanged(); }
        }

        public string DetailDescription
        {
            get => _detailDescription;
            set { _detailDescription = value; OnPropertyChanged(); }
        }

        public string DetailFaviconUrl
        {
            get => _detailFaviconUrl;
            set { _detailFaviconUrl = value; OnPropertyChanged(); }
        }

        private ImageSource? _detailFaviconImage;
        public ImageSource? DetailFaviconImage
        {
            get => _detailFaviconImage;
            set { _detailFaviconImage = value; OnPropertyChanged(); }
        }

        public string DetailLinkIdDisplay
        {
            get => _detailLinkIdDisplay;
            set { _detailLinkIdDisplay = value; OnPropertyChanged(); }
        }

        public string DetailUpdatedAtDisplay
        {
            get => _detailUpdatedAtDisplay;
            set { _detailUpdatedAtDisplay = value; OnPropertyChanged(); }
        }

        public string DetailCreatedAtDisplay
        {
            get => _detailCreatedAtDisplay;
            set { _detailCreatedAtDisplay = value; OnPropertyChanged(); }
        }

        public string DetailLastVisitedAtDisplay
        {
            get => _detailLastVisitedAtDisplay;
            set { _detailLastVisitedAtDisplay = value; OnPropertyChanged(); }
        }

        public string DetailVisitCountDisplay
        {
            get => _detailVisitCountDisplay;
            set { _detailVisitCountDisplay = value; OnPropertyChanged(); }
        }

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

        private ObservableCollection<FolderNode>? _trashItems;
        public ObservableCollection<FolderNode>? TrashItems
        {
            get => _trashItems;
            set { _trashItems = value; OnPropertyChanged(); }
        }

        public RecycleBinViewModel? RecycleBinViewModel
        {
            get => _recycleBinViewModel;
            set { _recycleBinViewModel = value; OnPropertyChanged(); }
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
        public ICommand ShowAddLinkCommand { get; }
        public ICommand CreateFolderCommand { get; }
        public IAsyncRelayCommand ConfirmCreateFolderCommand { get; }
        public ICommand CancelCreateFolderCommand { get; }
        public IAsyncRelayCommand DeleteSelectedCommand { get; }
        public ICommand EditLinkCommand { get; }
        public ICommand CancelEditLinkCommand { get; }
        public IAsyncRelayCommand SaveEditLinkCommand { get; }
        public IAsyncRelayCommand FetchMetadataCommand { get; }
        public ICommand ClearFaviconCommand { get; }
        public ICommand ShowDetailCommand { get; }
        public ICommand DetailEditCommand { get; }
        public ICommand CancelDetailCommand { get; }

        public string LinkSortField
        {
            get => _linkSortField;
            set { _linkSortField = value; OnPropertyChanged(); }
        }
        public string LinkSortOrder
        {
            get => _linkSortOrder;
            set { _linkSortOrder = value; OnPropertyChanged(); }
        }
        public string FolderSortOrder
        {
            get => _folderSortOrder;
            set { _folderSortOrder = value; OnPropertyChanged(); }
        }

        private void InitializeNavigationItems()
        {
            NavigationItems = new ObservableCollection<NavigationItem>
            {
                new() { Id = "browser", Label = "浏览", IconKind = "folder-open-outline", IsSelected = true },
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
                SyncNavSelection(navId);

                // P4 浏览页：首次进入从根目录加载；页面显隐由 MainWindow.xaml 的 CurrentNavId DataTrigger 声明式控制
                if (navId == "browser")
                {
                    if (BrowserViewModel.Rows.Count == 0)
                        _ = BrowserViewModel.LoadAsync(null);
                }

                if (_smartListViewModel != null && _smartListViewModel.ShowResult)
                {
                    _smartListViewModel.GoBack();
                }

                if (navId == "trash")
                {
                    if (_recycleBinViewModel != null)
                    {
                        await _recycleBinViewModel.LoadAsync();
                        var navigation = _ports.Navigation;
                        if (navigation != null)
                            await navigation.RefreshTrashPageAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // 审核 1.4：async void 里未捕获的异常会被全局 handler 吞掉且后续代码不执行——
                // 这里就地记录 + 暴露，不让「切页失败」静默
                Logger.Error($"切换导航到 {navId} 失败", ex);
            }
        }

        private void SyncNavSelection(string navId)
        {
            foreach (var item in NavigationItems)
                item.IsSelected = item.Id == navId;
        }

        private void ShowAddLink()
        {
            if (string.IsNullOrEmpty(_selectionManager.SelectedFolderId))
                throw new InvalidOperationException("添加书签必须先选中一个文件夹");
            IsEditMode = false;
            _editingLinkId = string.Empty;
            EditLinkUrl = string.Empty;
            EditLinkTitle = string.Empty;
            EditLinkDescription = string.Empty;
            _fetchedFaviconUrl = string.Empty;
            EditLinkFaviconImage = null;
            OnPropertyChanged(nameof(EditLinkFaviconUrl));
            EditLinkIdDisplay = string.Empty;
            EditLinkUpdatedAtDisplay = string.Empty;
            EditLinkCreatedAtDisplay = string.Empty;
            EditLinkHasError = false;
            EditLinkErrorMessage = string.Empty;
            ShowEditPage();
        }

        private void EditLink(LinkItem? link)
        {
            if (link == null) return;
            IsEditMode = true;
            _editingLinkId = link.LinkId;
            EditLinkUrl = link.Url ?? string.Empty;
            EditLinkTitle = link.Title ?? string.Empty;
            EditLinkDescription = link.Description ?? string.Empty;
            var faviconUrl = link.FaviconUrl ?? string.Empty;
            _fetchedFaviconUrl = faviconUrl;
            EditLinkFaviconImage = FaviconService.LoadFromCache(faviconUrl);
            OnPropertyChanged(nameof(EditLinkFaviconUrl));
            EditLinkIdDisplay = link.LinkId ?? string.Empty;
            EditLinkUpdatedAtDisplay = link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            EditLinkCreatedAtDisplay = link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            EditLinkLastVisitedAtDisplay = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            EditLinkVisitCountDisplay = link.VisitCount == 0 ? "0 次" : $"{link.VisitCount} 次";
            EditLinkHasError = false;
            EditLinkErrorMessage = string.Empty;

            if (!string.IsNullOrWhiteSpace(faviconUrl) && EditLinkFaviconImage == null)
            {
                var capturedFaviconUrl = faviconUrl;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FaviconService.PrefetchAndCacheAsync(capturedFaviconUrl);
                        var cached = FaviconService.LoadFromCache(capturedFaviconUrl);
                        if (cached != null && _fetchedFaviconUrl == capturedFaviconUrl)
                        {
                            Application.Current.Dispatcher.Invoke(() => EditLinkFaviconImage = cached);
                        }
                    }
                    catch { }
                });
            }

            ShowEditPage();
        }

        private void ShowEditPage()
        {
            IsEditPageVisible = true;
        }

        private void CancelEditLink()
        {
            IsEditPageVisible = false;
        }

        public async Task ToggleFolderSortAsync()
        {
            _folderSortOrder = _folderSortOrder == "asc" ? "desc" : "asc";
            OnPropertyChanged(nameof(FolderSortOrder));
            await LoadFolderTreeAsync();
        }

        private async void ShowDetail(LinkItem? link)
        {
            if (link == null) return;
            _viewingLink = link;
            // 先记账、再展示：展示的就是"含本次"的统计。
            // 不要用 Task.Run —— 后台线程与 UI 线程并发使用同一个 EF DbContext 是非线程安全的
            // （UI 线程同时可能因事件防抖在刷新列表）。
            try { await _client.LinkVisitRecordAsync(link.LinkId); } catch { }
            link.LastVisitedAt = DateTime.UtcNow;
            link.VisitCount++;
            DetailUrl = link.Url ?? string.Empty;
            DetailTitle = link.Title ?? string.Empty;
            DetailDescription = link.Description ?? "（无描述）";
            DetailFaviconUrl = link.FaviconUrl ?? string.Empty;
            DetailFolderName = !string.IsNullOrEmpty(link.ListId) ? FindFolderNameById(FolderItems, link.ListId) : "全部书签";
            DetailFaviconImage = FaviconService.LoadFromCache(link.FaviconUrl);
            DetailLinkIdDisplay = link.LinkId ?? string.Empty;
            DetailUpdatedAtDisplay = link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            DetailCreatedAtDisplay = link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            DetailLastVisitedAtDisplay = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            DetailVisitCountDisplay = link.VisitCount == 0 ? "0 次" : $"{link.VisitCount} 次";
        }

        private void DetailEdit()
        {
            if (_viewingLink == null) return;
            EditLink(_viewingLink);
        }

        private async void CancelDetail()
        {
            _viewingLink = null;
            await RefreshFolderTreeAndUIAsync();
            if (_currentNavId == "search")
                OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private bool CanFetchMetadata()
        {
            return !IsFetchingMetadata && !string.IsNullOrWhiteSpace(EditLinkUrl) && Uri.TryCreate(EditLinkUrl.Trim(), UriKind.Absolute, out _);
        }

        private void ClearFavicon()
        {
            _fetchedFaviconUrl = string.Empty;
            EditLinkFaviconImage = null;
            OnPropertyChanged(nameof(EditLinkFaviconUrl));
            OnPropertyChanged(nameof(EditLinkHasFavicon));
        }

        private bool CanClearFavicon() => EditLinkHasFavicon && !IsFetchingMetadata;

        private async Task FetchMetadataAsync()
        {
            if (string.IsNullOrWhiteSpace(EditLinkUrl)) return;

            try
            {
                IsFetchingMetadata = true;
                IsFetchOverlayVisible = true;
                FetchStatusMessage = "正在解析链接...";
                EditLinkHasError = false;

                var url = EditLinkUrl.Trim();
                var metadata = await _client.LinkMetadataFetchAsync(url);

                if (metadata != null)
                {
                    bool updated = false;
                    if (!string.IsNullOrEmpty(metadata.Title))
                    {
                        EditLinkTitle = metadata.Title;
                        updated = true;
                    }
                    if (!string.IsNullOrEmpty(metadata.FaviconUrl))
                    {
                        _fetchedFaviconUrl = metadata.FaviconUrl;
                        EditLinkFaviconImage = FaviconService.LoadFromCache(metadata.FaviconUrl);
                        OnPropertyChanged(nameof(EditLinkFaviconUrl));
                        Logger.Info($"Favicon: {metadata.FaviconUrl}");
                        updated = true;
                        if (EditLinkFaviconImage == null)
                        {
                            var resolvedUrl = metadata.FaviconUrl;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await FaviconService.PrefetchAndCacheAsync(resolvedUrl);
                                    var cached = FaviconService.LoadFromCache(resolvedUrl);
                                    if (cached != null && _fetchedFaviconUrl == resolvedUrl)
                                        Application.Current.Dispatcher.Invoke(() => EditLinkFaviconImage = cached);
                                }
                                catch { }
                            });
                        }
                    }
                    if (updated)
                        FetchStatusMessage = "✓ 解析完成";
                    else
                        FetchStatusMessage = "未能获取到信息";
                }
                else
                {
                    FetchStatusMessage = "✗ 解析失败，请检查URL是否正确";
                    EditLinkHasError = true;
                    EditLinkErrorMessage = "解析失败，请检查URL是否正确";
                }
            }
            catch (Exception ex)
            {
                FetchStatusMessage = $"✗ 解析失败: {ex.Message}";
                EditLinkHasError = true;
                EditLinkErrorMessage = $"解析失败: {ex.Message}";
                Logger.Error("自动解析元数据失败", ex);
            }
            finally
            {
                IsFetchingMetadata = false;
                ((AsyncRelayCommand)FetchMetadataCommand).NotifyCanExecuteChanged();
                await Task.Delay(1200);
                IsFetchOverlayVisible = false;
            }
        }

        private async Task SaveEditLinkAsync()
        {
            if (string.IsNullOrWhiteSpace(EditLinkUrl) || string.IsNullOrWhiteSpace(EditLinkTitle)) return;

            try
            {
                EditLinkIsLoading = true;
                EditLinkHasError = false;

                var url = DecodeUrl(EditLinkUrl.Trim());
                var title = EditLinkTitle.Trim();
                var description = string.IsNullOrEmpty(EditLinkDescription?.Trim()) ? null : EditLinkDescription.Trim();

                if (IsEditMode && !string.IsNullOrEmpty(_editingLinkId))
                {
                    await _client.LinkUpdateAsync(
                        id: _editingLinkId,
                        url: url,
                        title: title,
                        description: description,
                        faviconUrl: _fetchedFaviconUrl
                    );
                    Logger.Info($"链接 {_editingLinkId} 更新成功");
                }
                else
                {
                    if (string.IsNullOrEmpty(_selectionManager.SelectedFolderId))
                        throw new InvalidOperationException("添加书签必须先选中一个文件夹");
                    await _client.LinkCreateAsync(
                        url: url,
                        title: title,
                        description: description,
                        listId: string.IsNullOrEmpty(_selectionManager.SelectedFolderId) ? null : _selectionManager.SelectedFolderId,
                        faviconUrl: _fetchedFaviconUrl
                    );
                    Logger.Info("链接添加成功");
                }

                await RefreshFolderTreeAndUIAsync();

                if (_currentNavId == "search")
                    OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);

                CancelEditLink();
            }
            catch (Exception ex)
            {
                EditLinkHasError = true;
                var innerMsg = GetFullExceptionMessage(ex);
                EditLinkErrorMessage = $"保存失败: {innerMsg}";
                Logger.Error("保存链接失败", ex);
                MessageBox.Show($"保存失败: {innerMsg}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EditLinkIsLoading = false;
            }
        }

        private static string GetFullExceptionMessage(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var current = ex;
            int depth = 0;
            while (current != null && depth < 5)
            {
                if (depth > 0) sb.Append(" → ");
                sb.Append(current.Message);
                current = current.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        public void SelectFolder(string folderId)
        {
            _selectionManager.SelectFolder(folderId);
            NotifyActionCommandsChanged();

            if (string.IsNullOrEmpty(folderId))
            {
                Logger.Info($"[选中] SelectFolder: 全部书签");
            }
            else
            {
                var folderNode = FindFolderNodeById(FolderItems, folderId);
                Logger.Info($"[选中] SelectFolder: {folderNode?.Name ?? folderId} (FolderId: {folderNode?.FolderId ?? "unknown"})");
            }
        }

        private static FolderNode? FindFolderNodeById(ObservableCollection<FolderNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) return node;
                if (node.Children != null && node.Children.Count > 0)
                {
                    var found = FindFolderNodeById(node.Children, id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public void ClearFolderSelectionVM()
        {
            _selectionManager.ClearAll();
            NotifyActionCommandsChanged();
            Logger.Info($"[选中] ClearFolderSelectionVM → 全部清除");
        }

        private void NotifyActionCommandsChanged()
        {
            ((RelayCommand)ShowAddLinkCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CreateFolderCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeleteSelectedCommand).NotifyCanExecuteChanged();
        }

        private bool CanDeleteSelected()
        {
            if (!string.IsNullOrEmpty(_selectionManager.SelectedFolderId)) return true;
            if (_selectionManager.HasSelectedLink) return true;
            return false;
        }

        private async Task DeleteSelectedAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_selectionManager.SelectedFolderId))
                {
                    var folderName = FindFolderNameById(FolderItems, _selectionManager.SelectedFolderId);
                    if (_ports.Dialogs?.ConfirmDeleteFolder(folderName) != true)
                        return;

                    await _client.FolderDeleteAsync(_selectionManager.SelectedFolderId);
                    Logger.Info($"文件夹 {_selectionManager.SelectedFolderId} 已删除");
                    await RefreshFolderTreeAndUIAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("删除失败", ex);
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>打开新建文件夹对话框（同步；审核 2.3：原命名 CreateFolderAsync 与实现矛盾——它只弹窗、无 await）。</summary>
        private void OpenCreateFolderDialog()
        {
            if (string.IsNullOrEmpty(_selectionManager.SelectedFolderId))
                throw new InvalidOperationException("新建文件夹必须先选中一个文件夹");

            NewFolderName = string.Empty;
            IsNewFolderDialogVisible = true;
        }

        private async Task ConfirmCreateFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(NewFolderName)) return;

            IsNewFolderDialogVisible = false;

            try
            {
                string? parentId = string.IsNullOrEmpty(_selectionManager.SelectedFolderId) ? null : _selectionManager.SelectedFolderId;
                await _client.FolderCreateAsync(NewFolderName.Trim(), parentId);
                await RefreshFolderTreeAndUIAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("创建文件夹失败", ex);
            }
        }

        private void CancelCreateFolder()
        {
            IsNewFolderDialogVisible = false;
            NewFolderName = string.Empty;
        }

        private static string DecodeUrl(string url)
        {
            try
            {
                return Uri.UnescapeDataString(url);
            }
            catch
            {
                return url;
            }
        }

        public async Task RefreshFolderTreeAndUIAsync()
        {
            await LoadFolderTreeAsync();
            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadTrashTreeAsync()
        {
            // 事件驱动的回收站刷新：加载平铺条目 + 被删文件夹树（页面绑定即渲染）
            if (_recycleBinViewModel != null)
                await _recycleBinViewModel.LoadAsync();
        }

        public async Task<List<LinkDto>> GetAllLinksAsync()
        {
            return await _client.LinkAllAsync();
        }

        public async Task<List<LinkDto>> GetAllLinksForToolsAsync()
        {
            return await _client.LinkAllAsync();
        }

        public async Task<(List<LinkDto> Links, int TotalCount, int CurrentPage, int LastPage)> GetLinksForSidebarAsync(string? listId = null)
        {
            var page = await _client.LinkListAsync(
                listId: listId,
                sortBy: _linkSortField, sortOrder: _linkSortOrder,
                page: 1, perPage: 50);
            return (page.Links, page.TotalCount, page.CurrentPage, page.LastPage);
        }

        public async Task<List<LinkDto>> GetRootLevelLinksAsync()
        {
            return await _client.LinkRootsAsync(_linkSortField, _linkSortOrder);
        }

        // 搜索页已迁往 SearchViewModel（阶段 9 MVVM）：查询执行/范围守卫在页面 VM，
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

                // 审核 2.4：LoadFolderTreeAsync 的所有调用路径都在 UI 线程（命令/事件/Loaded），
                // Dispatcher.Invoke 冗余——直接赋值（FolderItems setter 已 OnPropertyChanged）
                FolderItems = folderNodes;
            }
            catch (Exception ex)
            {
                Logger.Error("加载目录树失败", ex);
            }
        }

        private void SortFolderNodes(ObservableCollection<FolderNode> nodes)
        {
            var sorted = _folderSortOrder == "asc"
                ? nodes.OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList()
                : nodes.OrderByDescending(n => n.Name, StringComparer.CurrentCulture).ToList();
            nodes.Clear();
            foreach (var n in sorted)
                nodes.Add(n);
        }

        private string FindFolderNameById(ObservableCollection<FolderNode> nodes, string folderId)
        {
            var path = FindFolderPathInNodes(nodes, folderId);
            return path ?? "未命名文件夹";
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
                // 审核 2.9：未找到的目录必须如实标记「未知目录」，不得伪装成根
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

        public async Task MoveFolderAsync(string folderId, string? targetParentId)
        {
            await _client.FolderMoveAsync(folderId, targetParentId);
        }

        public async Task<string> CopyFolderDeepAsync(string folderId, string? targetParentId)
        {
            var result = await _client.FolderCopyAsync(folderId, targetParentId);
            return result.Data?.NewFolderId ?? string.Empty;
        }

        public async Task<bool> WouldFolderMoveCreateCycleAsync(string folderId, string targetParentId)
        {
            return await _client.FolderCycleCheckAsync(folderId, targetParentId);
        }

        public async Task ReinitializeDatabaseAsync()
        {
            // maintenance.reinit（Destructive 两阶段确认）：引擎整库重置 = 批量删除后全新空库
            //（审核 2.6：旧 resetData 参数的「删文件 vs 只清数据」差异已收敛为引擎的「清空重建」——参数恒无意义，已删）
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

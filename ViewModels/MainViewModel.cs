using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;
using Microsoft.EntityFrameworkCore;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private LinkPocketDbContext _db;
        private readonly LinkService _linkService;
        private readonly FolderService _folderService;
        private readonly Managers.SelectionManager _selectionManager;
        
        private string _currentNavId = "links";
        private ObservableCollection<NavigationItem> _navigationItems = new();
        private ObservableCollection<FolderNode> _folderItems = new();
        private LinkViewModel? _linkViewModel;
        private RecycleBinViewModel? _recycleBinViewModel;
        private SettingsViewModel? _settingsViewModel;
        private SmartListViewModel? _smartListViewModel;

        private bool _isEditPageVisible;
        private bool _isInSecondaryPage;
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
        private bool _editOpenedFromDetail;
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

        private void EnsureSchema()
        {
            // 数据库将清空重建，无需迁移逻辑
        }

        public MainViewModel(Managers.SelectionManager selectionManager)
        {
            _selectionManager = selectionManager;

            _db = new LinkPocketDbContext();
            _db.Database.EnsureCreated();
            EnsureSchema();

            _linkService = new LinkService(_db);
            _folderService = new FolderService(_db);

            InitializeNavigationItems();

            _linkViewModel = new LinkViewModel(_linkService, _folderService);
            _linkViewModel.LinksChanged += OnLinksChanged;
            _recycleBinViewModel = new RecycleBinViewModel(_linkService);
            _settingsViewModel = new SettingsViewModel();
            _smartListViewModel = new SmartListViewModel(_linkService);

            SelectNavCommand = new RelayCommand<object>(param => SelectNav(param?.ToString() ?? "links"));
            ShowAddLinkCommand = new RelayCommand(ShowAddLink, () => !string.IsNullOrEmpty(_selectionManager.SelectedFolderId));
            CreateFolderCommand = new RelayCommand(CreateFolderAsync, () => !string.IsNullOrEmpty(_selectionManager.SelectedFolderId));
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

            SyncNavSelection("links");

            FolderItems = new ObservableCollection<FolderNode>
            {
                new FolderNode { Id = "0", FolderId = "0", Name = "全部书签", IconKind = PackIconKind.BookmarkOutline, LinkCount = 0 }
            };
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
                    if (value == "links" && _linkViewModel != null)
                        _ = _linkViewModel.LoadLinksAsync();
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

        public bool IsInSecondaryPage
        {
            get => _isInSecondaryPage;
            set { _isInSecondaryPage = value; OnPropertyChanged(); }
        }

        public bool IsEditMode
        {
            get => _isEditMode;
            set { _isEditMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(EditLinkPageTitle)); OnPropertyChanged(nameof(EditLinkPageIcon)); }
        }

        public string EditLinkPageTitle => IsEditMode ? "编辑链接" : "添加新链接";
        public PackIconKind EditLinkPageIcon => IsEditMode ? PackIconKind.PencilOutline : PackIconKind.LinkPlus;

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

        public LinkViewModel? LinkViewModel
        {
            get => _linkViewModel;
            set { _linkViewModel = value; OnPropertyChanged(); }
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

        public Managers.LinkNavigator? LinkNavigator { get; set; }

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
                new() { Id = "links", Label = "链接", IconKind = PackIconKind.LinkVariant, IsSelected = true },
                new() { Id = "search", Label = "搜索", IconKind = PackIconKind.Magnify },
                new() { Id = "smartlists", Label = "智能列表", IconKind = PackIconKind.AutoFix },
                new() { Id = "tools", Label = "工具", IconKind = PackIconKind.WrenchOutline },
                new() { Id = "trash", Label = "回收站", IconKind = PackIconKind.DeleteOutline },
                new() { Id = "settings", Label = "设置", IconKind = PackIconKind.CogOutline }
            };
        }

        private async void SelectNav(string navId)
        {
            if (navId != CurrentNavId && CurrentNavId == "links")
            {
                _linkViewModel?.ClearSelectionCommand.Execute(null);
                _selectionManager.ClearAll();
            }

            CurrentNavId = navId;
            SyncNavSelection(navId);

            if (_smartListViewModel != null && _smartListViewModel.ShowResult)
            {
                _smartListViewModel.GoBack();
                IsInSecondaryPage = false;
            }

            if (navId == "links")
                await RefreshFolderTreeAndUIAsync();
            else if (navId == "trash")
            {
                if (_recycleBinViewModel != null)
                {
                    await _recycleBinViewModel.LoadAsync();
                    if (Application.Current.MainWindow is MainWindow mw && mw.TrashView is Views.TrashPage tp)
                        await tp.RefreshAsync();
                }
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
            _editOpenedFromDetail = false;
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
            IsInSecondaryPage = true;
            if (_linkViewModel != null)
                _linkViewModel.ClearSelectionCommand.Execute(null);
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.MainView.Visibility = Visibility.Collapsed;
                mw.DetailView.Visibility = Visibility.Collapsed;
                mw.EditLinkView.Visibility = Visibility.Visible;
                mw.ClearDetailPanel();
            }
        }

        private void CancelEditLink()
        {
            IsEditPageVisible = false;
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.EditLinkView.Visibility = Visibility.Collapsed;
                if (_editOpenedFromDetail && _viewingLink != null)
                    mw.DetailView.Visibility = Visibility.Visible;
                else
                {
                    IsInSecondaryPage = false;
                    mw.MainView.Visibility = Visibility.Visible;
                }
            }
        }

        public async Task SetLinkSortAsync(string field)
        {
            if (field == _linkSortField)
            {
                _linkSortOrder = _linkSortOrder == "asc" ? "desc" : "asc";
            }
            else
            {
                _linkSortField = field;
            }
            OnPropertyChanged(nameof(LinkSortField));
            OnPropertyChanged(nameof(LinkSortOrder));
            if (_linkViewModel != null)
            {
                var q = _linkViewModel.CurrentQuery;
                await _linkViewModel.LoadLinksAsync(new LinkQueryParams
                {
                    Search = q.Search, ListId = q.ListId,
                    IsImportant = q.IsImportant,
                    DateFrom = q.DateFrom, DateTo = q.DateTo,
                    SortBy = _linkSortField, SortOrder = _linkSortOrder,
                    Page = q.Page, PerPage = q.PerPage
                });
            }
            if (Application.Current.MainWindow is MainWindow mw)
                await mw.RefreshMainListAsync();
        }

        public async Task ToggleFolderSortAsync()
        {
            _folderSortOrder = _folderSortOrder == "asc" ? "desc" : "asc";
            OnPropertyChanged(nameof(FolderSortOrder));
            await LoadFolderTreeAsync();
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.RefreshSidebar(this);
                await mw.RefreshMainListAsync();
            }
        }

        private void ShowDetail(LinkItem? link)
        {
            if (link == null) return;
            _viewingLink = link;
            _ = _linkService.RecordVisitAsync(link.LinkId);
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

            if (_linkViewModel != null)
                _linkViewModel.ClearSelectionCommand.Execute(null);

            if (Application.Current.MainWindow is MainWindow mw)
            {
                IsInSecondaryPage = true;
                mw.MainView.Visibility = Visibility.Collapsed;
                mw.DetailView.Visibility = Visibility.Visible;
                mw.ClearDetailPanel();
            }
        }

        private void DetailEdit()
        {
            if (_viewingLink == null) return;
            _editOpenedFromDetail = true;
            EditLink(_viewingLink);
        }

        private async void CancelDetail()
        {
            _viewingLink = null;
            IsInSecondaryPage = false;
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.DetailView.Visibility = Visibility.Collapsed;
                mw.MainView.Visibility = Visibility.Visible;
            }
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
                var metadata = await _linkService.FetchMetadataAsync(url);

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
                    await _linkService.UpdateLinkAsync(
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
                    await _linkService.CreateLinkAsync(
                        url: url,
                        title: title,
                        description: description,
                        listId: _selectionManager.SelectedFolderId == "0" ? null : _selectionManager.SelectedFolderId,
                        isImportant: false,
                        autoFetchMetadata: false,
                        faviconUrl: _fetchedFaviconUrl
                    );
                    Logger.Info("链接添加成功");
                    if (Application.Current.MainWindow is MainWindow mw2)
                        mw2.ExpandFolder(_selectionManager.SelectedFolderId);
                }

                if (_linkViewModel != null)
                    await _linkViewModel.LoadLinksAsync();
                await RefreshFolderTreeAndUIAsync();

                if (_currentNavId == "search")
                    OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);

                CancelEditLink();

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (_editOpenedFromDetail && _viewingLink != null)
                    {
                        var updatedLink = _linkViewModel?.Links.FirstOrDefault(l => l.LinkId == _editingLinkId);
                        if (updatedLink != null)
                        {
                            _viewingLink = updatedLink;
                            DetailUrl = updatedLink.Url ?? string.Empty;
                            DetailTitle = updatedLink.Title ?? string.Empty;
                            DetailDescription = updatedLink.Description ?? "（无描述）";
                            DetailFaviconUrl = updatedLink.FaviconUrl ?? string.Empty;
                            DetailFaviconImage = FaviconService.LoadFromCache(updatedLink.FaviconUrl);
                            DetailLinkIdDisplay = updatedLink.LinkId ?? string.Empty;
                            DetailUpdatedAtDisplay = updatedLink.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                            DetailCreatedAtDisplay = updatedLink.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                            DetailLastVisitedAtDisplay = updatedLink.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
                            DetailVisitCountDisplay = updatedLink.VisitCount == 0 ? "0 次" : $"{updatedLink.VisitCount} 次";
                            mw.ClearDetailPanel();
                        }
                    }
                    else if (!string.IsNullOrEmpty(_editingLinkId))
                    {
                        var updatedLink = _linkViewModel?.Links.FirstOrDefault(l => l.LinkId == _editingLinkId);
                        if (updatedLink != null)
                        {
                            updatedLink.IsSelected = true;
                            mw.UpdateDetailPanel(updatedLink);
                        }
                    }
                }
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

            if (folderId == "0")
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
            if (!string.IsNullOrEmpty(_selectionManager.SelectedFolderId) && _selectionManager.SelectedFolderId != "0") return true;
            if (_selectionManager.HasSelectedLink) return true;
            if (_linkViewModel != null && _linkViewModel.HasSelectedItems) return true;
            return false;
        }

        private async Task DeleteSelectedAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_selectionManager.SelectedFolderId) && _selectionManager.SelectedFolderId != "0")
                {
                    var folderName = FindFolderNameById(FolderItems, _selectionManager.SelectedFolderId);
                    if (!ShowDeleteFolderConfirmation(folderName))
                        return;

                    await _folderService.DeleteFolderAsync(_selectionManager.SelectedFolderId);
                    Logger.Info($"文件夹 {_selectionManager.SelectedFolderId} 已删除");
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ClearFolderSelection();
                    await RefreshFolderTreeAndUIAsync();
                    return;
                }

                if (_selectionManager.HasSelectedLink && Application.Current.MainWindow is MainWindow mw2)
                {
                    var selectedLink = mw2.GetSelectedLink();
                    if (selectedLink != null)
                    {
                        await _linkService.DeleteLinkAsync(selectedLink.LinkId);
                        Logger.Info($"已将书签 {selectedLink.LinkId} 移至回收站");
                        mw2.ClearDetailPanel();
                        await RefreshFolderTreeAndUIAsync();
                    }
                }

                if (_linkViewModel != null && _linkViewModel.HasSelectedItems)
                {
                    _linkViewModel.DeleteSelectedCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("删除失败", ex);
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CreateFolderAsync()
        {
            if (string.IsNullOrEmpty(_selectionManager.SelectedFolderId))
                throw new InvalidOperationException("新建文件夹必须先选中一个文件夹");

            NewFolderName = string.Empty;
            IsNewFolderDialogVisible = true;

            if (Application.Current.MainWindow is MainWindow mw)
                mw.FocusNewFolderDialog();
        }

        private async Task ConfirmCreateFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(NewFolderName)) return;

            IsNewFolderDialogVisible = false;

            try
            {
                string? parentId = _selectionManager.SelectedFolderId == "0" ? null : _selectionManager.SelectedFolderId;
                await _folderService.CreateFolderAsync(NewFolderName.Trim(), parentId: parentId);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ExpandFolder(_selectionManager.SelectedFolderId);
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

        public async Task PasteLinksToFolderAsync(List<LinkItem> items, bool isCut)
        {
            if (items == null || items.Count == 0) return;
            if (string.IsNullOrEmpty(_selectionManager.SelectedFolderId)) return;

            try
            {
                string? listId = _selectionManager.SelectedFolderId;

                if (isCut)
                {
                    foreach (var item in items)
                    {
                        await _linkService.UpdateLinkAsync(item.LinkId, listId: listId);
                        item.IsCut = false;
                    }
                }
                else
                {
                    foreach (var item in items)
                    {
                        await _linkService.CreateLinkAsync(
                            url: item.Url,
                            title: item.Title,
                            description: item.Description,
                            listId: listId,
                            isImportant: item.IsImportant,
                            autoFetchMetadata: false,
                            faviconUrl: item.FaviconUrl
                        );
                    }
                }

                if (_linkViewModel != null)
                    await _linkViewModel.LoadLinksAsync();
                await RefreshFolderTreeAndUIAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("粘贴链接失败", ex);
            }
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

        private async void OnLinksChanged(object? sender, EventArgs e)
        {
            if (_currentNavId == "search")
                OnSearchRefreshRequested?.Invoke(this, EventArgs.Empty);
            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);
            await RefreshFolderTreeAndUIAsync();
        }

        public async Task RefreshFolderTreeAndUIAsync()
        {
            await LoadFolderTreeAsync();
            if (Application.Current.MainWindow is MainWindow mw)
            {
                await mw.RefreshSidebarAsync(this);
                await mw.RefreshMainListAsync();
            }
            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadTrashTreeAsync()
        {
            var deletedLinks = await _linkService.GetDeletedLinksAsync();

            var trashRoot = new FolderNode { Id = "-1", Name = "回收站", IconKind = PackIconKind.Delete };

            TrashItems = new ObservableCollection<FolderNode> { trashRoot };
        }

        public async Task<List<Data.Link>> GetAllLinksAsync()
        {
            return await _linkService.GetAllActiveLinksAsync();
        }

        public async Task<List<Data.Link>> GetAllLinksForToolsAsync()
        {
            return await _linkService.GetAllActiveLinksAsync();
        }

        public async Task<(List<Data.Link> Links, int TotalCount, int CurrentPage, int LastPage)> GetLinksForSidebarAsync(string? listId = null)
        {
            return await _linkService.GetLinksAsync(
                listId: listId,
                sortBy: _linkSortField, sortOrder: _linkSortOrder,
                page: 1, perPage: 50
            );
        }

        public async Task<List<Data.Link>> GetRootLevelLinksAsync()
        {
            return await _linkService.GetRootLevelLinksAsync(_linkSortField, _linkSortOrder);
        }

        public async Task<List<LinkItem>> SearchLinksByTitleAsync(string query,
            bool searchPath = false, bool searchUrl = false,
            bool searchTitle = true, bool searchDescription = false)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<LinkItem>();

            var links = await _linkService.GetAllActiveLinksAsync();

            var predicates = new List<Func<Data.Link, bool>>();

            if (searchTitle)
                predicates.Add(l => l.Title != null && l.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (searchUrl)
                predicates.Add(l => l.Url != null && l.Url.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (searchDescription)
                predicates.Add(l => l.Description != null && l.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (searchPath)
            {
                var allFolders = await _folderService.GetAllFoldersAsync();
                var matchedFolderIds = allFolders
                    .Where(f => f.Name != null && f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FolderId)
                    .ToHashSet();

                if (matchedFolderIds.Count > 0)
                {
                    var expandedIds = new HashSet<string>(matchedFolderIds);
                    foreach (var fid in matchedFolderIds.ToList())
                    {
                        var descendants = await GetDescendantFolderIdsAsync(allFolders, fid);
                        foreach (var d in descendants) expandedIds.Add(d);
                    }
                    predicates.Add(l => l.ListId != null && expandedIds.Contains(l.ListId));
                }
            }

            if (predicates.Count == 0)
            {
                predicates.Add(l => l.Title != null && l.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = links.Where(l => predicates.Any(p => p(l)));

            filtered = _linkSortOrder == "asc"
                ? _linkSortField switch
                {
                    "title" => filtered.OrderBy(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId),
                    "updated_at" => filtered.OrderBy(l => l.UpdatedAt).ThenBy(l => l.LinkId),
                    "last_visited_at" => filtered.OrderBy(l => l.LastVisitedAt ?? DateTime.MinValue).ThenBy(l => l.LinkId),
                    "visit_count" => filtered.OrderBy(l => l.VisitCount).ThenBy(l => l.LinkId),
                    "created_at" => filtered.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId),
                    _ => filtered.OrderBy(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId)
                }
                : _linkSortField switch
                {
                    "title" => filtered.OrderByDescending(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId),
                    "updated_at" => filtered.OrderByDescending(l => l.UpdatedAt).ThenBy(l => l.LinkId),
                    "last_visited_at" => filtered.OrderByDescending(l => l.LastVisitedAt ?? DateTime.MinValue).ThenBy(l => l.LinkId),
                    "visit_count" => filtered.OrderByDescending(l => l.VisitCount).ThenBy(l => l.LinkId),
                    "created_at" => filtered.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.LinkId),
                    _ => filtered.OrderByDescending(l => l.Title, StringComparer.CurrentCulture).ThenBy(l => l.LinkId)
                };

            return filtered.ToList().Select(l => new LinkItem
            {
                LinkId = l.LinkId, Url = l.Url,
                Title = l.Title ?? "",
                Description = l.Description ?? "", FaviconUrl = l.FaviconUrl ?? "",
                ListId = l.ListId, LastVisitedAt = l.LastVisitedAt,
                VisitCount = l.VisitCount, IsImportant = l.IsImportant,
                CreatedAt = l.CreatedAt, UpdatedAt = l.UpdatedAt
            }).ToList();
        }

        private async Task<List<string>> GetDescendantFolderIdsAsync(List<Data.Folder> allFolders, string folderId)
        {
            var ids = new List<string>();
            var children = allFolders.Where(f => f.ParentId == folderId).ToList();
            foreach (var child in children)
            {
                ids.Add(child.FolderId);
                ids.AddRange(await GetDescendantFolderIdsAsync(allFolders, child.FolderId));
            }
            return ids;
        }

        public async Task LoadFolderTreeAsync()
        {
            try
            {
                var totalLinks = await _linkService.GetTotalCountAsync();
                var allFolders = await _folderService.GetAllFoldersAsync();
                var linkCountByFolder = await _linkService.GetLinkCountByFolderAsync();
                var rootLinkCount = await _linkService.GetRootLevelLinkCountAsync();

                var folderNodes = new ObservableCollection<FolderNode>();

                var rootNode = new FolderNode
                {
                    Id = "0", FolderId = "0", Name = "全部书签", LinkCount = rootLinkCount,
                    IconKind = PackIconKind.BookmarkOutline,
                    Children = new ObservableCollection<FolderNode>()
                };
                folderNodes.Add(rootNode);

                var lookup = new Dictionary<string, FolderNode>();
                foreach (var folder in allFolders)
                {
                    var count = linkCountByFolder.TryGetValue(folder.FolderId, out var c) ? c : 0;
                    var node = new FolderNode
                    {
                        Id = folder.FolderId, FolderId = folder.FolderId, Name = folder.Name, LinkCount = count,
                        ParentId = folder.ParentId,
                        IconKind = count > 0 ? PackIconKind.Folder : PackIconKind.FolderOutline,
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

                Application.Current.Dispatcher.Invoke(() =>
                {
                    FolderItems = folderNodes;
                });
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
                var allFolders = await _folderService.GetAllFoldersAsync();
                var dict = allFolders.ToDictionary(f => f.FolderId);
                if (!dict.ContainsKey(listId)) return "全部书签";
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
                return "全部书签";
            }
        }

        private bool ShowDeleteFolderConfirmation(string folderName)
        {
            var dialog = new Window
            {
                Title = "删除文件夹",
                Width = 360, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = System.Windows.Media.Brushes.Transparent,
                AllowsTransparency = true
            };

            var contentPanel = new StackPanel { Margin = new Thickness(24) };

            contentPanel.Children.Add(new TextBlock
            {
                Text = "删除文件夹", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4)
            });

            contentPanel.Children.Add(new TextBlock
            {
                Text = $"确定要删除文件夹 \"{folderName}\" 吗？",
                FontSize = 14, Margin = new Thickness(0, 0, 0, 20),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80))
            });

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(1)
            };
            cancelBtn.SetValue(ButtonAssist.CornerRadiusProperty, new CornerRadius(4));
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            btnPanel.Children.Add(cancelBtn);

            var okBtn = new System.Windows.Controls.Button
            {
                Content = "确定", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 0, 238)),
                Foreground = System.Windows.Media.Brushes.White
            };
            okBtn.SetValue(ButtonAssist.CornerRadiusProperty, new CornerRadius(4));
            okBtn.Click += (s, e) => dialog.DialogResult = true;
            btnPanel.Children.Add(okBtn);

            contentPanel.Children.Add(btnPanel);

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                Child = contentPanel
            };

            dialog.Content = outerBorder;

            dialog.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    dialog.DialogResult = false;
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    dialog.DialogResult = true;
                    e.Handled = true;
                }
            };

            return dialog.ShowDialog() == true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task MoveFolderAsync(string folderId, string? targetParentId)
        {
            await _folderService.MoveFolderAsync(folderId, targetParentId);
        }

        public async Task<string> CopyFolderDeepAsync(string folderId, string? targetParentId)
        {
            return await _folderService.CopyFolderDeepAsync(folderId, targetParentId);
        }

        public async Task<bool> WouldFolderMoveCreateCycleAsync(string folderId, string targetParentId)
        {
            return await _folderService.WouldCreateCycleAsync(folderId, targetParentId);
        }

        public async Task ReinitializeDatabaseAsync(bool resetData = true)
        {
            try { _db?.Database.CloseConnection(); } catch { }
            try { _db?.Dispose(); } catch { }

            var dbPath = Path.Combine(AppContext.BaseDirectory, "linkpocket.db");
            var connString = $"Data Source={dbPath}";
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection(connString)); } catch { }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(200);

            if (resetData)
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);

                var faviconDir = Path.Combine(AppContext.BaseDirectory, "favicons");
                if (Directory.Exists(faviconDir))
                    Directory.Delete(faviconDir, true);
            }

            _db = new LinkPocketDbContext();
            _db.Database.EnsureCreated();

            _linkService.SetDb(_db);
            _folderService.SetDb(_db);

            await LoadFolderTreeAsync();

            if (Application.Current.MainWindow is MainWindow mw)
            {
                await mw.RefreshSidebarAsync(this);
                mw.ClearMainList();
                await mw.RefreshMainListAsync();
            }

            OnToolsDataChanged?.Invoke(this, EventArgs.Empty);

            _selectionManager.SelectFolder(CurrentNavId == "links" ? "0" : CurrentNavId);
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private readonly LinkPocketDbContext _db;
        private readonly LinkService _linkService;
        private readonly FolderService _folderService;
        
        private string _currentNavId = "links";
        private int _selectedFolderId = -1;
        private bool _hasSelectedLink;
        private ObservableCollection<NavigationItem> _navigationItems = new();
        private ObservableCollection<FolderNode> _folderItems = new();
        private ObservableCollection<SmartListItem> _smartListItems = new();
        private LinkViewModel? _linkViewModel;
        private SearchViewModel? _searchViewModel;
        private RecycleBinViewModel? _recycleBinViewModel;
        private SettingsViewModel? _settingsViewModel;

        private bool _isEditPageVisible;
        private bool _isEditMode;
        private int _editingLinkId;
        private string _editLinkUrl = string.Empty;
        private string _editLinkTitle = string.Empty;
        private string _editLinkDescription = string.Empty;
        private string _editLinkIdDisplay = string.Empty;
        private string _editLinkUpdatedAtDisplay = string.Empty;
        private string _editLinkCreatedAtDisplay = string.Empty;
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void EnsureSchema()
        {
            try
            {
                var conn = _db.Database.GetDbConnection();
                conn.Open();

                using var cmd = conn.CreateCommand();

                cmd.CommandText = "PRAGMA table_info(lists)";
                var columns = new HashSet<string>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        columns.Add(reader.GetString(1).ToLower());
                }

                if (!columns.Contains("is_deleted"))
                {
                    cmd.CommandText = "ALTER TABLE lists ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0";
                    cmd.ExecuteNonQuery();
                }
                if (!columns.Contains("deleted_at"))
                {
                    cmd.CommandText = "ALTER TABLE lists ADD COLUMN deleted_at TEXT NULL";
                    cmd.ExecuteNonQuery();
                }

                conn.Close();
            }
            catch { }
        }

        public MainViewModel()
        {
            _db = new LinkPocketDbContext();
            _db.Database.EnsureCreated();
            EnsureSchema();

            _linkService = new LinkService(_db);
            _folderService = new FolderService(_db);

            InitializeNavigationItems();
            InitializeSmartListItems();
            
            _linkViewModel = new LinkViewModel(_linkService, _folderService);
            _linkViewModel.LinksChanged += OnLinksChanged;
            _searchViewModel = new SearchViewModel(_linkService);
            _recycleBinViewModel = new RecycleBinViewModel(_linkService);
            _settingsViewModel = new SettingsViewModel();

            SelectNavCommand = new RelayCommand<object>(param => SelectNav(param?.ToString() ?? "links"));
            ShowAddLinkCommand = new RelayCommand(ShowAddLink, () => _selectedFolderId >= 0);
            CreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync, () => _selectedFolderId >= 0);
            DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, CanDeleteSelected);
            EditLinkCommand = new RelayCommand<LinkItem>(EditLink);
            CancelEditLinkCommand = new RelayCommand(CancelEditLink);
            SaveEditLinkCommand = new AsyncRelayCommand(SaveEditLinkAsync, () => !EditLinkIsLoading && !string.IsNullOrWhiteSpace(EditLinkUrl) && !string.IsNullOrWhiteSpace(EditLinkTitle));
            FetchMetadataCommand = new AsyncRelayCommand(FetchMetadataAsync, CanFetchMetadata);
            ClearFaviconCommand = new RelayCommand(ClearFavicon, CanClearFavicon);
            ShowDetailCommand = new RelayCommand<LinkItem>(ShowDetail);
            DetailEditCommand = new RelayCommand(DetailEdit);
            CancelDetailCommand = new RelayCommand(CancelDetail);

            SyncNavSelection("links");

            FolderItems = new ObservableCollection<FolderNode>
            {
                new FolderNode { Id = 0, Name = "全部书签", IconKind = PackIconKind.BookmarkOutline, LinkCount = 0 }
            };
        }

        public string CurrentNavId
        {
            get => _currentNavId;
            set
            {
                if (_currentNavId != value)
                {
                    _currentNavId = value;
                    OnPropertyChanged();
                    if (value == "links" && _linkViewModel != null)
                        _ = _linkViewModel.LoadLinksAsync();
                }
            }
        }

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
            set { _fetchedFaviconUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(EditLinkHasFavicon)); ((RelayCommand)ClearFaviconCommand).RaiseCanExecuteChanged(); }
        }

        public bool EditLinkHasFavicon => !string.IsNullOrEmpty(_fetchedFaviconUrl);

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

        public ObservableCollection<SmartListItem> SmartListItems
        {
            get => _smartListItems;
            set { _smartListItems = value; OnPropertyChanged(); }
        }

        public LinkViewModel? LinkViewModel
        {
            get => _linkViewModel;
            set { _linkViewModel = value; OnPropertyChanged(); }
        }

        public SearchViewModel? SearchViewModel
        {
            get => _searchViewModel;
            set { _searchViewModel = value; OnPropertyChanged(); }
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

        public ICommand SelectNavCommand { get; }
        public ICommand ShowAddLinkCommand { get; }
        public IAsyncRelayCommand CreateFolderCommand { get; }
        public IAsyncRelayCommand DeleteSelectedCommand { get; }
        public ICommand EditLinkCommand { get; }
        public ICommand CancelEditLinkCommand { get; }
        public IAsyncRelayCommand SaveEditLinkCommand { get; }
        public IAsyncRelayCommand FetchMetadataCommand { get; }
        public ICommand ClearFaviconCommand { get; }
        public ICommand ShowDetailCommand { get; }
        public ICommand DetailEditCommand { get; }
        public ICommand CancelDetailCommand { get; }

        private void InitializeNavigationItems()
        {
            NavigationItems = new ObservableCollection<NavigationItem>
            {
                new() { Id = "links", Label = "链接", IconKind = PackIconKind.LinkVariant, IsSelected = true },
                new() { Id = "search", Label = "搜索", IconKind = PackIconKind.Magnify },
                new() { Id = "smartlists", Label = "智能列表", IconKind = PackIconKind.AutoFix },
                new() { Id = "ai", Label = "AI 助手", IconKind = PackIconKind.RobotOutline },
                new() { Id = "vault", Label = "密码库", IconKind = PackIconKind.LockOutline },
                new() { Id = "trash", Label = "回收站", IconKind = PackIconKind.DeleteOutline },
                new() { Id = "settings", Label = "设置", IconKind = PackIconKind.CogOutline }
            };
        }

        private void InitializeSmartListItems()
        {
            SmartListItems = new ObservableCollection<SmartListItem>
            {
                new() { Id = "recently-added", Name = "最近添加", IconKind = PackIconKind.ClockPlus, Description = "最近7天添加的链接" },
                new() { Id = "recently-visited", Name = "最近访问", IconKind = PackIconKind.History, Description = "最近7天访问过的链接" },
                new() { Id = "most-visited", Name = "最常访问", IconKind = PackIconKind.Eye, Description = "按访问次数排序" },
                new() { Id = "important", Name = "重要链接", IconKind = PackIconKind.Star, Description = "标记为重要的链接" }
            };
        }

        private async void SelectNav(string navId)
        {
            CurrentNavId = navId;
            SyncNavSelection(navId);

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
            if (_selectedFolderId < 0)
                throw new InvalidOperationException("添加书签必须先选中一个文件夹");
            _editOpenedFromDetail = false;
            IsEditMode = false;
            _editingLinkId = 0;
            EditLinkUrl = string.Empty;
            EditLinkTitle = string.Empty;
            EditLinkDescription = string.Empty;
            _fetchedFaviconUrl = string.Empty;
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
            _editingLinkId = link.Id;
            EditLinkUrl = link.Url ?? string.Empty;
            EditLinkTitle = link.Title ?? string.Empty;
            EditLinkDescription = link.Description ?? string.Empty;
            _fetchedFaviconUrl = link.FaviconUrl ?? string.Empty;
            OnPropertyChanged(nameof(EditLinkFaviconUrl));
            EditLinkIdDisplay = link.LinkId ?? string.Empty;
            EditLinkUpdatedAtDisplay = link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            EditLinkCreatedAtDisplay = link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            EditLinkHasError = false;
            EditLinkErrorMessage = string.Empty;
            ShowEditPage();
        }

        private void ShowEditPage()
        {
            IsEditPageVisible = true;
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
                    mw.MainView.Visibility = Visibility.Visible;
            }
        }

        private void ShowDetail(LinkItem? link)
        {
            if (link == null) return;
            _viewingLink = link;
            DetailUrl = link.Url ?? string.Empty;
            DetailTitle = link.Title ?? string.Empty;
            DetailDescription = link.Description ?? "（无描述）";
            DetailFaviconUrl = link.FaviconUrl ?? string.Empty;
            DetailLinkIdDisplay = link.LinkId ?? string.Empty;
            DetailUpdatedAtDisplay = link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            DetailCreatedAtDisplay = link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            if (_linkViewModel != null)
                _linkViewModel.ClearSelectionCommand.Execute(null);

            if (Application.Current.MainWindow is MainWindow mw)
            {
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

        private void CancelDetail()
        {
            _viewingLink = null;
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.DetailView.Visibility = Visibility.Collapsed;
                mw.MainView.Visibility = Visibility.Visible;
            }
        }

        private bool CanFetchMetadata()
        {
            return !IsFetchingMetadata && !string.IsNullOrWhiteSpace(EditLinkUrl) && Uri.TryCreate(EditLinkUrl.Trim(), UriKind.Absolute, out _);
        }

        private void ClearFavicon()
        {
            _fetchedFaviconUrl = string.Empty;
            OnPropertyChanged(nameof(EditLinkFaviconUrl));
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
                        OnPropertyChanged(nameof(EditLinkFaviconUrl));
                        Logger.Info($"Favicon: {metadata.FaviconUrl}");
                        updated = true;
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

                if (IsEditMode && _editingLinkId > 0)
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
                    if (_selectedFolderId < 0)
                        throw new InvalidOperationException("添加书签必须先选中一个文件夹");
                    await _linkService.CreateLinkAsync(
                        url: url,
                        title: title,
                        description: description,
                        listId: _selectedFolderId == 0 ? null : _selectedFolderId,
                        rating: 0,
                        isImportant: false,
                        tagIds: null,
                        autoFetchMetadata: false,
                        faviconUrl: _fetchedFaviconUrl
                    );
                    Logger.Info("链接添加成功");
                    if (Application.Current.MainWindow is MainWindow mw2)
                        mw2.ExpandFolder(_selectedFolderId);
                }

                CancelEditLink();

                if (_linkViewModel != null)
                    await _linkViewModel.LoadLinksAsync();
                await RefreshFolderTreeAndUIAsync();

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (_editOpenedFromDetail && _viewingLink != null)
                    {
                        var updatedLink = _linkViewModel?.Links.FirstOrDefault(l => l.Id == _editingLinkId);
                        if (updatedLink != null)
                        {
                            _viewingLink = updatedLink;
                            DetailUrl = updatedLink.Url ?? string.Empty;
                            DetailTitle = updatedLink.Title ?? string.Empty;
                            DetailDescription = updatedLink.Description ?? "（无描述）";
                            DetailFaviconUrl = updatedLink.FaviconUrl ?? string.Empty;
                            DetailLinkIdDisplay = updatedLink.LinkId ?? string.Empty;
                            DetailUpdatedAtDisplay = updatedLink.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                            DetailCreatedAtDisplay = updatedLink.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                            mw.ClearDetailPanel();
                        }
                    }
                    else if (_editingLinkId > 0)
                    {
                        var updatedLink = _linkViewModel?.Links.FirstOrDefault(l => l.Id == _editingLinkId);
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
                EditLinkErrorMessage = $"保存失败: {ex.Message}";
                Logger.Error("保存链接失败", ex);
            }
            finally
            {
                EditLinkIsLoading = false;
            }
        }

        public void SelectFolder(int folderId)
        {
            _selectedFolderId = folderId;
            NotifyActionCommandsChanged();

            Logger.Info($"选中目录: {(folderId == 0 ? "全部书签" : $"文件夹 {folderId}")}");
        }

        public void ClearFolderSelectionVM()
        {
            _selectedFolderId = -1;
            _hasSelectedLink = false;
            NotifyActionCommandsChanged();
        }

        public void NotifyLinkSelected(bool selected)
        {
            _hasSelectedLink = selected;
            NotifyActionCommandsChanged();
        }

        private void NotifyActionCommandsChanged()
        {
            ((RelayCommand)ShowAddLinkCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)CreateFolderCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)DeleteSelectedCommand).NotifyCanExecuteChanged();
        }

        private bool CanDeleteSelected()
        {
            if (_selectedFolderId > 0) return true;
            if (_hasSelectedLink) return true;
            if (_linkViewModel != null && _linkViewModel.HasSelectedItems) return true;
            return false;
        }

        private async Task DeleteSelectedAsync()
        {
            try
            {
                if (_selectedFolderId > 0)
                {
                    var folderName = FindFolderNameById(FolderItems, _selectedFolderId);
                    if (!ShowDeleteFolderConfirmation(folderName))
                        return;

                    await _folderService.SoftDeleteFolderAsync(_selectedFolderId);
                    Logger.Info($"文件夹 {_selectedFolderId} 已移至回收站");
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ClearFolderSelection();
                    await RefreshFolderTreeAndUIAsync();
                    return;
                }

                if (_hasSelectedLink && Application.Current.MainWindow is MainWindow mw2)
                {
                    var selectedLink = mw2.GetSelectedLink();
                    if (selectedLink != null)
                    {
                        await _linkService.DeleteLinkAsync(selectedLink.Id);
                        Logger.Info($"已将书签 {selectedLink.Id} 移至回收站");
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

        private async Task CreateFolderAsync()
        {
            if (_selectedFolderId < 0)
                throw new InvalidOperationException("新建文件夹必须先选中一个文件夹");

            var dialog = new Window
            {
                Title = "新建文件夹",
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
                Text = "新建文件夹", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16)
            });

            var dialogNameBox = new System.Windows.Controls.TextBox
            {
                FontSize = 14, Padding = new Thickness(8, 6, 8, 6)
            };
            dialogNameBox.SetValue(HintAssist.HintProperty, "文件夹名称");
            contentPanel.Children.Add(dialogNameBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
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
                Content = "创建", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 0, 238)),
                Foreground = System.Windows.Media.Brushes.White
            };
            okBtn.SetValue(ButtonAssist.CornerRadiusProperty, new CornerRadius(4));
            okBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(dialogNameBox.Text))
                    dialog.DialogResult = true;
            };
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
            dialogNameBox.Focus();

            dialog.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    dialog.DialogResult = false;
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    if (!string.IsNullOrWhiteSpace(dialogNameBox.Text))
                    {
                        dialog.DialogResult = true;
                        e.Handled = true;
                    }
                }
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    int? parentId = _selectedFolderId == 0 ? null : _selectedFolderId;
                    await _folderService.CreateFolderAsync(dialogNameBox.Text.Trim(), parentId: parentId);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ExpandFolder(_selectedFolderId);
                    await RefreshFolderTreeAndUIAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error("创建文件夹失败", ex);
                }
            }
        }

        public async Task PasteLinksToFolderAsync(List<LinkItem> items, bool isCut)
        {
            if (items == null || items.Count == 0) return;
            if (_selectedFolderId < 0) return;

            try
            {
                int? listId = _selectedFolderId == 0 ? null : _selectedFolderId;

                if (isCut)
                {
                    foreach (var item in items)
                    {
                        await _linkService.UpdateLinkAsync(item.Id, listId: listId);
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
                            rating: item.Rating,
                            isImportant: item.IsImportant,
                            tagIds: null,
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
        }

        public async Task LoadTrashTreeAsync()
        {
            var deletedFolders = await _folderService.GetDeletedFoldersAsync();
            var deletedLinks = await _linkService.GetDeletedLinksAsync();

            var trashRoot = new FolderNode { Id = -1, Name = "回收站", IconKind = PackIconKind.Delete };

            var lookup = new Dictionary<int, FolderNode>();
            foreach (var f in deletedFolders)
            {
                lookup[f.Id] = new FolderNode
                {
                    Id = f.Id, Name = f.Name,
                    IconKind = PackIconKind.Folder,
                    Children = new ObservableCollection<FolderNode>()
                };
            }

            foreach (var f in deletedFolders)
            {
                if (f.ParentId.HasValue && lookup.TryGetValue(f.ParentId.Value, out var parentNode))
                    parentNode.Children.Add(lookup[f.Id]);
                else
                    trashRoot.Children.Add(lookup[f.Id]);
            }

            foreach (var link in deletedLinks)
            {
                if (link.ListId.HasValue && lookup.TryGetValue(link.ListId.Value, out var folderNode))
                {
                    folderNode.LinkCount++;
                }
            }

            TrashItems = new ObservableCollection<FolderNode> { trashRoot };
        }

        public async Task<List<Data.Link>> GetTrashLinksForFolderAsync(int folderId)
        {
            return await _linkService.GetDeletedLinksForFolderAsync(folderId);
        }

        public async Task<List<Data.Link>> GetTrashRootLinksAsync()
        {
            return await _linkService.GetDeletedLinksForFolderAsync(null);
        }

        public async Task<List<Data.Link>> GetAllLinksAsync()
        {
            return await _linkService.GetAllActiveLinksAsync();
        }

        public async Task<(List<Data.Link> Links, int TotalCount, int CurrentPage, int LastPage)> GetLinksForSidebarAsync(int? listId = null)
        {
            return await _linkService.GetLinksAsync(
                listId: listId, isDeleted: false,
                sortBy: "created_at", sortOrder: "desc",
                page: 1, perPage: 50
            );
        }

        public async Task<List<Data.Link>> GetRootLevelLinksAsync()
        {
            return await _linkService.GetRootLevelLinksAsync();
        }

        public async Task LoadFolderTreeAsync()
        {
            try
            {
                var totalLinks = await _linkService.GetTotalCountAsync();
                var allFolders = await _folderService.GetAllFoldersAsync();

                var folderNodes = new ObservableCollection<FolderNode>();

                var rootNode = new FolderNode
                {
                    Id = 0, Name = "全部书签", LinkCount = totalLinks,
                    IconKind = PackIconKind.BookmarkOutline,
                    Children = new ObservableCollection<FolderNode>()
                };
                folderNodes.Add(rootNode);

                var lookup = new Dictionary<int, FolderNode>();
                foreach (var folder in allFolders)
                {
                    var node = new FolderNode
                    {
                        Id = folder.Id, Name = folder.Name, LinkCount = folder.LinkCount,
                        ParentId = folder.ParentId,
                        IconKind = folder.LinkCount > 0 ? PackIconKind.Folder : PackIconKind.FolderOutline,
                        Children = new ObservableCollection<FolderNode>()
                    };
                    lookup[folder.Id] = node;
                }

                foreach (var folder in allFolders)
                {
                    if (folder.ParentId.HasValue && lookup.TryGetValue(folder.ParentId.Value, out var parentNode))
                        parentNode.Children.Add(lookup[folder.Id]);
                    else
                        rootNode.Children.Add(lookup[folder.Id]);
                }

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

        private string FindFolderNameById(ObservableCollection<FolderNode> nodes, int folderId)
        {
            foreach (var node in nodes)
            {
                if (node.Id == folderId)
                    return node.Name;
                if (node.Children != null && node.Children.Count > 0)
                {
                    var name = FindFolderNameById(node.Children, folderId);
                    if (name != null)
                        return name;
                }
            }
            return "未命名文件夹";
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
    }
}

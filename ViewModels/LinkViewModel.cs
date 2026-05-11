using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class LinkViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private readonly FolderService _folderService;
        
        private bool _isLoading;
        private bool _hasError;
        private string? _errorMessage;
        private string _noDataMessage = "暂无链接";
        private ObservableCollection<LinkItem> _links = new();
        private Models.LinkQueryParams _currentQuery = new() { PerPage = 200 };

        // 撤销栈：保存修改前的数据
        private readonly Stack<LinkItem> _undoStack = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public LinkViewModel(LinkService linkService, FolderService folderService)
        {
            _linkService = linkService;
            _folderService = folderService;

            RefreshCommand = new RelayCommand(async () => await LoadLinksAsync());
            UndoCommand = new RelayCommand(Undo, CanUndoExecute);
            CopyUrlCommand = new RelayCommand<int>(CopyUrlToClipboard);
            ToggleSelectCommand = new RelayCommand<LinkItem>(ToggleSelect);
            DeleteSelectedCommand = new RelayCommand(async () => await DeleteSelectedAsync(), CanDeleteSelected);
            ClearSelectionCommand = new RelayCommand(ClearSelection);
        }

        public bool HasSelectedItems => Links.Any(l => l.IsSelected);

        public List<LinkItem> SelectedItems => Links.Where(l => l.IsSelected).ToList();

        private LinkItem? _selectedItem;
        public LinkItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _selectedItem = value;
                OnPropertyChanged();
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand CopyUrlCommand { get; }
        public ICommand ToggleSelectCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        public event EventHandler? LinksChanged;
        public event EventHandler? SelectionChanged;

        public ObservableCollection<LinkItem> Links
        {
            get => _links;
            set
            {
                _links = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasData));
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        public bool HasError
        {
            get => _hasError;
            set
            {
                _hasError = value;
                OnPropertyChanged();
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                _errorMessage = value;
                OnPropertyChanged();
            }
        }

        public string NoDataMessage
        {
            get => _noDataMessage;
            set
            {
                _noDataMessage = value;
                OnPropertyChanged();
            }
        }

        public bool HasData => Links.Count > 0;

        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// 加载链接列表（使用本地服务，无需HTTP请求）
        /// </summary>
        public async Task LoadLinksAsync(Models.LinkQueryParams? queryParams = null)
        {
            try
            {
                IsLoading = true;
                HasError = false;
                ErrorMessage = null;

                Logger.Info($"开始加载链接列表...");

                if (queryParams != null)
                {
                    _currentQuery = queryParams;
                }

                // 直接调用本地LinkService，不再通过HTTP API
                var (links, totalCount, currentPage, lastPage) = await _linkService.GetLinksAsync(
                    search: _currentQuery.Search,
                    listId: _currentQuery.ListId,
                    tagId: _currentQuery.TagId,
                    isImportant: _currentQuery.IsImportant,
                    minRating: _currentQuery.MinRating,
                    maxRating: _currentQuery.MaxRating,
                    isDeleted: _currentQuery.IsDeleted,
                    dateFrom: _currentQuery.DateFrom,
                    dateTo: _currentQuery.DateTo,
                    sortBy: _currentQuery.SortBy,
                    sortOrder: _currentQuery.SortOrder,
                    page: _currentQuery.Page,
                    perPage: _currentQuery.PerPage
                );

                Logger.Info($"查询完成，获取到 {links.Count} 条链接（总计 {totalCount} 条）");

                // 将数据实体转换为UI模型
                var linkItems = links.Select(ConvertToLinkItem).ToList();

                Links = new ObservableCollection<LinkItem>(linkItems);
                
                Logger.Info($"Links集合已更新，当前数量: {Links.Count}, HasData: {HasData}");

                if (linkItems.Count == 0)
                {
                    NoDataMessage = "暂无链接数据";
                    Logger.Info("显示空状态提示");
                }
                else
                {
                    Logger.Info($"显示链接列表，第一条: {linkItems[0].Title ?? linkItems[0].Url}");
                }
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                NoDataMessage = "加载失败，请重试";
                
                Logger.Error("加载链接失败", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 创建新链接（使用本地服务）
        /// </summary>
        public async Task<LinkItem?> CreateLinkAsync(string url, string? title = null, 
            string? description = null, int? listId = null, List<int>? tagIds = null)
        {
            try
            {
                var link = await _linkService.CreateLinkAsync(
                    url: url,
                    title: title,
                    description: description,
                    listId: listId,
                    tagIds: tagIds
                );

                return ConvertToLinkItem(link);
            }
            catch (Exception ex)
            {
                Logger.Error("创建链接失败", ex);
                MessageBox.Show($"创建链接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// 更新链接（使用本地服务）
        /// </summary>
        public async Task<bool> UpdateLinkAsync(int id, string? url = null, string? title = null, 
            string? description = null, int? listId = null, List<int>? tagIds = null, 
            int? starRating = null, bool? isImportant = null)
        {
            try
            {
                await _linkService.UpdateLinkAsync(id, url, title, description, listId, tagIds, starRating, isImportant);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("更新链接失败", ex);
                MessageBox.Show($"更新链接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// 删除链接（软删除到回收站）
        /// </summary>
        public async Task DeleteLinkAsync(int id)
        {
            try
            {
                await _linkService.DeleteLinkAsync(id);
                await LoadLinksAsync(); // 刷新列表
            }
            catch (Exception ex)
            {
                Logger.Error("删除链接失败", ex);
                MessageBox.Show($"删除链接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 记录访问（使用本地服务）
        /// </summary>
        public async Task RecordVisitAsync(int id)
        {
            try
            {
                await _linkService.RecordVisitAsync(id);
            }
            catch (Exception ex)
            {
                Logger.Error("记录访问失败", ex);
            }
        }

        /// <summary>
        /// 编辑前保存当前状态（用于撤销）
        /// </summary>
        public void SaveForUndo(LinkItem link)
        {
            // 深拷贝当前状态到撤销栈
            var snapshot = new LinkItem
            {
                Id = link.Id,
                Url = link.Url,
                Title = link.Title,
                OriginalTitle = link.OriginalTitle,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                ListId = link.ListId,
                LastVisitedAt = link.LastVisitedAt,
                VisitCount = link.VisitCount,
                Rating = link.Rating,
                IsImportant = link.IsImportant,
                IsDeleted = link.IsDeleted,
                CreatedAt = link.CreatedAt,
                UpdatedAt = link.UpdatedAt,
                Tags = link.Tags?.ToList() ?? new List<TagItem>(),
                Notes = link.Notes?.ToList() ?? new List<NoteItem>()
            };
            
            _undoStack.Push(snapshot);
            OnPropertyChanged(nameof(CanUndo));
        }

        /// <summary>
        /// 撤销修改（Ctrl+Z）
        /// </summary>
        private void Undo()
        {
            if (_undoStack.Count == 0) return;

            var previousState = _undoStack.Pop();
            
            // 在列表中找到对应的链接并恢复
            var currentLink = Links.FirstOrDefault(l => l.Id == previousState.Id);
            if (currentLink != null)
            {
                currentLink.Url = previousState.Url;
                currentLink.Title = previousState.Title;
                currentLink.OriginalTitle = previousState.OriginalTitle;
                currentLink.Description = previousState.Description;
                currentLink.FaviconUrl = previousState.FaviconUrl;
                currentLink.ListId = previousState.ListId;
                currentLink.LastVisitedAt = previousState.LastVisitedAt;
                currentLink.VisitCount = previousState.VisitCount;
                currentLink.Rating = previousState.Rating;
                currentLink.IsImportant = previousState.IsImportant;
                currentLink.Tags = previousState.Tags;
                currentLink.Notes = previousState.Notes;
            }
            
            OnPropertyChanged(nameof(CanUndo));
        }

        private bool CanUndoExecute() => _undoStack.Count > 0;

        /// <summary>
        /// 复制URL到剪贴板
        /// </summary>
        private void CopyUrlToClipboard(int linkId)
        {
            var link = Links.FirstOrDefault(l => l.Id == linkId);
            if (link == null) return;

            try
            {
                Clipboard.SetText(link.Url);
                System.Diagnostics.Debug.WriteLine($"URL已复制到剪贴板: {link.Url}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"复制失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换单个链接的选中状态（单选模式：点击新卡片自动取消旧选择）
        /// </summary>
        private void ToggleSelect(LinkItem? link)
        {
            if (link == null) return;

            if (link.IsSelected)
            {
                // 点击已选中的卡片 → 取消选中
                link.IsSelected = false;
            }
            else
            {
                // 点击未选中的卡片 → 先取消所有其他卡片的选择
                foreach (var item in Links.Where(l => l.IsSelected && l.Id != link.Id))
                {
                    item.IsSelected = false;
                }
                // 选中当前卡片
                link.IsSelected = true;
            }

            OnPropertyChanged(nameof(HasSelectedItems));
            OnPropertyChanged(nameof(SelectedItems));
            SelectedItem = link.IsSelected ? link : Links.FirstOrDefault(l => l.IsSelected);
            Logger.Info($"链接 {link.Id} 选中状态: {link.IsSelected}");
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 批量删除选中的链接（直接删除，无确认弹窗）
        /// </summary>
        private async Task DeleteSelectedAsync()
        {
            var selectedLinks = Links.Where(l => l.IsSelected).ToList();
            if (selectedLinks.Count == 0) return;

            try
            {
                foreach (var link in selectedLinks)
                {
                    await _linkService.DeleteLinkAsync(link.Id);
                }

                Logger.Info($"成功删除 {selectedLinks.Count} 个链接");
                await LoadLinksAsync();
                LinksChanged?.Invoke(this, EventArgs.Empty);

                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ClearDetailPanel();
            }
            catch (Exception ex)
            {
                Logger.Error("批量删除失败", ex);
                MessageBox.Show($"批量删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 是否可以执行删除操作（有选中项时才可执行）
        /// </summary>
        private bool CanDeleteSelected() => HasSelectedItems;

        /// <summary>
        /// 清除所有选中状态
        /// </summary>
        private void ClearSelection()
        {
            foreach (var link in Links.Where(l => l.IsSelected))
            {
                link.IsSelected = false;
            }
            OnPropertyChanged(nameof(HasSelectedItems));
            OnPropertyChanged(nameof(SelectedItems));
            SelectedItem = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 将数据实体转换为UI模型
        /// </summary>
        private LinkItem ConvertToLinkItem(Data.Link link)
        {
            return new LinkItem
            {
                Id = link.Id,
                LinkId = link.LinkId,
                Url = link.Url,
                Title = link.Title ?? "",
                OriginalTitle = link.OriginalTitle ?? "",
                Description = link.Description ?? "",
                FaviconUrl = link.FaviconUrl ?? "",
                ListId = link.ListId,
                LastVisitedAt = link.LastVisitedAt,
                VisitCount = link.VisitCount,
                Rating = link.Rating,
                IsImportant = link.IsImportant,
                IsDeleted = link.IsDeleted,
                DeletedAt = link.DeletedAt,
                CreatedAt = link.CreatedAt,
                UpdatedAt = link.UpdatedAt,
                Tags = link.Tags.Select(t => new TagItem
                {
                    Id = t.Id,
                    Name = t.Name,
                    Color = t.Color ?? "#1976D2",
                    Description = t.Description
                }).ToList(),
                Notes = link.Notes.Select(n => new NoteItem
                {
                    Id = n.Id,
                    Title = n.Title,
                    Content = n.Content ?? ""
                }).ToList()
            };
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

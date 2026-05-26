using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.ViewModels
{
    public class SearchViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private string _searchQuery = string.Empty;
        private bool _isLoading;
        private bool _hasError;
        private string? _errorMessage;
        private ObservableCollection<LinkItem> _searchResults = new();
        private int _currentPage = 1;
        private int _totalResults = 0;
        
        // 搜索历史相关
        private ObservableCollection<SearchHistoryItem> _searchHistory = new();
        private bool _showHistory = true; 

        public event PropertyChangedEventHandler? PropertyChanged;

        public SearchViewModel(LinkService linkService)
        {
            _linkService = linkService;
            SearchCommand = new RelayCommand(async () => await ExecuteSearchAsync());
            ClearCommand = new RelayCommand(ClearSearch);
            LoadMoreCommand = new RelayCommand(async () => await LoadMoreAsync(), CanLoadMore);
            ClearHistoryCommand = new RelayCommand(ClearHistoryLocal);
            
            _ = LoadSearchHistoryAsync();
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (_searchQuery != value)
                {
                    _searchQuery = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool HasError
        {
            get => _hasError;
            set { _hasError = value; OnPropertyChanged(); }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public ObservableCollection<LinkItem> SearchResults
        {
            get => _searchResults;
            set { _searchResults = value; OnPropertyChanged(); }
        }

        public int CurrentPage
        {
            get => _currentPage;
            set { _currentPage = value; OnPropertyChanged(); }
        }

        public int TotalResults
        {
            get => _totalResults;
            set { _totalResults = value; OnPropertyChanged(); }
        }

        public bool HasData => _searchResults.Count > 0;
        
        public bool ShowHistory
        {
            get => _showHistory && _searchHistory.Count > 0;
            set { _showHistory = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SearchHistoryItem> SearchHistory
        {
            get => _searchHistory;
            set { _searchHistory = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowHistory)); }
        }

        public ICommand SearchCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LoadMoreCommand { get; }
        public ICommand ClearHistoryCommand { get; }

        private async Task ExecuteSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(_searchQuery)) return;

            try
            {
                IsLoading = true;
                HasError = false;
                ErrorMessage = null;
                CurrentPage = 1;

                var (results, totalCount, currentPage, lastPage) = await _linkService.SearchAsync(
                    query: _searchQuery,
                    page: 1,
                    perPage: 20
                );

                TotalResults = totalCount;
                
                var linkItems = results.Select(ConvertToLinkItem).ToList();
                SearchResults = new ObservableCollection<LinkItem>(linkItems);

                // 保存搜索历史
                await SaveSearchHistoryAsync(_searchQuery, linkItems.Count);

                ShowHistory = false;
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Logger.Error("搜索失败", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ClearSearch()
        {
            SearchQuery = string.Empty;
            SearchResults.Clear();
            TotalResults = 0;
            CurrentPage = 1;
            HasError = false;
            ErrorMessage = null;
            ShowHistory = true;
        }

        private async Task LoadMoreAsync()
        {
            if (!CanLoadMore()) return;

            try
            {
                IsLoading = true;
                CurrentPage++;

                var (results, _, _, _) = await _linkService.SearchAsync(
                    query: _searchQuery,
                    page: CurrentPage,
                    perPage: 20
                );

                foreach (var item in results.Select(ConvertToLinkItem))
                {
                    SearchResults.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载更多失败", ex);
                CurrentPage--;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private bool CanLoadMore()
        {
            return !IsLoading && SearchResults.Count < TotalResults && !string.IsNullOrEmpty(_searchQuery);
        }

        private async Task LoadSearchHistoryAsync()
        {
            try
            {
                using var db = new LinkPocketDbContext();
                var history = await db.SearchHistories
                    .OrderByDescending(h => h.SearchedAt)
                    .Take(20)
                    .ToListAsync();

                SearchHistory = new ObservableCollection<SearchHistoryItem>(
                    history.Select(h => new SearchHistoryItem
                    {
                        Id = h.Id,
                        Query = h.Query,
                        ResultsCount = h.ResultsCount,
                        SearchedAt = h.SearchedAt
                    })
                );
            }
            catch (Exception ex)
            {
                Logger.Error("加载搜索历史失败", ex);
            }
        }

        private async Task SaveSearchHistoryAsync(string query, int count)
        {
            try
            {
                using var db = new LinkPocketDbContext();
                var history = new Data.SearchHistory
                {
                    Query = query,
                    ResultsCount = count,
                    SearchedAt = DateTime.UtcNow
                };

                db.SearchHistories.Add(history);
                await db.SaveChangesAsync();

                // 刷新历史列表
                await LoadSearchHistoryAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("保存搜索历史失败", ex);
            }
        }

        private void ClearHistoryLocal()
        {
            try
            {
                using var db = new LinkPocketDbContext();
                db.SearchHistories.RemoveRange(db.SearchHistories);
                db.SaveChanges();

                SearchHistory.Clear();
                ShowHistory = false;
            }
            catch (Exception ex)
            {
                Logger.Error("清空搜索历史失败", ex);
            }
        }

        private LinkItem ConvertToLinkItem(Data.Link link) => new()
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
            IsImportant = link.IsImportant,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

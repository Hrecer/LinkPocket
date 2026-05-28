using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class SmartListResultViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private readonly string _listId;
        private bool _isLoading;
        private bool _hasData;
        private ObservableCollection<LinkItem> _items = new();
        private string _title = "智能列表";
        private string _emptyMessage = "暂无数据";
        private int _totalCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ListId => _listId;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool HasData
        {
            get => _hasData;
            set { _hasData = value; OnPropertyChanged(); }
        }

        public ObservableCollection<LinkItem> Items
        {
            get => _items;
            set { _items = value; OnPropertyChanged(); }
        }

        public string EmptyMessage
        {
            get => _emptyMessage;
            set { _emptyMessage = value; OnPropertyChanged(); }
        }

        public int TotalCount
        {
            get => _totalCount;
            set { _totalCount = value; OnPropertyChanged(); }
        }

        public SmartListResultViewModel(LinkService linkService, string listId, string title)
        {
            _linkService = linkService;
            _listId = listId;
            Title = title;
        }

        public async Task LoadAsync()
        {
            IsLoading = true;
            HasData = false;
            Items.Clear();

            try
            {
                List<Data.Link> links = _listId switch
                {
                    "recently_added" => await _linkService.GetRecentlyAddedAsync(7, 100),
                    "recently_visited" => await _linkService.GetRecentlyVisitedAsync(7, 100),
                    "recently_edited" => await _linkService.GetRecentlyEditedAsync(7, 100),
                    "most_visited" => await _linkService.GetMostVisitedAsync(20),
                    _ => new List<Data.Link>()
                };

                TotalCount = links.Count;

                if (links.Count == 0)
                {
                    EmptyMessage = _listId switch
                    {
                        "recently_added" => "最近 7 天没有添加新书签",
                        "recently_visited" => "最近 7 天没有访问过书签",
                        "recently_edited" => "最近 7 天没有编辑过书签",
                        "most_visited" => "暂无访问记录",
                        _ => "暂无数据"
                    };
                    return;
                }

                var items = links.Select(ConvertToLinkItem).ToList();
                foreach (var item in items) Items.Add(item);
                HasData = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static LinkItem ConvertToLinkItem(Data.Link link) => new()
        {
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

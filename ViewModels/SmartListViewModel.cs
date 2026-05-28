using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class SmartListCardItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string IconKind { get; set; } = "StarOutline";
        public string Color { get; set; } = "#6200EE";
    }

    public class SmartListViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private bool _isLoading;
        private ObservableCollection<SmartListCardItem> _cards = new();
        private SmartListResultViewModel? _resultViewModel;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SmartListViewModel(LinkService linkService)
        {
            _linkService = linkService;
            InitializeCards();
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SmartListCardItem> Cards
        {
            get => _cards;
            set { _cards = value; OnPropertyChanged(); }
        }

        public SmartListResultViewModel? ResultViewModel
        {
            get => _resultViewModel;
            set { _resultViewModel = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowResult)); }
        }

        public bool ShowResult => _resultViewModel != null;

        private void InitializeCards()
        {
            Cards = new ObservableCollection<SmartListCardItem>
            {
                new() { Id = "recently_added", Title = "最近添加", Subtitle = "近 7 天新增的书签", IconKind = "PlusCircleOutline", Color = "#4CAF50" },
                new() { Id = "recently_visited", Title = "最近查看", Subtitle = "近 7 天访问过的书签", IconKind = "History", Color = "#2196F3" },
                new() { Id = "recently_edited", Title = "最近编辑", Subtitle = "近 7 天修改过的书签", IconKind = "PencilOutline", Color = "#FF9800" },
                new() { Id = "most_visited", Title = "最常查看", Subtitle = "访问次数前 20 的书签", IconKind = "TrendingUp", Color = "#E91E63" }
            };
        }

        public async void OpenSmartList(string listId)
        {
            IsLoading = true;
            try
            {
                var resultVm = new SmartListResultViewModel(_linkService, listId, GetTitleById(listId));
                await resultVm.LoadAsync();
                ResultViewModel = resultVm;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void GoBack()
        {
            ResultViewModel = null;
        }

        private static string GetTitleById(string id) => id switch
        {
            "recently_added" => "最近添加",
            "recently_visited" => "最近查看",
            "recently_edited" => "最近编辑",
            "most_visited" => "最常查看",
            _ => "智能列表"
        };

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

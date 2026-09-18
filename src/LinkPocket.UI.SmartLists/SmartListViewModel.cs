using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Api;
using LinkPocket.Contracts;
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
        public string Color { get; set; } = "Primary";
    }

    public class SmartListViewModel : INotifyPropertyChanged
    {
        private readonly EngineClient _api;
        private readonly Services.UiPortProvider _ports;
        private readonly Func<string?, string> _resolveFolderPath;
        private bool _isLoading;
        private ObservableCollection<SmartListCardItem> _cards = new();
        private SmartListResultViewModel? _resultViewModel;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>「位置」列与详情栏共用的路径解析（组合根注入，与浏览页同一份目录树）；视图单元格工厂也经此取值。</summary>
        public Func<string?, string> ResolveFolderPath { get; }

        /// <summary>
        /// ports = UI 端口槽位（组合根持有，MainWindow 构造时登记）；结果页动作命令
        /// 在打开列表时从槽位取用（此时端口必已登记）。路径解析器 = 「位置」列与
        /// 详情栏共用的目录树路径解析（MainViewModel 注入，与浏览页同一份树）。
        /// </summary>
        public SmartListViewModel(EngineClient api, Services.UiPortProvider ports, Func<string?, string> resolveFolderPath)
        {
            _api = api;
            _ports = ports;
            _resolveFolderPath = resolveFolderPath;
            ResolveFolderPath = resolveFolderPath;
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

        /// <summary>单一数据源：四个智能列表的语义定义（入口卡片副标题与结果页灰色提示共用）。</summary>
        private static (string Id, string Title, string Subtitle, string Icon) Definition(string id) => id switch
        {
            "recently_added" => ("recently_added", "最近添加", "近 7 天新增的书签", "plus-circle-outline"),
            "recently_visited" => ("recently_visited", "最近查看", "近 7 天访问过的书签", "history"),
            "recently_edited" => ("recently_edited", "最近编辑", "近 7 天修改过的书签", "pencil-outline"),
            "most_visited" => ("most_visited", "最常查看", "访问次数前 20 的书签", "trending-up"),
            _ => (id, "智能列表", "自动汇集的动态集合", "bookmark-outline"),
        };

        private void InitializeCards()
        {
            Cards = new ObservableCollection<SmartListCardItem>
            {
                new() { Id = "recently_added", Title = "最近添加", Subtitle = "近 7 天新增的书签", IconKind = "plus-circle-outline", Color = "Success" },
                new() { Id = "recently_visited", Title = "最近查看", Subtitle = "近 7 天访问过的书签", IconKind = "history", Color = "Primary" },
                new() { Id = "recently_edited", Title = "最近编辑", Subtitle = "近 7 天修改过的书签", IconKind = "pencil-outline", Color = "Warning" },
                new() { Id = "most_visited", Title = "最常查看", Subtitle = "访问次数前 20 的书签", IconKind = "trending-up", Color = "Tertiary" }
            };
        }

        public async void OpenSmartList(string listId)
        {
            IsLoading = true;
            try
            {
                var def = Definition(listId);
                var resultVm = new SmartListResultViewModel(_api, listId, def.Title,
                    _ports.Navigation, _ports.Dialogs, _resolveFolderPath)
                {
                    Subtitle = def.Subtitle,
                };
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

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        /// <summary>M3 字形名（小写连字符，见 LpIcons/M3 字形表）。原默认 "StarOutline"（PascalCase）不是已注册字形。</summary>
        public string IconKind { get; set; } = "bookmark-outline";
        public string Color { get; set; } = "Primary";
    }

    public class SmartListViewModel : INotifyPropertyChanged
    {
        private readonly EngineClient _api;
        private readonly Services.UiPortProvider _ports;
        private readonly Func<string?, string> _resolveFolderPath;
        private bool _isLoading;
        private int _openGeneration;   // 打开代次：GoBack / 重新打开时递增，使在途结果失效
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

        /// <summary>单一数据源：四张入口卡片（副标题/结果页灰色提示共用）；Definition 反查语义。</summary>
        private static readonly (string Id, string Title, string Subtitle, string Icon, string Color)[] CardDefs =
        [
            ("recently_added", "最近添加", "近 7 天新增的书签", "plus-circle-outline", "Success"),
            ("recently_visited", "最近查看", "近 7 天访问过的书签", "history", "Primary"),
            ("recently_edited", "最近编辑", "近 7 天修改过的书签", "pencil-outline", "Warning"),
            ("most_visited", "最常查看", "访问次数前 20 的书签", "trending-up", "Tertiary"),
        ];

        private static (string Id, string Title, string Subtitle, string Icon) Definition(string id)
        {
            foreach (var d in CardDefs)
                if (d.Id == id) return (d.Id, d.Title, d.Subtitle, d.Icon);
            return (id, "智能列表", "自动汇集的动态集合", "bookmark-outline");
        }

        private void InitializeCards()
        {
            var cards = new ObservableCollection<SmartListCardItem>();
            foreach (var d in CardDefs)
                cards.Add(new SmartListCardItem { Id = d.Id, Title = d.Title, Subtitle = d.Subtitle, IconKind = d.Icon, Color = d.Color });
            Cards = cards;
        }

        public async void OpenSmartList(string listId)
        {
            IsLoading = true;
            var generation = ++_openGeneration;
            try
            {
                var def = Definition(listId);
                var resultVm = new SmartListResultViewModel(_api, listId, def.Title,
                    _ports.Navigation, _ports.Dialogs, _resolveFolderPath)
                {
                    Subtitle = def.Subtitle,
                };
                await resultVm.LoadAsync();
                // 代次校验：期间用户已返回卡片页或打开了别的列表 → 晚到结果不覆盖
                if (generation != _openGeneration) return;
                ResultViewModel = resultVm;
            }
            catch (Exception ex)
            {
                Services.Logger.Error("智能列表加载失败", ex);
                if (generation == _openGeneration) ResultViewModel = null;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void GoBack()
        {
            _openGeneration++;   // 使在途加载结果失效
            ResultViewModel = null;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

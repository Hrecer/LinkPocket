using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels
{
    public class SmartListCardItem
    {
        public string Id { get; set; } = string.Empty;
        public LocValue Title { get; set; }
        public LocValue Subtitle { get; set; }
        /// <summary>M3 字形名（小写连字符，见 LpIcons/M3 字形表）。原默认 "StarOutline"（PascalCase）不是已注册字形。</summary>
        public string IconKind { get; set; } = "bookmark-outline";
        public string Color { get; set; } = "Primary";
    }

    public class SmartListViewModel : INotifyPropertyChanged
    {
        private readonly EngineClient _api;
        private readonly Services.UiPortProvider _ports;
        private readonly IContentLocator? _locator;
        private readonly Func<string?, LocValue> _resolveFolderPath;
        private bool _isLoading;
        private int _openGeneration;   // 打开代次：GoBack / 重新打开时递增，使在途结果失效
        private ObservableCollection<SmartListCardItem> _cards = new();
        private SmartListResultViewModel? _resultViewModel;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>「位置」列与详情栏共用的路径解析（组合根注入，与浏览页同一份目录树）；视图单元格工厂也经此取值。</summary>
        public Func<string?, LocValue> ResolveFolderPath { get; }

        /// <summary>
        /// ports = UI 端口槽位（组合根持有，MainWindow 构造时登记）；结果页动作命令
        /// 在打开列表时从槽位取用（此时端口必已登记）。locator = 定位组件（结果页「跳转」用：
        /// 进目录 + 选中行）。路径解析器 = 「位置」列与详情栏共用的目录树路径解析
        /// （MainViewModel 注入，与浏览页同一份树）。
        /// </summary>
        public SmartListViewModel(EngineClient api, Services.UiPortProvider ports,
            Func<string?, LocValue> resolveFolderPath, IContentLocator? locator = null)
        {
            _api = api;
            _ports = ports;
            _locator = locator;
            _resolveFolderPath = resolveFolderPath;
            ResolveFolderPath = resolveFolderPath;
            GoBackCommand = new RelayCommand(GoBack, () => ResultViewModel != null);
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
            set { _resultViewModel = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowResult)); CommandRefresh.Request(); }
        }

        public bool ShowResult => _resultViewModel != null;

        /// <summary>单一数据源：四张入口卡片（副标题/结果页灰色提示共用）；Definition 反查语义。</summary>
        private static readonly (string Id, LocValue Title, LocValue Subtitle, string Icon, string Color)[] CardDefs =
        [
            ("recently_added", Loc.K("smartlists.preset.added"), Loc.K("smartlists.preset.addedDesc"), "plus-circle-outline", "Success"),
            ("recently_visited", Loc.K("smartlists.preset.visited"), Loc.K("smartlists.preset.visitedDesc"), "history", "Primary"),
            ("recently_edited", Loc.K("smartlists.preset.edited"), Loc.K("smartlists.preset.editedDesc"), "pencil-outline", "Warning"),
            ("most_visited", Loc.K("smartlists.preset.mostVisited"), Loc.K("smartlists.preset.mostVisitedDesc"), "trending-up", "Tertiary"),
        ];

        private static (string Id, LocValue Title, LocValue Subtitle, string Icon) Definition(string id)
        {
            foreach (var d in CardDefs)
                if (d.Id == id) return (d.Id, d.Title, d.Subtitle, d.Icon);
            return (id, Loc.K("smartlists.title"), Loc.K("smartlists.subtitle"), "bookmark-outline");
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
                    _ports.Navigation, _ports.Dialogs, _resolveFolderPath, _locator)
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
                LpLog.Error("smart list load failed", ex);
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

        /// <summary>Esc 分层：结果页有选中 → 先清选中；否则返回卡片列表。
        /// 键位在 ShortcutCatalog（`smartlists.back`）；返回按钮与本命令同源。</summary>
        public void EscapeOrBack()
        {
            if (ResultViewModel is { HasSelection: true } result)
            {
                result.ClearSelection();
                return;
            }
            GoBack();
        }

        /// <summary>Esc 返回卡片列表（键位在 ShortcutCatalog；返回按钮与本命令同源）。</summary>
        public ICommand GoBackCommand { get; }

        /// <summary>
        /// 事件防抖刷新入口：跨页数据变更后，让当前打开的结果列表重拉——
        /// 与页面内删除后的重载同一条路径（LoadAsync → Reloaded → 视图重绑）。
        /// 未打开列表（ResultViewModel null）不做事；持有期间代次递增使在途结果失效。
        /// </summary>
        public async Task RefreshCurrentAsync()
        {
            var result = ResultViewModel;
            if (result == null || IsLoading) return;
            _openGeneration++;   // 在途 OpenSmartList 结果作废（若与本次刷新竞争）
            IsLoading = true;
            try
            {
                await result.LoadAsync();
                result.NotifyReloaded();   // 视图重绑（页面已订阅该实例）
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list refresh failed", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

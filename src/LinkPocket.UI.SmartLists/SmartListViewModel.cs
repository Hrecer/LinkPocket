using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.I18n;
using LinkPocket.Views;

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
        private readonly Func<string?, int>? _resolveFolderOrder;
        private bool _isLoading;
        private int _openGeneration;   // 打开代次：GoBack / 重新打开时递增，使在途结果失效
        private SmartListResultViewModel? _loadingResult;   // 首页仍在取的结果页（GoBack 要能取消它）
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
            Func<string?, LocValue> resolveFolderPath, IContentLocator? locator = null,
            Func<string?, int>? resolveFolderOrder = null)
        {
            _api = api;
            _ports = ports;
            _locator = locator;
            _resolveFolderPath = resolveFolderPath;
            _resolveFolderOrder = resolveFolderOrder;
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

        /// <summary>
        /// 进入智能列表页时把四个预设的**首页查询**各走热一次（后台）：点卡时不再付
        /// EF 查询编译 / SQLite 连接与页读的冷启动成本（「最常查看」首次查询实测 35–180ms）。
        /// 只取首页，不取全量；失败不影响真实打开。
        /// </summary>
        public void WarmEngine()
        {
            CancelStartupWarm();
            if (Interlocked.Exchange(ref _engineWarmStarted, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await WarmPagesAsync(ensureSpareRows: true, CancellationToken.None);
                }
                catch
                {
                    // 预热失败不影响真实查询（真实打开自己会报错）
                }
            });
        }

        /// <summary>
        /// 启动后空闲预热：只取四个预设首页，**不建 10 万占位行池**
        /// （启动阶段不额外占约 50MB、不触发 GC）；用户点卡时页数据已在缓存/磁盘已热。
        /// 真实进入智能列表页或打开列表会取消本预热。
        /// </summary>
        public async Task WarmForStartupAsync()
        {
            CancelStartupWarm();
            var cts = new CancellationTokenSource();
            _startupWarmCts = cts;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await WarmPagesAsync(ensureSpareRows: false, cts.Token);
                LpLog.Info($"startup warm: smart lists {watch.ElapsedMilliseconds}ms",
                    category: "app.lifecycle");
            }
            catch (OperationCanceledException)
            {
                // 用户已进入智能列表：真实请求优先。
            }
            catch (Exception ex)
            {
                LpLog.Warn("startup warm: smart lists failed", ex, category: "app.lifecycle");
            }
            finally
            {
                if (ReferenceEquals(_startupWarmCts, cts)) _startupWarmCts = null;
                cts.Dispose();
            }
        }

        /// <summary>取消启动预热（页面进入/真实打开时调用；幂等）。</summary>
        public void CancelStartupWarm()
        {
            var cts = Interlocked.Exchange(ref _startupWarmCts, null);
            if (cts is not null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        private CancellationTokenSource? _startupWarmCts;

        private async Task WarmPagesAsync(bool ensureSpareRows, CancellationToken ct)
        {
            var added = await _api.LinkSmartListPageAsync(
                "recently_added", "title", "asc", 1, 64, ct: ct);
            ct.ThrowIfCancellationRequested();
            var edited = await _api.LinkSmartListPageAsync(
                "recently_edited", "title", "asc", 1, 64, ct: ct);
            ct.ThrowIfCancellationRequested();
            var visited = await _api.LinkSmartListPageAsync(
                "recently_visited", "title", "asc", 1, 64, ct: ct);
            ct.ThrowIfCancellationRequested();
            var most = await _api.LinkSmartListPageAsync(
                "most_visited", "visit_count", "desc", 1, 20, ct: ct);
            ct.ThrowIfCancellationRequested();

            var at = DateTime.UtcNow;
            lock (_warmPages)
            {
                _warmPages["recently_added"] = (added, at);
                _warmPages["recently_edited"] = (edited, at);
                _warmPages["recently_visited"] = (visited, at);
                _warmPages["most_visited"] = (most, at);
            }
            // 页面进入时的预热才建占位行池；启动预热刻意跳过。
            if (ensureSpareRows) EnsureSpareRows(added.TotalCount);
        }

        private int _engineWarmStarted;
        private readonly Dictionary<string, (SmartListPageDto Page, DateTime At)> _warmPages = new(StringComparer.Ordinal);
        private static readonly TimeSpan WarmPageTtl = TimeSpan.FromSeconds(10);   // 与引擎查询缓存同一窗口；数据变更会主动清空

        // —— 首开占位行池：后台预建、取用一次；语言版本或规模不符则弃用（正常现场构建） ——
        private readonly object _spareLock = new();
        private List<LinkItem>? _spareRows;
        private int _spareCount;
        private int _spareLocVersion = -1;

        private List<LinkItem>? TryTakeSpareRows(int total)
        {
            lock (_spareLock)
            {
                if (_spareRows == null || _spareLocVersion != Loc.Table.Version) return null;
                // 小结果集现场构建本来就只花 0–1ms，别为它消耗行池（留给下一个十万级列表）。
                if (total < _spareCount / 2) return null;
                if (_spareCount < total) return null;
                var rows = _spareRows;
                _spareRows = null;
                return rows.Count == total ? rows : rows.GetRange(0, total);
            }
        }

        private void EnsureSpareRows(int total)
        {
            if (total <= 0) return;
            var version = Loc.Table.Version;
            lock (_spareLock)
            {
                if (_spareRows != null && _spareCount == total && _spareLocVersion == version) return;
            }
            var text = Loc.T("smartlists.rowLoading");
            var rows = new List<LinkItem>(total);
            for (var i = 0; i < total; i++)
                rows.Add(SmartListResultRow.Placeholder(text));
            lock (_spareLock)
            {
                _spareRows = rows;
                _spareCount = total;
                _spareLocVersion = version;
            }
        }

        /// <summary>数据变更（任意页面/外部进程）后预热页立即作废，绝不把过期快照当首屏。</summary>
        public void InvalidateWarmPages()
        {
            lock (_warmPages) _warmPages.Clear();
        }

        /// <summary>刚预热的首页（TTL 内、与默认排序同口径）直接交付，点卡不再等一次引擎往返；
        /// 过期则丢弃，真实打开自己查询（引擎侧本身也有事件失效的查询缓存）。</summary>
        private SmartListPageDto? TakeWarmPage(string kind)
        {
            lock (_warmPages)
            {
                if (!_warmPages.TryGetValue(kind, out var entry)) return null;
                _warmPages.Remove(kind);
                return DateTime.UtcNow - entry.At <= WarmPageTtl ? entry.Page : null;
            }
        }

        public async void OpenSmartList(string listId, int? days = null)
        {
            CancelStartupWarm();   // 真实打开优先，启动预热让路
            IsLoading = true;
            var generation = ++_openGeneration;
            _loadingResult?.CancelLoad();
            ResultViewModel?.CancelLoad();
            SmartListResultViewModel? resultVm = null;
            try
            {
                var def = Definition(listId);
                resultVm = new SmartListResultViewModel(_api, listId, def.Title,
                    _ports.Navigation, _ports.Dialogs, _resolveFolderPath, _locator,
                    _ports.BrowseEdit, _ports.Notices, _resolveFolderOrder)
                {
                    Subtitle = def.Subtitle,
                };
                _loadingResult = resultVm;
                resultVm.SpareRowsProvider = TryTakeSpareRows;
                await resultVm.LoadAsync(days, days == null ? TakeWarmPage(listId) : null);
                // 代次校验：期间用户已返回卡片页或打开了别的列表 → 晚到结果不覆盖
                if (generation != _openGeneration)
                {
                    resultVm.CancelLoad();
                    return;
                }
                ResultViewModel = resultVm;
                // 补页开始后（约 0.5s）在后台把下一次开卡要用的占位行池补回来。
                var total = resultVm.TotalCount;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(900);
                        EnsureSpareRows(total);
                    }
                    catch { /* 行池是加速项，失败不影响打开 */ }
                });
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list load failed", ex);
                if (generation == _openGeneration) ResultViewModel = null;
            }
            finally
            {
                if (ReferenceEquals(_loadingResult, resultVm)) _loadingResult = null;
                if (generation == _openGeneration) IsLoading = false;
            }
        }

        public void GoBack()
        {
            _openGeneration++;   // 使在途加载结果失效
            _loadingResult?.CancelLoad();
            ResultViewModel?.CancelLoad();
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

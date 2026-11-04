using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
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
    /// <summary>
    /// 智能列表结果页 ViewModel（MVVM）：数据装载（LoadAsync）之外，
    /// 行选中态、右侧详情栏与页面动作命令（详情/打开网站/删除）也在此——
    /// 视图（Views/SmartListsPage）只负责表格装配与渲染。
    /// 动作语义与搜索页同一套：「详情/编辑」= 进入浏览页链接详情页；
    /// 删除 = 确认后移入回收站并重载当前列表（Reloaded 事件驱动视图重绑）。
    /// 选中由全站共享的 <see cref="ListSelection"/> 承载（本页为**单选中**：结果页 = 只读页，
    /// 可读、可选、不可操作——不引入删除/Ctrl+A 等操作键）。
    ///
    /// 装载口径（十万行）：**首帧 = 真实总数 + 首页真实行**，其余行是同高占位行，
    /// 全量数据在后台取回后按页**原位填充**——不替换集合、不重建行、不移动滚动位置。
    /// 排序仍是内存排序（位置列走目录树前序整数键，不再逐目录拼显示路径）。
    /// </summary>
    public class SmartListResultViewModel : INotifyPropertyChanged
    {
        /// <summary>首帧取多少条真实行（与搜索页同一量级：一屏多）。</summary>
        private const int FirstPageRows = 64;
        /// <summary>后台原位填充的分块大小（单块属性通知有界；块间让出一帧）。</summary>
        private const int FillChunkRows = 5000;
        /// <summary>「最常查看」= 全站唯一带「前 N」口径的智能列表。</summary>
        private const int MostVisitedRows = 20;

        /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
        private readonly EngineClient _client;
        private readonly INavigationService? _navigation;
        private readonly IDialogService? _dialogs;
        private readonly IContentLocator? _locator;
        private readonly Func<string?, LocValue> _resolveFolderPath;
        /// <summary>位置排序键：目录树前序序号（0 = 根级）；组合根注入，缺省恒等（测试/无树时退化为稳定顺序）。</summary>
        private readonly Func<string?, int> _resolveFolderOrder;
        private readonly IBrowseEditState? _editState;
        private readonly IUiNoticeService? _notices;
        private bool _isDeleting;

        /// <summary>删除进行中：命令可用性与防重入共用同一开关（删除过程中禁用再次触发）。</summary>
        public bool IsDeleting
        {
            get => _isDeleting;
            private set
            {
                if (_isDeleting == value) return;
                _isDeleting = value;
                OnPropertyChanged();
                CommandRefresh.Request();   // 立即重估（选中变化 → 命令可用性同步更新）
            }
        }

        private readonly string _listId;
        private bool _isLoading;
        private bool _hasData;
        private ObservableCollection<LinkItem> _items = new();
        private LocValue _title = Loc.K("smartlists.title");
        private LocValue _emptyMessage = Loc.K("smartlists.empty");
        private int _totalCount;
        private string _sortField = "title";
        private bool _sortAscending = true;

        // —— 渐进装载状态（全部只在 UI 线程读写；后台只负责取数） ——
        private int _loadGeneration;
        private CancellationTokenSource? _loadCts;
        private bool _fillComplete = true;
        private int _loadedRows;
        private bool _pendingSort;
        private string _pendingSortField = "title";
        private bool _pendingSortAscending = true;
        private bool _selectLastAfterFill;

        /// <summary>位置列文本缓存（按目录 + 语言版本）：行模板只解析视口内的行。</summary>
        private const int MaxPathDisplayChars = 200;
        private readonly Dictionary<string, (int Version, string Text)> _pathTextCache = new(StringComparer.Ordinal);

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>删除重载完成后通知视图重绑表格（排序复位 + 行集替换 + 清表格选中）。</summary>
        public event EventHandler? Reloaded;

        /// <summary>后台补全结束时若有排队的排序：集合已重排，视图据此事重绑（保留滚动位置）。</summary>
        public event EventHandler? ItemsReordered;

        /// <summary>宿主（SmartListViewModel.RefreshCurrentAsync 等）在跨页事件刷新后调用，
        /// 触发视图重绑——与删除后重载同一出口（事件只能在声明类内触发，故提供公开方法）。</summary>
        public void NotifyReloaded() => Reloaded?.Invoke(this, EventArgs.Empty);

        public string ListId => _listId;

        /// <summary>结果页标题（键；语言一变自己重算）。</summary>
        public LocValue Title
        {
            get => _title;
            set { if (!_title.Equals(value)) { _title = value; OnPropertyChanged(); } }
        }

        private LocValue _subtitle;
        /// <summary>列表语义的灰色提示（结果页标题右侧展示，与入口卡片副标题同一数据源）。</summary>
        public LocValue Subtitle
        {
            get => _subtitle;
            set { if (!_subtitle.Equals(value)) { _subtitle = value; OnPropertyChanged(); } }
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

        /// <summary>后台全量补全是否结束（含「无需补全」的小结果集）。UiBench/探针等它的最终态。</summary>
        public bool IsFillComplete
        {
            get => _fillComplete;
            private set
            {
                if (_fillComplete == value) return;
                _fillComplete = value;
                OnPropertyChanged();
            }
        }

        /// <summary>已填入真实数据的行数（占位行不计；补全后等于 <see cref="TotalCount"/>）。</summary>
        public int LoadedRows
        {
            get => _loadedRows;
            private set
            {
                if (_loadedRows == value) return;
                _loadedRows = value;
                OnPropertyChanged();
            }
        }

        /// <summary>空态文案（键 + 参数；语言一变自己重算）。</summary>
        public LocValue EmptyMessage
        {
            get => _emptyMessage;
            set { if (!_emptyMessage.Equals(value)) { _emptyMessage = value; OnPropertyChanged(); } }
        }

        public int TotalCount
        {
            get => _totalCount;
            set
            {
                if (_totalCount == value) return;
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCountText));
            }
        }

        /// <summary>
        /// 「共 N 项」整句（含变量的句子由模型出文本，语序随语言走）。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>交出去的是文案值（键 + 参数），不是取好词的字符串</b>：返回 <c>string</c> 会把这句话
        /// <b>冻结在读取那一刻的语言</b>上（绑定只认"属性值变了没有"，而普通属性不发通知），
        /// 换语言后屏幕上留着上一种语言的计数句。界面侧一律写 <c>{loc:Value TotalCountText}</c>。
        /// </remarks>
        public LocValue TotalCountText => Loc.K("smartlists.count.total", _totalCount);

        // —— 选中态与详情栏（MVVM 自页面下沉；共享 ListSelection 核心，单选中） ——

        /// <summary>**选中核心（全站共享实现）**：结果页为单选中。</summary>
        public ListSelection Selection { get; } = new();

        /// <summary>视图注入：当前结果的**视觉顺序**（共享表格的当前排序）——↑/↓ 据此计算。</summary>
        public Func<IReadOnlyList<string>>? OrderProvider { get; set; }

        /// <summary>把某行滚入视口（移动选中后由视图订阅执行）。</summary>
        public event EventHandler<LinkItem>? FocusRowRequested;

        /// <summary>当前选中行（按结果集顺序取首项；本页恒为单选中）。</summary>
        private LinkItem? PrimarySelected =>
            Items.FirstOrDefault(r => Selection.Contains(r.LinkId));

        /// <summary>是否存在选中（命令 CanExecute 用）。</summary>
        public bool HasSelection => Selection.HasAny;

        /// <summary>右侧详情栏（复用搜索页同一 DetailSidebar 数据契约）。</summary>
        public SearchDetailsViewModel Details { get; } = new();

        public SmartListResultViewModel(EngineClient client, string listId, LocValue title,
            INavigationService? navigation, IDialogService? dialogs, Func<string?, LocValue> resolveFolderPath,
            IContentLocator? locator = null, IBrowseEditState? editState = null, IUiNoticeService? notices = null,
            Func<string?, int>? resolveFolderOrder = null)
        {
            _client = client;
            _listId = listId;
            Title = title;
            _navigation = navigation;
            _dialogs = dialogs;
            _resolveFolderPath = resolveFolderPath;
            _resolveFolderOrder = resolveFolderOrder ?? (_ => int.MaxValue);
            _locator = locator;
            _editState = editState;
            _notices = notices;

            Selection.Changed += OnSelectionChanged;

            // 「详情」= 打开浏览页的链接详情页（Enter 同此命令）
            OpenInBrowserCommand = new RelayCommand(
                () => { if (PrimarySelected is { } item) _navigation?.OpenLinkInBrowser(item.LinkId); },
                () => Selection.HasAny && !IsDeleting);
            // 「跳转」= 进浏览页对应目录并选中该行（经定位组件；与「详情」互不替代）。
            // 本页为单选中，故"有选中"即"有唯一目标"。
            JumpCommand = new RelayCommand(() => _ = JumpAsync(), () => Selection.HasAny && !IsDeleting);
            OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => Selection.HasAny && !IsDeleting);
            // 删除过程中（IsDeleting）禁用删除与其余行动作 —— 防重入不再只靠方法内 if
            DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => Selection.HasAny && !IsDeleting);
            // 只读页的键位延伸（↑/↓/End/F5；不引入任何会改数据的键）
            MoveSelectionCommand = new RelayCommand<object?>(p => MoveSelection(ParseDirection(p)));
            SelectLastCommand = new RelayCommand(SelectLast);
            // F5 = 重新查询（**导航加载口径**）：亮加载遮罩、首页落地后视图重绑，剩余行后台补齐
            RefreshCommand = new RelayCommand(() => _ = ReloadAsync(navigating: true));
            // 点空白清选中（BlankClick 挂在结果区；与 Esc 分层共用同一"出口"语义）
            ClearSelectionCommand = new RelayCommand(() => Selection.Clear());

            Details.OpenCommand = OpenInBrowserCommand;
            Details.RenameCommand = OpenInBrowserCommand;
            Details.OpenWebsiteCommand = OpenWebsiteCommand;
            Details.DeleteCommand = DeleteCommand;
            Details.JumpCommand = JumpCommand;   // 右栏「跳转」图标钮 = 同一条定位入口（绝不另写一份）
            // 只读结果页的右栏动作面收窄：**不显示「编辑」与「删除」两个按钮**
            // ——结果页是只读的查看面（键位集也是只读集，无 Delete/Ctrl+A）。命令仍接好（如需恢复显示位即可用）。
            Details.HideEditAndDeleteActions();
        }

        public ICommand OpenInBrowserCommand { get; }
        /// <summary>「跳转」= 进浏览页对应目录并选中该行（定位组件；与「详情」互不替代）。</summary>
        public ICommand JumpCommand { get; }
        public ICommand OpenWebsiteCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand MoveSelectionCommand { get; }
        public ICommand SelectLastCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        /// <summary>视图在用户点击行时调用（单选中：只读页只有"读 + 选"；占位行拒绝）。</summary>
        public void ClickItem(LinkItem item)
        {
            if (item is SmartListResultRow { IsPlaceholder: true }) return;
            _selectLastAfterFill = false;   // 用户在补全中自己点了行：不再兑现之前的 End 意图
            Selection.SelectSingle(item.LinkId);
        }

        /// <summary>清空选中与详情栏（返回卡片页/重载列表时由视图或本类调用）。</summary>
        public void ClearSelection()
        {
            _selectLastAfterFill = false;
            Selection.Clear();
        }

        /// <summary>取消在途取数/补全（返回卡片页、打开另一个列表、页面卸载时调用）。</summary>
        public void CancelLoad() => _loadCts?.Cancel();

        /// <summary>选中的**唯一投影点**：集合 → 属性通知 + 右栏（恒为单选详情态）。</summary>
        private void OnSelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            var item = PrimarySelected;
            Details.UpdateFrom(item, item == null ? LocValue.Empty : _resolveFolderPath(item.ListId));
            ProjectSelection();
            CommandRefresh.Request();
        }

        /// <summary>
        /// 把选中的那一行投影到行模型（模板模式的选中底色由 <see cref="LinkItem.IsSelected"/> 驱动）。
        /// 本页是**单选中**：只维护"当前选中行 + 上一个选中行"，未选中时 O(1)——十万行首帧不为此扫全表。
        /// </summary>
        public void ProjectSelection()
        {
            LinkItem? selected = null;
            if (Selection.HasAny)
            {
                foreach (var item in _items)
                {
                    if (Selection.Contains(item.LinkId)) { selected = item; break; }
                }
            }

            if (ReferenceEquals(_projectedSelection, selected)) return;
            if (_projectedSelection is { IsSelected: true }) _projectedSelection.IsSelected = false;
            if (selected is { IsSelected: false }) selected.IsSelected = true;
            _projectedSelection = selected;
        }

        private LinkItem? _projectedSelection;

        /// <summary>位置列文本（目录 + 语言版本缓存，限长 200 字；只服务显示，不参与排序）。</summary>
        public string ResolveFolderPathText(string? listId)
        {
            var key = listId ?? string.Empty;
            var version = Loc.Table.Version;
            if (_pathTextCache.TryGetValue(key, out var hit) && hit.Version == version)
                return hit.Text;

            var text = _resolveFolderPath(string.IsNullOrEmpty(listId) ? null : listId).Resolve();
            if (text.Length > MaxPathDisplayChars)
                text = string.Concat(text.AsSpan(0, MaxPathDisplayChars), "…");
            _pathTextCache[key] = (version, text);
            return text;
        }

        /// <summary>
        /// 外部排序（模板模式）：按列头字段重排当前结果集并**替换集合实例**，由视图重绑。
        /// 十万行是内存排序；位置列用目录树前序整数键（不逐目录拼 200 字显示路径）。
        /// 后台补全尚未结束时只记下排队排序，补全完成后统一应用（行数不完整时排序没有正确结果）。
        /// 返回 false = 视图无需立即重绑（排序未变化，或已排队到补全完成）。
        /// </summary>
        public bool Sort(string field, bool ascending)
        {
            var normalized = string.IsNullOrEmpty(field) ? "title" : field;
            if (string.Equals(normalized, _sortField, StringComparison.Ordinal) && ascending == _sortAscending)
                return false;

            _sortField = normalized;
            _sortAscending = ascending;
            if (!_fillComplete)
            {
                _pendingSort = true;
                _pendingSortField = normalized;
                _pendingSortAscending = ascending;
                return false;
            }

            ApplySortNow(normalized, ascending);
            return true;
        }

        private void ApplySortNow(string field, bool ascending)
        {
            if (_items.Count > 1)
                Items = NewCollection(SortRows(_items, field, ascending));
            ProjectSelection();
        }

        /// <summary>一次性集合（构造即 Reset，无逐条通知）：BulkObservableCollection 没有带初值的构造函数。</summary>
        private static BulkObservableCollection<LinkItem> NewCollection(IEnumerable<LinkItem> rows)
        {
            var collection = new BulkObservableCollection<LinkItem>();
            collection.ReplaceAll(rows);
            return collection;
        }

        private List<LinkItem> SortRows(IEnumerable<LinkItem> items, string field, bool ascending)
        {
            // 稳定排序 + 强类型键：名称用当前区域比较（与旧口径一致），其余用整数/日期键。
            return field switch
            {
                "path" => ascending
                    ? items.OrderBy(i => _resolveFolderOrder(i.ListId)).ToList()
                    : items.OrderByDescending(i => _resolveFolderOrder(i.ListId)).ToList(),
                "updated_at" => ascending
                    ? items.OrderBy(i => i.UpdatedAt).ToList()
                    : items.OrderByDescending(i => i.UpdatedAt).ToList(),
                "last_visited_at" => ascending
                    ? items.OrderBy(i => i.LastVisitedAt is null)
                        .ThenBy(i => i.LastVisitedAt ?? DateTime.MinValue).ToList()
                    : items.OrderBy(i => i.LastVisitedAt is null)
                        .ThenByDescending(i => i.LastVisitedAt ?? DateTime.MinValue).ToList(),
                "visit_count" => ascending
                    ? items.OrderBy(i => i.VisitCount).ToList()
                    : items.OrderByDescending(i => i.VisitCount).ToList(),
                "created_at" => ascending
                    ? items.OrderBy(i => i.CreatedAt).ToList()
                    : items.OrderByDescending(i => i.CreatedAt).ToList(),
                _ => ascending
                    ? items.OrderBy(TitleKey, StringComparer.CurrentCulture).ToList()
                    : items.OrderByDescending(TitleKey, StringComparer.CurrentCulture).ToList(),
            };
        }

        private static string TitleKey(LinkItem item)
            => string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;

        // —— ↑/↓ / End（与浏览页主栏同一套共享语义；顺序由视图注入） ——

        private static int ParseDirection(object? p)
        {
            var s = (p as string is string str ? str : p?.ToString()) ?? string.Empty;
            return s.Contains("up") ? -1 : s.Contains("down") ? 1 : 0;
        }

        private void MoveSelection(int delta)
        {
            var target = Selection.Move(delta, OrderProvider?.Invoke() ?? Array.Empty<string>());
            FocusOn(target);
        }

        private void SelectLast()
        {
            // 补全未完成时末行还是占位行：记下意图，等全量落地后再选中 + 滚入视口。
            if (!_fillComplete)
            {
                _selectLastAfterFill = true;
                return;
            }
            SelectLastCore();
        }

        private void SelectLastCore()
        {
            var target = Selection.SelectLast(OrderProvider?.Invoke() ?? Array.Empty<string>());
            FocusOn(target);
        }

        private void FocusOn(string? linkId)
        {
            if (linkId == null) return;
            var item = Items.FirstOrDefault(r => r.LinkId == linkId);
            if (item != null) FocusRowRequested?.Invoke(this, item);
        }

        /// <summary>
        /// 装载当前列表的**首页**（真实总数 + 首页真实行 + 同高占位行），返回即上屏；
        /// 全量数据随后在后台取回并原位填充（<see cref="FillRemainingAsync"/>）。
        /// <c>days</c> = 时间窗覆盖（**null = 命令缺省 7 天**，即用户口径）；
        /// 探针等自动化用大窗口拿到足够样本，才能真正核对"渲染行序 = 排序"。
        /// </summary>
        public async Task LoadAsync(int? days = null)
        {
            IsLoading = true;
            CancelLoad();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            var gen = ++_loadGeneration;
            var fillStarted = false;
            try
            {
                var isMostVisited = _listId == "most_visited";
                // 「最常查看」= 先按访问次数取前 20，再由界面按当前列头排序（默认名称升序）；
                // 其余三个 = 时间窗筛选，命中多少显示多少（无条数上限）。
                var engineSortField = isMostVisited ? "visit_count" : EngineSortField();
                var engineSortAsc = isMostVisited ? false : EngineSortAscending();
                var perPage = isMostVisited ? MostVisitedRows : FirstPageRows;
                var phase = Stopwatch.StartNew();

                SmartListPageDto first;
                try
                {
                    first = await FetchPageAsync(engineSortField, engineSortAsc, page: 1, perPage, days, cts.Token);
                }
                catch (OperationCanceledException) { return; }

                if (gen != _loadGeneration) return;
                var engineMs = phase.ElapsedMilliseconds;
                phase.Restart();

                TotalCount = isMostVisited ? first.Items.Count : first.TotalCount;
                _pathTextCache.Clear();   // 目录改名/移动后重载必须重算路径

                if (TotalCount == 0)
                {
                    EmptyMessage = _listId switch
                    {
                        "recently_added" => Loc.K("smartlists.empty.added"),
                        "recently_visited" => Loc.K("smartlists.empty.visited"),
                        "recently_edited" => Loc.K("smartlists.empty.edited"),
                        "most_visited" => Loc.K("smartlists.empty.mostVisited"),
                        _ => Loc.K("smartlists.empty")
                    };
                    Items = new BulkObservableCollection<LinkItem>();
                    LoadedRows = 0;
                    IsFillComplete = true;
                    HasData = false;
                    return;
                }

                var loadingText = Loc.T("smartlists.rowLoading");
                var total = TotalCount;
                var prefetch = first.Items;
                var rows = await Task.Run(() => BuildInitialRows(total, prefetch, loadingText), cts.Token);
                if (gen != _loadGeneration) return;
                var buildMs = phase.ElapsedMilliseconds;
                phase.Restart();

                if (isMostVisited)
                {
                    // 前 20 已按访问次数选出；展示顺序 = 当前列头（默认名称升序）。
                    rows = SortRows(rows, _sortField, _sortAscending);
                }
                else if (_sortField == "path")
                {
                    // 引擎没有"目录树位置"排序：首页先按名称上屏，补全完成后统一按位置重排。
                    _pendingSort = true;
                    _pendingSortField = "path";
                    _pendingSortAscending = _sortAscending;
                }

                Items = NewCollection(rows);
                LoadedRows = isMostVisited ? rows.Count : Math.Min(total, prefetch.Count);
                IsFillComplete = isMostVisited || total <= prefetch.Count;
                ProjectSelection();
                HasData = true;
                LpLog.Info(
                    $"first page kind={_listId} engine={engineMs}ms build={buildMs}ms publish={phase.ElapsedMilliseconds}ms"
                    + $" rows={rows.Count} filled={LoadedRows}",
                    category: "app.smartlist");

                if (!IsFillComplete)
                {
                    fillStarted = true;
                    _ = FillRemainingAsync(gen, cts, days);
                }
            }
            catch (OperationCanceledException)
            {
                // 被新一次打开/刷新/返回取代：不发布任何半成品状态。
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list load failed", ex);
                if (gen == _loadGeneration) IsFillComplete = true;
            }
            finally
            {
                // 后台补全持有本轮 token，由它负责释放；没有补全才在本方法收尾。
                if (!fillStarted) DisposeLoadToken(cts);
                if (gen == _loadGeneration) IsLoading = false;
            }
        }

        private string EngineSortField() => _sortField == "path" ? "title" : _sortField;
        private bool EngineSortAscending() => _sortField == "path" ? true : _sortAscending;

        private async Task<SmartListPageDto> FetchPageAsync(
            string sortField, bool ascending, int page, int perPage, int? days, CancellationToken ct)
            => await Task.Run(() => _client.LinkSmartListPageAsync(
                _listId,
                sortBy: sortField,
                sortOrder: ascending ? "asc" : "desc",
                page: page,
                perPage: perPage,
                days: days,
                ct: ct), ct);

        /// <summary>首屏行集（后台线程构建）：占位行 + 首页真实行；十万行也不在 UI 线程分配。</summary>
        private static List<LinkItem> BuildInitialRows(int total, IReadOnlyList<LinkDto> prefetch, string loadingText)
        {
            var rows = new List<LinkItem>(total);
            for (var i = 0; i < total; i++)
                rows.Add(SmartListResultRow.Placeholder(loadingText));

            var filled = Math.Min(total, prefetch.Count);
            for (var i = 0; i < filled; i++)
                ((SmartListResultRow)rows[i]).Fill(prefetch[i]);
            return rows;
        }

        /// <summary>
        /// 后台补齐剩余行：一次取回全量（同一排序）→ 分块**原位填充**占位行。
        /// 块间让出一帧，最大单次 UI 停摆只跟分块大小走，不跟结果总数走。
        /// </summary>
        private async Task FillRemainingAsync(int gen, CancellationTokenSource cts, int? days)
        {
            var ct = cts.Token;
            try
            {
                // 先让首帧真正渲染：补页不抢首屏。
                await Task.Delay(120, ct);

                SmartListPageDto all = null!;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        all = await FetchPageAsync(EngineSortField(), EngineSortAscending(), 1, 0, days, ct);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (attempt < 2)
                    {
                        LpLog.Warn("smart list full fetch failed, retrying", ex, category: "smartlist");
                        await Task.Delay(100, ct);
                    }
                }

                if (gen != _loadGeneration) return;
                var rows = all.Items;

                // 计数之后被并发删空：以全量结果为准回到空态（不把十万占位行留在屏幕上）。
                if (rows.Count == 0)
                {
                    Items = new BulkObservableCollection<LinkItem>();
                    TotalCount = 0;
                    LoadedRows = 0;
                    IsFillComplete = true;
                    HasData = false;
                    return;
                }

                // 计数与全量取数之间若发生并发写，以全量结果为准调整占位行数。
                if (rows.Count != _items.Count)
                    ResizeRows(rows.Count);

                TotalCount = rows.Count;
                for (var start = 0; start < rows.Count; start += FillChunkRows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (gen != _loadGeneration) return;

                    var end = Math.Min(start + FillChunkRows, rows.Count);
                    ApplyRows(rows, start, end);
                    LoadedRows = end;
                    if (end < rows.Count) await Task.Delay(1, ct);
                }

                if (gen != _loadGeneration) return;

                // 选中行可能在补全中才落地 / 数据有更新：统一重投影一次。
                // 补全前无法判断选中 ID 是否还在后续页里，所以这里才是"剔除已消失项"的最终点。
                if (Selection.HasAny)
                {
                    var changed = Selection.RemoveMissing(id => _items.Any(i => i.LinkId == id));
                    if (!changed) OnSelectionChanged();
                }
                else
                {
                    ProjectSelection();
                }

                if (_pendingSort)
                {
                    _pendingSort = false;
                    ApplySortNow(_pendingSortField, _pendingSortAscending);
                    ItemsReordered?.Invoke(this, EventArgs.Empty);
                }
                if (_selectLastAfterFill)
                {
                    _selectLastAfterFill = false;
                    SelectLastCore();
                }

                // 终态最后发布：观察方（界面/量具）看到 true 时排序与选中都已经是最终态。
                IsFillComplete = true;
            }
            catch (OperationCanceledException)
            {
                // 新一次加载接管；不要发布任何终态。
            }
            catch (Exception ex)
            {
                if (gen == _loadGeneration)
                {
                    LpLog.Error("smart list background fill failed", ex);
                    IsFillComplete = true;   // 允许 F5 重试，别把界面永久卡在"正在加载…"
                }
            }
            finally
            {
                DisposeLoadToken(cts);
            }
        }

        /// <summary>把一页 DTO 原位写进对应行（含首屏真实行的覆盖写）；只填占位行或重排后的同一行。</summary>
        private void ApplyRows(IReadOnlyList<LinkDto> rows, int start, int end)
        {
            for (var i = start; i < end && i < _items.Count; i++)
            {
                if (_items[i] is not SmartListResultRow row) continue;
                row.Fill(rows[i]);
                if (Selection.Contains(row.LinkId)) row.IsSelected = true;
            }
        }

        /// <summary>全量结果与首屏计数不一致（并发写）时调整尾部占位行；正常路径不触发。</summary>
        private void ResizeRows(int count)
        {
            if (_items is not BulkObservableCollection<LinkItem> bulk) return;
            if (count < bulk.Count)
            {
                var trimmed = new List<LinkItem>(count);
                for (var i = 0; i < count; i++) trimmed.Add(bulk[i]);
                Items = NewCollection(trimmed);
                ItemsReordered?.Invoke(this, EventArgs.Empty);
                return;
            }

            var loadingText = Loc.T("smartlists.rowLoading");
            var extra = new List<LinkItem>(count - bulk.Count);
            for (var i = bulk.Count; i < count; i++)
                extra.Add(SmartListResultRow.Placeholder(loadingText));
            bulk.AddRange(extra);
        }

        private void DisposeLoadToken(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }

        /// <summary>
        /// 重新查询：重拉当前列表并通知视图重绑（与删除后重载同一条出口）。
        /// <paramref name="navigating"/> = **用户发起**（F5）：亮加载遮罩、首页落地后重绑；
        /// 删除后的重载、跨页事件刷新等一律 false（静默）——与浏览页的导航加载口径一致。
        /// 选中保留由视图重绑时统一处理（只剔除已不在结果里的 ID）。
        /// </summary>
        private async Task ReloadAsync(bool navigating = false)
        {
            if (IsLoading) return;
            if (navigating) IsNavigating = true;
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list re-query failed", ex);
            }
            finally
            {
                if (navigating)
                {
                    IsNavigating = false;
                    RefreshCompleted?.Invoke(this, EventArgs.Empty);
                }
            }
            Reloaded?.Invoke(this, EventArgs.Empty);
        }

        private bool _isNavigating;

        /// <summary>
        /// 用户发起的重新查询在途（界面**加载遮罩的唯一来源**）：本页只有 F5 为 true；
        /// 删除后的重载、入口对齐刷新一律 false（不亮遮罩、不播动画）——见 BEHAVIOR-CONTRACT §1.5。
        /// </summary>
        public bool IsNavigating
        {
            get => _isNavigating;
            private set { if (_isNavigating == value) return; _isNavigating = value; OnPropertyChanged(); }
        }

        /// <summary>一次**用户发起**的重新查询（F5）结束：视图据此播行入场动画并收回焦点。</summary>
        public event EventHandler? RefreshCompleted;

        /// <summary>
        /// **「跳转」**：进浏览页对应目录并选中该行（定位组件 <see cref="IContentLocator"/>；
        /// **不是**打开详情页——后者是 <see cref="OpenInBrowserCommand"/>）。本页为单选中，故"有选中"即"有唯一目标"。
        /// 失败（目标刚被移走 / 无界面宿主 / 组件不可用）一律如实提示，绝不静默。
        /// </summary>
        private async Task JumpAsync()
        {
            var item = PrimarySelected;
            if (item == null) return;
            if (_locator == null)
            {
                LpLog.Error("jump failed: the locator component is unavailable", null);   // 观测面：失败留痕
                return;
            }

            var result = await _locator.LocateLinkAsync(item.LinkId);
            if (result.IsSuccess || result.UserNotified) return;   // 阻断类提示已由共享 NoticeBar 给出

            _dialogs?.Alert(Loc.T("common.jump"), (result.Message ?? result.Status switch
            {
                LocateStatus.NotFound => Loc.K("locate.notFoundLink"),
                LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
                LocateStatus.Failed => Loc.K("locate.failedRetry"),
                _ => Loc.K("locate.incomplete"),
            }).Resolve());
        }

        /// <summary>「打开网站」：默认浏览器打开并记录一次访问（与搜索页侧栏同口径）。</summary>
        private async Task OpenSelectedWebsiteAsync()
        {
            var item = PrimarySelected;
            if (item == null) return;
            Services.LinkLauncher.Open(item.Url);
            try
            {
                await _client.LinkVisitRecordAsync(item.LinkId);
                // 统计行原位刷新（代次校验：选中未变才写回）；记账已落库 → 本地同步 VisitCount 再渲染
                if (Selection.Count == 1 && Selection.Contains(item.LinkId))
                {
                    item.VisitCount++;
                    Details.UpdateFrom(item, _resolveFolderPath(item.ListId));
                }
            }
            catch { /* 记账失败不打断 */ }
        }

        /// <summary>删除 = 确认后移入回收站（可恢复），随后重载当前列表刷新结果。</summary>
        private async Task DeleteSelectedAsync()
        {
            if (IsDeleting) return;   // 命令可用性已拦，这里双保险（键盘路径等不经 CanExecute 时）
            var item = PrimarySelected;
            if (item == null) return;

            // 编辑优先：正在改名的/整页编辑的那一条不能被跨页删除；提示走共享 NoticeBar。
            if (_editState?.IsEditingEntity(item.LinkId) == true)
            {
                _notices?.Show(Loc.T("browser.edit.blockedByEditing"));
                return;
            }

            var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
            if (_dialogs == null || !_dialogs.Confirm(Loc.T("common.title.deleteLink"), Loc.T("browser.confirm.linkToTrash", name))) return;

            IsDeleting = true;
            try
            {
                await _client.LinkTrashAsync(item.LinkId);
                ClearSelection();

                // 重载当前列表（结果集直接从 API 重拉，杜绝本地残留）
                await LoadAsync();
                Reloaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list link deletion failed", ex);
            }
            finally
            {
                IsDeleting = false;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

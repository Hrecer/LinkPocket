using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.I18n;
using LinkPocket.Views;

namespace LinkPocket.ViewModels;

/// <summary>搜索页空态描述：视图据此渲染 MD3E 空态徽章（图标 + 标题 + 副文案 + 色调）。</summary>
public sealed record SearchEmptyState(string IconKind, LocValue Title, LocValue? Subtitle,
    string ContainerBrush, string OnContainerBrush);

/// <summary>
/// 搜索页 ViewModel（MVVM）：查询执行、范围守卫与立即刷新、选中态（**完整多选**）、
/// 页面动作命令（搜索/取消/跳转/打开网站/删除所选/移动选中/全选/清选中）全部在此；
/// 视图（Views/SearchView）只负责表格装配、单元格与空态渲染、关键词高亮、把"当前视觉顺序"注入本类。
/// 数据口径：结果 = 后端 search 协议（默认排序 名称升序，与主栏一致）；
/// 「位置」路径由组合根注入的解析器解析（与浏览页/智能列表同一目录树）。
/// 选中由全站共享的 <see cref="ListSelection"/> 承载（与浏览页/回收站/智能列表/去重明细同一实现）。
/// </summary>
public sealed partial class SearchViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _api;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly IContentLocator? _locator;
    private readonly Func<string?, LocValue> _resolveFolderPath;
    /// <summary>位置排序键：目录树前序序号（0 = 根级）；与智能列表同一份注入。</summary>
    private readonly Func<string?, int> _resolveFolderOrder;
    private readonly IBrowseEditState? _editState;
    private readonly IUiNoticeService? _notices;

    public SearchViewModel(EngineClient api, INavigationService navigation, IDialogService dialogs,
        Func<string?, LocValue> resolveFolderPath, IContentLocator? locator = null,
        IBrowseEditState? editState = null, IUiNoticeService? notices = null,
        Func<string?, int>? resolveFolderOrder = null)
    {
        _api = api;
        _navigation = navigation;
        _dialogs = dialogs;
        _resolveFolderPath = resolveFolderPath;
        _resolveFolderOrder = resolveFolderOrder ?? (_ => int.MaxValue);
        _locator = locator;
        _editState = editState;
        _notices = notices;

        Selection.Changed += OnSelectionChanged;

        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        CancelCommand = new RelayCommand(Cancel);
        // **「跳转」= 进浏览页对应目录并选中该行**（经定位组件 IContentLocator，与 ID 跳转工具同一套语义）；
        // **仅单选可用**：多选时没有"某一个目标"。绝不展开详情页。
        JumpCommand = new RelayCommand(() => _ = JumpAsync(), () => SelectionCount == 1);
        // **「详情」= 打开浏览页的链接详情页**（INavigationService）——与「跳转」是两条互不替代的语义：
        // 顶部跳转药丸 / 右栏跳转钮走前者，`Enter` / 双击 / 右栏「详情」/ 铅笔槽走后者。
        OpenDetailCommand = new RelayCommand(
            () =>
            {
                if (PrimarySelected is not { } item) return;
                // 文件夹行 = 进入该目录（与浏览页双击文件夹同一语义）；链接行 = 详情页（不变）。
                if (item.IsFolder) _navigation.OpenFolderInBrowser(item.LinkId);
                else _navigation.OpenLinkInBrowser(item.LinkId);
            },
            () => SelectionCount > 0);
        OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => SelectionCount == 1);
        DeleteSelectionCommand = new RelayCommand(() => _ = DeleteSelectionAsync(), () => SelectionCount > 0 && !IsDeleting);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            _selectAllAfterLoad = false;
            _selectLastAfterLoad = false;
            Selection.Clear();
        });
        MoveSelectionCommand = new RelayCommand<object?>(p => MoveSelection(ParseDirection(p)));
        SelectLastCommand = new RelayCommand(SelectLast);
        EscapeCommand = new RelayCommand(Escape);
        // F5 = 真刷新（**导航加载口径**）：重跑当前已执行查询并保留选中（只剔除已消失的项），
        // 亮加载遮罩、结束后播行入场动画；事件驱动的静默刷新走 RefreshFromEventAsync（另一条口径，不亮不播）。
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !string.IsNullOrWhiteSpace(LastQuery));

        // 详情栏的页面动作命令：「详情」/铅笔槽 = 打开浏览页的链接详情页（自带完整编辑与删除入口）；
        // 「跳转」单独挂定位入口（进目录 + 选中行）——两条语义互不替代。
        Details.OpenCommand = OpenDetailCommand;
        Details.RenameCommand = OpenDetailCommand;
        Details.OpenWebsiteCommand = OpenWebsiteCommand;
        Details.DeleteCommand = DeleteSelectionCommand;
        Details.JumpCommand = JumpCommand;
        // 右栏 286 宽放不下「两枚药丸 + 三枚 32 图标钮（跳转 / 编辑 / 删除）」→ 两行排布
        //（与浏览页侧栏 / 回收站右栏同一套 `StackedActions`；按钮定义仍只有一份，不是第二套详情栏）
        Details.UseStackedActions();
    }

    // —— 搜索输入与范围 ——

    private string _query = "";
    /// <summary>搜索框文本（视图 UpdateSourceTrigger=PropertyChanged 双向绑定）。</summary>
    public string Query
    {
        get => _query;
        set { _query = value; OnPropertyChanged(); }
    }

    private bool _searchPath = true;
    public bool SearchPath { get => _searchPath; set { _searchPath = value; OnPropertyChanged(); OnScopeChanged(); } }

    private bool _searchUrl = true;
    public bool SearchUrl { get => _searchUrl; set { _searchUrl = value; OnPropertyChanged(); OnScopeChanged(); } }

    private bool _searchTitle = true;
    public bool SearchTitle { get => _searchTitle; set { _searchTitle = value; OnPropertyChanged(); OnScopeChanged(); } }

    private bool _searchDesc = true;
    public bool SearchDesc { get => _searchDesc; set { _searchDesc = value; OnPropertyChanged(); OnScopeChanged(); } }

    /// <summary>最近一次实际执行的查询（结果行高亮关键词用）。</summary>
    public string LastQuery { get; private set; } = "";

    // —— 结果与选中 ——

    /// <summary>首屏预取行数（视口 × 2，下限 64；不是渲染上限，也不是结果边界）。</summary>
    private const int MinimumPrefetchRows = 64;
    private int _prefetchRows = MinimumPrefetchRows;

    /// <summary>首屏预取行数（视图按实际视口调用；不允许把首屏预取当成结果总数）。</summary>
    public int PrefetchRows
    {
        get => _prefetchRows;
        set => _prefetchRows = Math.Max(MinimumPrefetchRows, value);
    }

    /// <summary>后台补齐的单次取数：引擎单页上限 10 000（见 EngineLimits.MaxPageSize）。</summary>
    private const int RemainingLoadSize = 10_000;

    /// <summary>**全部命中**的连续结果集：行数从第一帧起就等于 <see cref="TotalHitCount"/>；
    /// 未加载的位置是同高占位行，后台按页**原位填充**，不替换集合、不重建行。</summary>
    public BulkObservableCollection<LinkItem> Results { get; } = new();

    /// <summary>本条查询的全部命中数（来自快照会话的 count；从首帧起就是真实总数）。</summary>
    public int TotalHitCount { get; private set; }

    private int _loadedCount;
    /// <summary>已经填入真实数据的行数（占位行不计）；补全后等于 <see cref="TotalHitCount"/>。</summary>
    public int LoadedCount => _loadedCount;

    private bool _isLoadingAll;
    private bool _loadFailed;

    /// <summary>后台正在补齐剩余命中（不是截断：结果集行数始终等于 TotalHitCount）。</summary>
    public bool IsLoadingAll
    {
        get => _isLoadingAll;
        private set
        {
            if (_isLoadingAll == value) return;
            _isLoadingAll = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoadingNotice));
        }
    }

    /// <summary>补全提示（加载完毕即空）。类型必须是 <see cref="LocText"/>（自适应通道只认它）。</summary>
    public LocText LoadingNotice => _loadFailed
        ? LocText.Of(Loc.K("search.result.loadFailed", LoadedCount))
        : IsLoadingAll
            ? (LoadedCount < TotalHitCount
                ? LocText.Of(Loc.K("search.result.loadingAll", LoadedCount, TotalHitCount))
                : LocText.Of(Loc.K("search.result.loading")))
            : LocText.Empty;

    private SearchEmptyState? _emptyState;
    /// <summary>当前空态（加载/引导/无结果/错误），视图监听后渲染。</summary>
    public SearchEmptyState? EmptyState
    {
        get => _emptyState;
        private set { _emptyState = value; OnPropertyChanged(); }
    }

    /// <summary>**选中核心（全站共享实现）**：搜索页为完整多选（Ctrl 翻转 / Shift 区间 / Ctrl+A / ↑↓）。</summary>
    public ListSelection Selection { get; } = new();

    /// <summary>视图注入：当前结果的**视觉顺序**（共享表格的当前排序）——↑/↓ 与 Ctrl+A 据此计算。</summary>
    public Func<IReadOnlyList<string>>? OrderProvider { get; set; }

    /// <summary>把某行滚入视口（移动选中后由视图订阅执行）。</summary>
    public event EventHandler<LinkItem>? FocusRowRequested;

    /// <summary>当前选中行（只含已填充真实行；缓存一次，避免绑定重复遍历十万条）。</summary>
    private IReadOnlyList<LinkItem> _selectedItems = Array.Empty<LinkItem>();
    public IReadOnlyList<LinkItem> SelectedItems => _selectedItems;

    public int SelectionCount { get; private set; }
    public bool HasSelection => SelectionCount > 0;
    private LinkItem? PrimarySelected => _selectedItems.FirstOrDefault();

    /// <summary>「跳转」可用性 = **恰选中一项真实行**（多选/占位/未加载无跳转目标）。</summary>
    public bool JumpEnabled => SelectionCount == 1;

    /// <summary>ID → 结果项（十万条时箭头/滚入视口不能每次线性找）。只登记已填充真实行。</summary>
    private readonly Dictionary<string, LinkItem> _itemsById = new(StringComparer.Ordinal);
    private readonly List<string> _orderedIds = new();

    /// <summary>当前视觉顺序（已填充的真实行；补全后就是全部命中）：↑/↓ 与 Ctrl+A 直接复用。</summary>
    public IReadOnlyList<string> OrderedLinkIds => _orderedIds;

    /// <summary>行位置 → 已填充行的 ID（未填充 = null）。视口优先让页乱序到达时，
    /// 导航顺序仍按行序重建——↑/↓、Shift 区间、Ctrl+A、End 都依赖它。</summary>
    private string?[] _idsByRow = Array.Empty<string?>();

    /// <summary>当前快照会话（空 = 无进行中的搜索）。关闭由 CancelSession/LoadRemaining 收尾。</summary>
    private string? _sessionId;
    private CancellationTokenSource? _loadCts;
    private int _searchGen;
    private bool _selectAllAfterLoad;
    private bool _selectLastAfterLoad;

    /// <summary>补页记账：已加载页位图 + 顺序游标 + 视口请求页（同一快照内的调度状态）。</summary>
    private int _pageCount;
    private bool[]? _pageLoaded;
    private int _nextSequentialPage;
    private int _priorityPage;

    /// <summary>新结果集落地后，只登记已填充前缀（绝不遍历十万个占位行）。</summary>
    private void ResetLoadedMaps(int loadedCount)
    {
        _itemsById.Clear();
        _idsByRow = new string?[Results.Count];
        for (var i = 0; i < loadedCount && i < Results.Count; i++)
            RegisterLoaded(Results[i], i);
        RebuildOrderedIds();
    }

    private bool RegisterLoaded(LinkItem item, int rowIndex)
    {
        if (string.IsNullOrEmpty(item.LinkId)) return false;
        if (!_itemsById.TryAdd(item.Key, item)) return false;
        _idsByRow[rowIndex] = item.Key;
        return true;
    }

    private void RebuildOrderedIds()
    {
        _orderedIds.Clear();
        foreach (var id in _idsByRow)
            if (id is not null) _orderedIds.Add(id);
    }

    /// <summary>整表重排后按新行序重建行号索引；`_itemsById` 不变（同一批行对象，键没变）。</summary>
    private void ReorderLoadedMaps(IReadOnlyList<LinkItem> sorted)
    {
        _idsByRow = new string?[sorted.Count];
        _orderedIds.Clear();
        _orderedIds.Capacity = sorted.Count;
        for (var i = 0; i < sorted.Count; i++)
        {
            var key = sorted[i].Key;
            _idsByRow[i] = key;
            _orderedIds.Add(key);
        }
    }

    /// <summary>
    /// 视图在滚动时调用：把包含该行号的页提到补页队列最前（仍取自同一快照）。
    /// 快速滑到末尾时不必等前面所有页顺序到达；同页已加载则忽略。
    /// </summary>
    public void RequestVisibleRow(int rowIndex)
    {
        if (rowIndex < 0 || TotalHitCount <= 0) return;
        if (rowIndex >= TotalHitCount) rowIndex = TotalHitCount - 1;
        var page = rowIndex / RemainingLoadSize + 1;
        var loaded = _pageLoaded;
        if (loaded is null || page > loaded.Length || loaded[page - 1]) return;
        _priorityPage = page;
    }

    /// <summary>视图用：按 ID 取结果项（选中投影 / 滚入视口）。</summary>
    public LinkItem? FindItem(string? linkId)
        => linkId != null && _itemsById.TryGetValue(linkId, out var item) ? item : null;

    /// <summary>选中的**唯一投影点**：集合 → 属性通知 + 右栏（空占位 / 单选详情 / 多选计数三态）。
    /// 只查已填充行；不再每次点选遍历十万条结果。</summary>
    private void OnSelectionChanged()
    {
        var sel = new List<LinkItem>(Selection.Count);
        foreach (var id in Selection.Ids)
        {
            if (_itemsById.TryGetValue(id, out var item)
                && item is not SearchResultRow { IsPlaceholder: true })
                sel.Add(item);
        }
        _selectedItems = sel;
        SelectionCount = sel.Count;
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(JumpEnabled));
        if (sel.Count == 0) Details.UpdateFrom(null, LocValue.Empty);
        else if (sel.Count == 1) Details.UpdateFrom(sel[0], _resolveFolderPath(sel[0].ListId));
        else Details.ShowMulti(sel);
        CommandRefresh.Request();
    }

    private void SelectAll()
    {
        if (IsLoadingAll)
        {
            _selectAllAfterLoad = true;
            return;
        }
        Selection.SelectAll(_orderedIds);
    }

    private void SelectLast()
    {
        if (IsLoadingAll)
        {
            // End 的语义 = 去最后一条：先请求末页（视口优先），末行落地即可选中。
            RequestVisibleRow(TotalHitCount - 1);
            _selectLastAfterLoad = true;
            return;
        }
        var target = Selection.SelectLast(_orderedIds);
        FocusOn(target);
    }

    /// <summary>搜索页右侧详情栏（与浏览页同一 DetailSidebar 控件数据契约）。</summary>
    public SearchDetailsViewModel Details { get; } = new();

    // —— 命令 ——

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand JumpCommand { get; }
    /// <summary>「详情」/铅笔槽 = 打开浏览页的链接详情页（`Enter` / 双击同此命令）。</summary>
    public ICommand OpenDetailCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    /// <summary>删除选中（单选=原单条文案；多选=批量计数），危险键受宿主守卫（WARNINGS 48）。</summary>
    public ICommand DeleteSelectionCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand MoveSelectionCommand { get; }
    public ICommand SelectLastCommand { get; }
    public ICommand EscapeCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>视图在用户点击行时调用（带修饰键路由：Ctrl 翻转 / Shift 区间 / 无修饰 = 单选）。</summary>
    public void ClickItem(LinkItem item, ModifierKeys mods)    {
        if (item is SearchResultRow { IsPlaceholder: true }) return;
        // 选中集合按**跨类型身份键**索引（链接 l: / 文件夹 f:），不是裸 LinkId。
        Selection.Click(item.Key,
            mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), CurrentOrder());
    }

    private IReadOnlyList<string> CurrentOrder() => OrderProvider?.Invoke() ?? Array.Empty<string>();

    /// <summary>视图在取消/重置后获得焦点用（VM 不碰键盘焦点）。</summary>
    public event EventHandler? ResetRequested;

    /// <summary>结果集刚整批挂上表格：视图先挂**紧凑行模板**保证首帧快，下一帧再恢复完整模板。</summary>
    public event EventHandler? ResultSetReplaced;

    /// <summary>下一次整批替换是否保留当前阅读位置：刷新/F5 保留；**新查询**回到顶部。</summary>
    public bool PreserveScrollOnNextResultReplace { get; private set; }

    /// <summary>「位置」列与详情栏共用的路径解析（组合根注入）。</summary>
    public LocValue ResolveFolderPath(string? listId) => _resolveFolderPath(listId);

    /// <summary>位置列单行显示的字符上限：整条百层路径排版会把首帧布局拖到几十毫秒，
    /// 而列宽只放得下约 30–70 字（TextTrimming 也只呈现这一截）。超出部分与截断后不可见的部分等价。</summary>
    private const int MaxPathDisplayChars = 200;

    private readonly Dictionary<string, (int Version, string Text)> _pathTextCache = new(StringComparer.Ordinal);

    /// <summary>位置列文本：按（目录, 语言版本）缓存并限长；只服务显示，不改变路径语义/复制/详情栏。</summary>
    public string ResolveFolderPathText(string? listId, int languageVersion)
    {
        var key = listId ?? string.Empty;
        if (_pathTextCache.TryGetValue(key, out var hit) && hit.Version == languageVersion)
            return hit.Text;

        var text = ResolveFolderPath(string.IsNullOrEmpty(listId) ? null : listId).Resolve();
        if (text.Length > MaxPathDisplayChars)
            text = string.Concat(text.AsSpan(0, MaxPathDisplayChars), "…");
        _pathTextCache[key] = (languageVersion, text);
        return text;
    }

    // —— 导航生命周期（MainViewModel 的 search 路由事件转发到这里） ——

    /// <summary>进入搜索页（Shell 路由）：**不清空任何内容**——查询文本 / 结果 / 排序原样保留；
    /// 隐藏期间收到的精确变更在这里一次对账（只改变化行）；没有变更就**零工作**，
    /// 不再用“入口全量静默刷新”碰运气。随后把焦点收回搜索框。</summary>
    public void OnNavigatedTo()
    {
        _isActive = true;

        // 进入页面就把搜索命令/SQLite 读路径走热一次（查询本身不命中任何数据）：
        // 首次用户搜索不必再付 JIT + 连接池预热，首屏延迟更稳定。
        WarmUpSearchEngine();

        if (!string.IsNullOrWhiteSpace(LastQuery))
        {
            if (_loadFailed && !IsLoadingAll)
                _ = RefreshResultsAsync(forceReplace: true);
            else if (!IsLoadingAll && (_pendingFullRefresh || _pendingLinkIds.Count > 0))
                _ = ApplyPendingChangesAsync();
            // 后台补页仍在进行：让它跑完，CompleteLoad 会一次性追平变更。
        }
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private int _warmStarted;

    private void WarmUpSearchEngine()
    {
        if (Interlocked.Exchange(ref _warmStarted, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var warm = await _api.SearchSessionOpenAsync("\u0001warmup",
                    searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                    sortBy: "title", sortOrder: "asc", firstPageSize: 1, includeFolders: true);
                await _api.SearchSessionCloseAsync(warm.SessionId);
            }
            catch { /* 预热失败不影响真实搜索 */ }
        });
    }

    /// <summary>离开搜索页：只清选中与详情栏（保留查询与结果，返回时原样呈现）。
    /// 隐藏期间的数据变更由 <see cref="NotifyDataChanges"/> 排队，进入时一次对账。</summary>
    public void OnNavigatedFrom()
    {
        _isActive = false;
        Selection.Clear();
    }

    private bool _isDeleting;

    /// <summary>
    /// 删除在途：命令可用性据此置灰，删除入口再挡一次。
    /// </summary>
    /// <remarks>
    /// 删除是逐条命令的循环写，连点/连发时第二次会对着已移入回收站的条目再删一次，
    /// 结果是"清空结果 + 报删除失败"的空态盖住刚刚成功的操作。
    /// </remarks>
    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (_isDeleting == value) return;
            _isDeleting = value;
            OnPropertyChanged();
            CommandRefresh.Request();
        }
    }

    private bool _isNavigating;

    /// <summary>
    /// 用户发起的刷新在途（界面**加载遮罩的唯一来源**）：本页只有 F5 为 true；
    /// 进入页面的入口对齐刷新、事件驱动的静默刷新一律 false（不亮遮罩、不播动画）——见 BEHAVIOR-CONTRACT §1.5。
    /// </summary>
    public bool IsNavigating
    {
        get => _isNavigating;
        private set { if (_isNavigating == value) return; _isNavigating = value; OnPropertyChanged(); }
    }

    private int _searching;

    /// <summary>搜索/静默刷新在途（并发计数）。量具与测试据此等"结果真的落地"，
    /// 不靠固定 Pump 时间猜；计数避免范围防抖与 F5 交叠时提前变 false。</summary>
    public bool IsSearching => System.Threading.Volatile.Read(ref _searching) > 0;

    private void BeginSearching()
    {
        System.Threading.Interlocked.Increment(ref _searching);
        OnPropertyChanged(nameof(IsSearching));
    }

    private void EndSearching()
    {
        System.Threading.Interlocked.Decrement(ref _searching);
        OnPropertyChanged(nameof(IsSearching));
    }

    /// <summary>一次**用户发起**的刷新（F5）结束：视图据此播行入场动画并收回焦点。</summary>
    public event EventHandler? RefreshCompleted;

    /// <summary>
    /// F5 真刷新：重跑**当前已执行的查询**（<see cref="LastQuery"/>）并保留选中——
    /// 只剔除已不在新结果里的 ID（刷新后仍存在的项保留选中）。
    /// 按"导航加载口径"亮遮罩 + 播入场动画，与浏览页 F5 同源；
    /// 输入框里已改但未执行的文本不参与（那是"回车/搜索按钮"的语义，避免按 F5 变成静默换查询）。
    /// </summary>
    private async Task RefreshAsync()
    {
        CancelStartupWarm();
        if (string.IsNullOrWhiteSpace(LastQuery)) return;
        IsNavigating = true;
        try
        {
            await RefreshResultsAsync(forceReplace: true);
        }
        finally
        {
            IsNavigating = false;
            RefreshCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    // —— 查询执行 ——

    /// <summary>
    /// 启动后空闲预热：随便一个文本查询都会沿搜索覆盖索引做一次全扫（结果不用），
    /// 目的只是把索引页读热；不预取任何用户结果、不建占位行。真实搜索/刷新会取消它。
    /// </summary>
    public void WarmIndexForStartup()
    {
        CancelStartupWarm();
        var slot = new StartupWarmSlot();
        lock (_startupWarmGate) _startupWarm = slot;
        _ = Task.Run(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _api.SearchCountAsync("a",
                    searchTitle: true, searchUrl: true, searchDescription: true,
                    searchPath: false, ct: slot.Token);
                LpLog.Info($"startup warm: search index {watch.ElapsedMilliseconds}ms",
                    category: "app.lifecycle");
            }
            catch (OperationCanceledException)
            {
                // 用户已开始搜索：真实请求优先。
            }
            catch (Exception ex)
            {
                LpLog.Warn("startup warm: search index failed", ex, category: "app.lifecycle");
            }
            finally
            {
                slot.Finish();
            }
        });
    }

    /// <summary>取消启动预热（真实搜索/刷新时调用；幂等）。</summary>
    public void CancelStartupWarm()
    {
        StartupWarmSlot? slot;
        lock (_startupWarmGate)
        {
            slot = _startupWarm;
            _startupWarm = null;
        }

        slot?.Cancel();
    }

    private readonly object _startupWarmGate = new();
    private StartupWarmSlot? _startupWarm;

    private async Task SearchAsync()
    {
        CancelStartupWarm();   // 真实搜索优先，启动预热让路
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ResetToEmpty();
            return;
        }

        // 范围守卫：四个范围全部取消勾选时没有可搜字段，直接给引导空态
        if (HasNoScope)
        {
            CancelSession();
            _searchGen++;
            Selection.Clear();
            ResetResultState();
            ShowNoScopeState();
            return;
        }

        CancelSession();
        ClearPendingChanges();   // 新快照本身覆盖这一刻的全部已提交状态
        _loadFailed = false;
        _pendingPathSort = string.Equals(_sortField, "path", StringComparison.Ordinal);
        var gen = ++_searchGen;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        BeginSearching();
        Selection.Clear();
        EmptyState = new SearchEmptyState("magnify", Loc.K("search.state.searching"), null,
            "SecondaryContainer", "OnSecondaryContainer");
        ResetResultState();

        try
        {
            // 快照会话：count 与后续每一页来自同一 SQLite 读事务；首屏预取只取视口×2（下限64）。
            var snapshot = await OpenSnapshotAsync(query, PrefetchRows, cts.Token);
            if (gen != _searchGen || cts.IsCancellationRequested)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                return;
            }

            _sessionId = snapshot.SessionId;
            LastQuery = query;
            OnPropertyChanged(nameof(LastQuery));
            TotalHitCount = snapshot.Total;
            OnPropertyChanged(nameof(TotalHitCount));

            var loadingText = Loc.T("search.result.rowLoading");
            var initial = await BuildInitialRowsAsync(
                snapshot.Total, snapshot.Prefetch, query, loadingText, cts.Token);
            if (gen != _searchGen || cts.IsCancellationRequested)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                return;
            }

            _loadedCount = Math.Min(snapshot.Total, snapshot.Prefetch.Count);
            PreserveScrollOnNextResultReplace = false;
            Results.ReplaceAll(initial);
            ResultSetReplaced?.Invoke(this, EventArgs.Empty);
            ResetLoadedMaps(_loadedCount);
            OnPropertyChanged(nameof(LoadedCount));
            OnPropertyChanged(nameof(LoadingNotice));

            EmptyState = snapshot.Total == 0
                ? new SearchEmptyState("emoticon-sad-outline",
                    Loc.K("search.state.noResults", query),
                    Loc.K("search.state.noResultsHint"),
                    "SecondaryContainer", "OnSecondaryContainer")
                : new SearchEmptyState("magnify", Loc.K("search.state.prompt"),
                    Loc.K("search.state.promptHint"),
                    "PrimaryContainer", "OnPrimaryContainer");

            if (snapshot.Total == 0)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                _sessionId = null;
                IsLoadingAll = false;
                return;
            }

            StartLoadingRemaining(gen, query, snapshot.Total, cts);
        }
        catch (OperationCanceledException)
        {
            await CloseSessionQuietlyAsync(_sessionId);
            _sessionId = null;
        }
        catch (Exception ex)
        {
            LpLog.Error("search: opening the result snapshot failed", ex);
            if (gen == _searchGen)
            {
                EmptyState = new SearchEmptyState("alert-outline", Loc.K("search.state.failed"),
                    Loc.K("err.unexpected"), "SurfaceContainerHighest", "OnSurface");
                ResetResultState();
            }
            await CloseSessionQuietlyAsync(_sessionId);
            _sessionId = null;
        }
        finally
        {
            EndSearching();
            if (!IsLoadingAll)
            {
                if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
                cts.Dispose();
            }
        }
    }

    private void ResetToEmpty()
    {
        CancelSession();
        _searchGen++;
        Query = "";
        LastQuery = "";
        Selection.Clear();
        ResetResultState();
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        CancelSession();
        _searchGen++;
        Query = "";
        LastQuery = "";
        Selection.Clear();
        ResetResultState();
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空结果状态（不动查询/选中；调用方决定是否清选中）。</summary>
    private void ResetResultState()
    {
        PreserveScrollOnNextResultReplace = false;
        Results.ReplaceAll(Array.Empty<LinkItem>());
        _itemsById.Clear();
        _orderedIds.Clear();
        _idsByRow = Array.Empty<string?>();
        _pathTextCache.Clear();
        _pageCount = 0;
        _pageLoaded = null;
        _nextSequentialPage = 0;
        _priorityPage = 0;
        _loadedCount = 0;
        TotalHitCount = 0;
        _selectAllAfterLoad = false;
        _selectLastAfterLoad = false;
        IsLoadingAll = false;
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(TotalHitCount));
        OnPropertyChanged(nameof(LoadingNotice));
    }

    // —— 选中移动 / Esc（与浏览页主栏同一套共享语义；顺序由视图注入） ——

    /// <summary>命令参数的方向字面量（↑ = -1 / ↓ = +1）。</summary>
    private static int ParseDirection(object? p)
    {
        var s = (p as string is string str ? str : p?.ToString()) ?? string.Empty;
        return s.Contains("up") ? -1 : s.Contains("down") ? 1 : 0;
    }

    private void MoveSelection(int delta)
    {
        var target = Selection.Move(delta, CurrentOrder());
        FocusOn(target);
    }

    private void FocusOn(string? linkId)
    {
        if (FindItem(linkId) is { } item) FocusRowRequested?.Invoke(this, item);
    }

    /// <summary>Esc 分层（与浏览页同口径）：有选中 → 清选中；否则清空（=「取消」）。</summary>
    private void Escape()
    {
        if (Selection.HasAny)
        {
            Selection.Clear();
            return;
        }
        Cancel();
    }

    // —— 范围/排序变化 → 新快照刷新；后台原位补齐全部命中（无分页边界、无截断） ——

    private string _sortField = "title";
    private bool _sortAscending = true;
    /// <summary>位置排序等全量到齐后再应用（引擎白名单无 path，快照仍按 title 取页）。</summary>
    private bool _pendingPathSort;

    private void OnScopeChanged()
    {
        // 范围 chip 是**用户的一次明确点击**，不是连续输入：立即刷新（代际守卫丢弃旧结果）。
        if (!string.IsNullOrWhiteSpace(LastQuery))
            _ = RefreshResultsAsync(forceReplace: true);
    }

    /// <summary>表头排序：普通列交给引擎重算（新快照会话）；「位置」列引擎没有排序键，
    /// 用目录树前序在**全量到齐后**本地重排（补页期间先排队，完成时应用）。</summary>
    public Task SortAsync(string field, bool ascending)
    {
        if (string.Equals(field, _sortField, StringComparison.Ordinal) && ascending == _sortAscending)
            return Task.CompletedTask;
        _sortField = field;
        _sortAscending = ascending;
        if (!string.Equals(field, "path", StringComparison.Ordinal))
        {
            _pendingPathSort = false;
            return string.IsNullOrWhiteSpace(LastQuery) ? Task.CompletedTask : RefreshResultsAsync(forceReplace: true);
        }

        // 位置排序：全量已到 → 立即本地重排；仍在补页 → 记下意图，CompleteLoad 统一应用。
        if (string.IsNullOrWhiteSpace(LastQuery) || Results.Count == 0)
            return Task.CompletedTask;
        if (IsLoadingAll)
        {
            _pendingPathSort = true;
            return Task.CompletedTask;
        }
        _pendingPathSort = false;
        ApplyPathSortNow();
        return Task.CompletedTask;
    }

    /// <summary>取消旧代次并关闭其快照会话；不修改结果/选中的最终状态（调用方决定清空或保留）。</summary>
    private void CancelSession()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        var sessionId = _sessionId;
        _sessionId = null;
        if (!string.IsNullOrEmpty(sessionId))
            _ = CloseSessionQuietlyAsync(sessionId);

        _loadFailed = false;
        _pendingPathSort = false;
        IsLoadingAll = false;
        _selectAllAfterLoad = false;
        _selectLastAfterLoad = false;
        OnPropertyChanged(nameof(LoadingNotice));
    }

    private void StartLoadingRemaining(int gen, string query, int total, CancellationTokenSource cts)
    {
        if (_loadedCount >= total)
        {
            var sessionId = _sessionId;
            _sessionId = null;
            if (!string.IsNullOrEmpty(sessionId))
                _ = CloseSessionQuietlyAsync(sessionId);
            CompleteLoad();
            cts.Dispose();
            return;
        }

        _pageCount = (total + RemainingLoadSize - 1) / RemainingLoadSize;
        _pageLoaded = new bool[_pageCount];
        _nextSequentialPage = 1;
        _priorityPage = 0;
        IsLoadingAll = true;
        _ = LoadRemainingAsync(gen, query, total, cts, cts.Token);
    }

    private async Task<(string SessionId, int Total, List<LinkDto> Prefetch)> OpenSnapshotAsync(
        string query, int prefetchRows, CancellationToken ct)
    {
        var title = SearchTitle;
        var url = SearchUrl;
        var desc = SearchDesc;
        var path = SearchPath;
        // 引擎白名单没有 path：位置排序先用 title 取一份完整快照，补全后本地重排。
        var sortBy = string.Equals(_sortField, "path", StringComparison.Ordinal) ? "title" : _sortField;
        var sortOrder = string.Equals(_sortField, "path", StringComparison.Ordinal)
            ? "asc"
            : _sortAscending ? "asc" : "desc";

        var open = await Task.Run(() => _api.SearchSessionOpenAsync(query,
            searchTitle: title, searchUrl: url,
            searchDescription: desc, searchPath: path,
            sortBy: sortBy, sortOrder: sortOrder, firstPageSize: prefetchRows,
            includeFolders: true, ct: ct), ct);

        return (open.SessionId, open.Total, open.FirstPage?.ToList() ?? []);
    }

    /// <summary>后台取一页（快照会话；不取 count，排序/范围由会话固定）。</summary>
    private async Task<List<LinkDto>> QuerySessionPageAsync(
        string sessionId, int page, int perPage, CancellationToken ct)
        => await Task.Run(() => _api.SearchSessionPageAsync(
            sessionId, page: page, perPage: perPage, ct: ct), ct);

    /// <summary>首屏：总数条占位行 + 前 prefetch 行真实数据；一次 Reset 挂进表格，绝不再整批替换。</summary>
    private static Task<List<LinkItem>> BuildInitialRowsAsync(
        int total, IReadOnlyList<LinkDto> prefetch, string query, string loadingText, CancellationToken ct)
        => Task.Run(() =>
        {
            var rows = new List<LinkItem>(total);
            for (var i = 0; i < total; i++)
                rows.Add(new SearchResultRow { Title = loadingText, Url = "\u00A0" });

            var filled = Math.Min(total, prefetch.Count);
            for (var i = 0; i < filled; i++)
            {
                var row = new SearchResultRow();
                row.Fill(prefetch[i], query);
                rows[i] = row;
            }
            ct.ThrowIfCancellationRequested();
            return rows;
        }, ct);

    /// <summary>原位填充一页：只改占位行字段，不替换集合、不重建容器、不移动滚动位置。
    /// 页可以乱序到达（视口优先），因此只按行号写；返回本次真正填入的行数。</summary>
    private int ApplyPage(IReadOnlyList<LinkDto> rows, int page, string query)
    {
        var offset = (page - 1) * RemainingLoadSize;
        var filled = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var index = offset + i;
            if (index >= Results.Count) break;
            if (Results[index] is not SearchResultRow row || !row.IsPlaceholder) continue;

            row.Fill(rows[i], query);
            if (!RegisterLoaded(row, index))
                throw new InvalidOperationException($"duplicate link id in search snapshot: {rows[i].LinkId}");
            filled++;
        }

        if (filled > 0) RebuildOrderedIds();
        _loadedCount += filled;
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(LoadingNotice));
        if (Selection.HasAny) OnSelectionChanged();

        // End 在加载中按下：末行一落地就完成“选中 + 滚入视口”，不等其余页。
        if (_selectLastAfterLoad && _idsByRow.Length > 0 && _idsByRow[^1] is not null)
        {
            _selectLastAfterLoad = false;
            FocusOn(Selection.SelectLast(_orderedIds));
        }
        return filled;
    }

    /// <summary>取下一页：视口请求页优先，其后按顺序推进；两者都取自同一快照会话。</summary>
    private int NextPageToLoad()
    {
        var loaded = _pageLoaded;
        if (loaded is null) return 0;

        var priority = _priorityPage;
        if (priority > 0 && priority <= _pageCount && !loaded[priority - 1])
        {
            _priorityPage = 0;
            return priority;
        }

        while (_nextSequentialPage <= _pageCount && loaded[_nextSequentialPage - 1])
            _nextSequentialPage++;
        return _nextSequentialPage <= _pageCount ? _nextSequentialPage : 0;
    }

    private async Task LoadRemainingAsync(
        int gen, string query, int total, CancellationTokenSource cts, CancellationToken ct)
    {
        var sessionId = _sessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            cts.Dispose();
            return;
        }

        try
        {
            // 先给首屏一个完整渲染帧，再开始后台补页——首屏延迟不背后续页。
            await Task.Delay(150, ct);
            if (ct.IsCancellationRequested || gen != _searchGen) return;

            var loadedPages = 0;
            while (!ct.IsCancellationRequested && gen == _searchGen && loadedPages < _pageCount)
            {
                var page = NextPageToLoad();
                if (page == 0) break;

                var rows = await QuerySessionPageWithRetryAsync(sessionId, page, RemainingLoadSize, ct);
                if (ct.IsCancellationRequested || gen != _searchGen) return;

                _pageLoaded![page - 1] = true;
                loadedPages++;
                if (rows.Count == 0)
                {
                    // 总数来自同一快照：非末页读到空页 = 快照读取异常，继续只会无限空转。
                    if (page < _pageCount)
                        throw new InvalidOperationException(
                            $"search snapshot page {page}/{_pageCount} returned no rows");
                    continue;
                }

                ApplyPage(rows, page, query);
                if (_loadedCount >= total) break;
            }

            if (ct.IsCancellationRequested || gen != _searchGen) return;
            if (_loadedCount >= total) CompleteLoad();
            else MarkLoadFailed($"snapshot returned {_loadedCount}/{total} rows");
        }
        catch (OperationCanceledException) { /* 新查询接管 */ }
        catch (Exception ex)
        {
            LpLog.Error("search: loading remaining snapshot pages failed", ex);
            if (gen == _searchGen) MarkLoadFailed("snapshot page read failed");
        }
        finally
        {
            await CloseSessionQuietlyAsync(sessionId);
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) _sessionId = null;
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>单页最多尝试 3 次（初次 + 2 次重试）；取消不重试。快照读事务仍有效，重读同一页是安全的。</summary>
    private async Task<List<LinkDto>> QuerySessionPageWithRetryAsync(
        string sessionId, int page, int perPage, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await QuerySessionPageAsync(sessionId, page, perPage, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < attempts)
            {
                LpLog.Warn($"search: snapshot page {page} failed (attempt {attempt}/{attempts}), retrying",
                    ex, category: "search");
                await Task.Delay(TimeSpan.FromMilliseconds(80 * attempt), ct);
            }
        }
    }

    /// <summary>
    /// 位置排序（本地）：按目录树前序整数键重排**已全部落地**的结果集，文件夹块与链接块各自有序、
    /// 文件夹块仍在前（与引擎的块顺序口径一致）；替换集合后重建行号索引（↑/↓、Ctrl+A、End 依赖它）。
    /// </summary>
    private void ApplyPathSortNow()
    {
        if (Results.Count < 2 || _loadedCount < TotalHitCount) return;

        var dir = _sortAscending ? 1 : -1;

        // 每行只解析一次目录前序键与标题（十万行若在比较器里查字典 = 百万级查找）。
        var folders = new List<(int Order, string Title, string Id, LinkItem Item)>();
        var links = new List<(int Order, string Title, string Id, LinkItem Item)>();
        foreach (var item in Results)
        {
            var entry = (_resolveFolderOrder(item.ListId), item.DisplayTitle, item.LinkId, item);
            (item.IsFolder ? folders : links).Add(entry);
        }

        static int Compare(
            (int Order, string Title, string Id, LinkItem Item) a,
            (int Order, string Title, string Id, LinkItem Item) b, int dir)
        {
            var byPath = a.Order.CompareTo(b.Order);
            if (byPath != 0) return byPath * dir;
            var byName = string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            if (byName != 0) return byName * dir;
            return string.CompareOrdinal(a.Id, b.Id) * dir;
        }
        folders.Sort((a, b) => Compare(a, b, dir));
        links.Sort((a, b) => Compare(a, b, dir));

        var sorted = new List<LinkItem>(Results.Count);
        foreach (var entry in folders) sorted.Add(entry.Item);
        foreach (var entry in links) sorted.Add(entry.Item);

        PreserveScrollOnNextResultReplace = true;   // 排序保留阅读位置（视图在 Reset 时恢复）
        Results.ReplaceAll(sorted);
        ReorderLoadedMaps(sorted);
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(LoadingNotice));
        if (Selection.HasAny) OnSelectionChanged();
        LpLog.Info($"search path sort rows={sorted.Count} asc={_sortAscending}", category: "search");
    }

    private void CompleteLoad()
    {
        _loadFailed = false;
        if (_pendingPathSort)
        {
            _pendingPathSort = false;
            ApplyPathSortNow();
        }
        var changed = Selection.RemoveMissing(id => _itemsById.ContainsKey(id));
        if (!changed) OnSelectionChanged();

        var selectAll = _selectAllAfterLoad;
        var selectLast = _selectLastAfterLoad;
        _selectAllAfterLoad = false;
        _selectLastAfterLoad = false;
        if (selectAll) Selection.SelectAll(_orderedIds);
        else if (selectLast) FocusOn(Selection.SelectLast(_orderedIds));

        // “加载完成”必须是**最后一个**发布的读数：补全期间按下的 Ctrl+A / End 落在这里才生效，
        // 先置 false 会让观察方（界面状态行、量具、回归用例）读到“已加载 N 条、选中仍是 0”的中间态。
        IsLoadingAll = false;
        OnPropertyChanged(nameof(LoadingNotice));

        // 补页期间收到的数据变更不逐行插进半成品；补完立刻从完整结果集上追平。
        if (_pendingFullRefresh || _pendingLinkIds.Count > 0)
            _ = ApplyPendingChangesAsync();
    }

    private void MarkLoadFailed(string reason)
    {
        _loadFailed = true;
        _pendingPathSort = false;
        IsLoadingAll = false;
        _selectAllAfterLoad = false;
        _selectLastAfterLoad = false;
        OnPropertyChanged(nameof(LoadingNotice));
        LpLog.Warn($"search: partial result set ({LoadedCount}/{TotalHitCount}): {reason}", category: "search");
    }

    private async Task CloseSessionQuietlyAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try { await _api.SearchSessionCloseAsync(sessionId); }
        catch (Exception ex) { LpLog.Warn("search: closing snapshot session failed", ex, category: "search"); }
    }

    private static bool SamePrefix(IReadOnlyList<LinkItem> current, IReadOnlyList<LinkDto> dtos, int count)
    {
        if (count > current.Count || count > dtos.Count) return false;
        for (var i = 0; i < count; i++)
        {
            var c = current[i];
            var d = dtos[i];
            if (!string.Equals(c.LinkId, d.LinkId, StringComparison.Ordinal)
                || !string.Equals(c.Url, d.Url ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(c.Title, d.Title ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(c.Description, d.Description ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(c.FaviconUrl, d.FaviconUrl ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(c.ListId, d.ListId, StringComparison.Ordinal)
                || c.LastVisitedAt != d.LastVisitedAt || c.VisitCount != d.VisitCount
                || c.IsImportant != d.IsImportant || c.CreatedAt != d.CreatedAt
                || c.UpdatedAt != d.UpdatedAt) return false;
        }
        return true;
    }

    /// <summary>
    /// 静默刷新：新快照会话；结果等价且已全量时整段跳过（切页入口对齐不重建行）。
    /// 数据变化时重建“总数占位 + 首屏真实行”，随后后台原位补齐。
    /// </summary>
    private async Task RefreshResultsAsync(bool forceReplace = false)
    {
        var query = LastQuery;
        if (string.IsNullOrWhiteSpace(query)) return;

        // 旧结果只有在“真正补全且未失败”时才可以走等价跳过；否则占位行不是完整结果。
        var wasComplete = !_loadFailed && _loadedCount == TotalHitCount && Results.Count == TotalHitCount;
        CancelSession();
        ClearPendingChanges();   // 全量快照已包含当前全部状态；查询期间的变更事件会在之后重新入队
        _pathTextCache.Clear();
        _loadFailed = false;
        _pendingPathSort = string.Equals(_sortField, "path", StringComparison.Ordinal);
        int gen = ++_searchGen;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        if (HasNoScope)
        {
            Selection.Clear();
            ResetResultState();
            ShowNoScopeState();
            cts.Dispose();
            return;
        }

        BeginSearching();
        try
        {
            var snapshot = await OpenSnapshotAsync(query, PrefetchRows, cts.Token);
            if (gen != _searchGen || cts.IsCancellationRequested)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                return;
            }

            var prefetchCount = Math.Min(snapshot.Total, snapshot.Prefetch.Count);
            var unchanged = !forceReplace
                && wasComplete
                && !IsLoadingAll
                && Results.Count == TotalHitCount
                && TotalHitCount == snapshot.Total
                && prefetchCount <= Results.Count
                && SamePrefix(Results, snapshot.Prefetch, prefetchCount);
            if (unchanged)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                return;
            }

            // 会话一接手就记为当前会话：占位构建失败/取消时 catch 才能把它关掉（否则要等 TTL 兜底）。
            _sessionId = snapshot.SessionId;
            var loadingText = Loc.T("search.result.rowLoading");
            var initial = await BuildInitialRowsAsync(
                snapshot.Total, snapshot.Prefetch, query, loadingText, cts.Token);
            if (gen != _searchGen || cts.IsCancellationRequested)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                _sessionId = null;
                return;
            }

            var hadSelection = Selection.HasAny;
            TotalHitCount = snapshot.Total;
            _loadedCount = prefetchCount;
            PreserveScrollOnNextResultReplace = true;
            Results.ReplaceAll(initial);
            ResultSetReplaced?.Invoke(this, EventArgs.Empty);
            ResetLoadedMaps(_loadedCount);
            if (TotalHitCount == 0) Selection.Clear();
            else if (hadSelection) OnSelectionChanged();
            OnPropertyChanged(nameof(TotalHitCount));
            OnPropertyChanged(nameof(LoadedCount));
            OnPropertyChanged(nameof(LoadingNotice));

            EmptyState = snapshot.Total == 0
                ? new SearchEmptyState("emoticon-sad-outline",
                    Loc.K("search.state.noResults", query),
                    Loc.K("search.state.noResultsHint"),
                    "SecondaryContainer", "OnSecondaryContainer")
                : new SearchEmptyState("magnify", Loc.K("search.state.prompt"),
                    Loc.K("search.state.promptHint"),
                    "PrimaryContainer", "OnPrimaryContainer");

            if (snapshot.Total == 0)
            {
                await CloseSessionQuietlyAsync(snapshot.SessionId);
                _sessionId = null;
                IsLoadingAll = false;
                return;
            }

            StartLoadingRemaining(gen, query, snapshot.Total, cts);
        }
        catch (OperationCanceledException)
        {
            await CloseSessionQuietlyAsync(_sessionId);
            _sessionId = null;
        }
        catch (Exception ex)
        {
            LpLog.Error("search: refreshing the result snapshot failed", ex);
            if (gen == _searchGen)
            {
                _loadFailed = true;
                IsLoadingAll = false;
                OnPropertyChanged(nameof(LoadingNotice));
                if (Results.Count == 0)
                    EmptyState = new SearchEmptyState("alert-outline", Loc.K("search.state.failed"),
                        Loc.K("err.unexpected"), "SurfaceContainerHighest", "OnSurface");
            }
            await CloseSessionQuietlyAsync(_sessionId);
            _sessionId = null;
        }
        finally
        {
            EndSearching();
            if (!IsLoadingAll)
            {
                if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
                cts.Dispose();
            }
        }
    }

    // —— 页面动作 ——

    /// <summary>
    /// **「跳转」**：进浏览页对应目录并选中该行（定位组件 <see cref="IContentLocator"/>，
    /// 与「ID 跳转」工具同一条流水线；**不是**打开详情页）。结果行来自真实搜索结果，
    /// 失败（目标刚被移走 / 无界面宿主 / 组件不可用）一律如实提示——绝不静默。
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

        var result = item.IsFolder
            ? await _locator.LocateFolderAsync(item.LinkId)
            : await _locator.LocateLinkAsync(item.LinkId);
        if (result.IsSuccess || result.UserNotified) return;   // 阻断类提示已由共享 NoticeBar 给出

        _dialogs.Alert(Loc.T("common.jump"), (result.Message ?? result.Status switch
        {
            LocateStatus.NotFound => item.IsFolder ? Loc.K("locate.notFoundFolderId") : Loc.K("locate.notFoundLink"),
            LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
            LocateStatus.Failed => Loc.K("locate.failedRetry"),
            _ => Loc.K("locate.incomplete"),
        }).Resolve());
    }

    /// <summary>搜索侧栏「打开网站」：默认浏览器打开并记录一次访问（与浏览页侧栏同口径）。</summary>
    private async Task OpenSelectedWebsiteAsync()
    {
        var item = PrimarySelected;
        if (item == null || item.IsFolder) return;   // 文件夹没有网站可开（按钮已隐，这里挡键盘/自动化路径）
        Services.LinkLauncher.Open(item.Url);
        try
        {
            await _api.LinkVisitRecordAsync(item.LinkId);
            if (Selection.Count == 1 && Selection.Contains(item.Key))
                Details.UpdateFrom(item, _resolveFolderPath(item.ListId)); // 统计行原位刷新
        }
        catch { /* 记账失败不打断 */ }
    }

    /// <summary>删除选中：1 条 = 原单条文案；多条 = 批量计数；完成后静默重跑当前搜索刷新结果。</summary>
    private async Task DeleteSelectionAsync()
    {
        if (IsDeleting) return;   // 命令可用性已拦，这里再挡一次（键盘路径等不经 CanExecute 时）
        var victims = SelectedItems;
        if (victims.Count == 0) return;

        // 编辑优先：正在就地改名 / 整页编辑的那一条不能被跨页删除（整批拒绝，避免半删）。
        // 提示与跳转闸共用 Shell 的 NoticeBar 通知栈，不弹确认框。
        if (_editState != null && victims.Any(v => _editState.IsEditingEntity(v.LinkId)))
        {
            _notices?.Show(Loc.T("browser.edit.blockedByEditing"));
            return;
        }

        // 确认文案与浏览页**同一套键**：同一个动作不允许两套说法（文件夹 = 整树进回收站）。
        var folderCount = victims.Count(v => v.IsFolder);
        var linkCount = victims.Count - folderCount;
        LocValue message;
        if (folderCount > 0 && linkCount > 0)
            message = Loc.K("browser.confirm.mixedToTrash", folderCount, linkCount);
        else if (folderCount > 0)
            message = folderCount == 1
                ? Loc.K("browser.confirm.folderToTrash", victims.First(v => v.IsFolder).Title)
                : Loc.K("browser.confirm.foldersToTrash", folderCount);
        else
            message = linkCount == 1
                ? Loc.K("browser.confirm.linkToTrash",
                    string.IsNullOrEmpty(victims[0].Title) ? victims[0].Url : victims[0].Title)
                : Loc.K("tools.dedup.deleteConfirm", linkCount);

        var title = folderCount > 0 && linkCount == 0
            ? Loc.T("common.title.deleteFolder")
            : folderCount > 0 ? Loc.T("common.delete") : Loc.T("common.title.deleteLink");
        if (!_dialogs.Confirm(title, message.Resolve(), Loc.T("common.delete"))) return;

        IsDeleting = true;
        try
        {
            // 链接批量入站（单命令单事务）：全选 N 条只发 1 条命令。
            // 批量预检是原子的：任一选中项已不存在 → 整批拒绝、零写入；失败后同样刷新，
            // 把陈旧行从结果集清掉，用户重试即可（不会出现"看着删了、库里却还在"）。
            var deleted = 0;
            var failed = 0;
            var deletedLinkIds = new List<string>();
            var folderDeleted = false;

            // 链接：一次批量入站（单命令单事务）。
            var ids = victims.Where(v => !v.IsFolder)
                .Select(v => v.LinkId).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count > 0)
            {
                try
                {
                    var result = await _api.LinkTrashBatchAsync(ids);
                    deleted = result.Data?.Affected ?? ids.Count;
                    deletedLinkIds.AddRange(ids);
                }
                catch (Exception ex)
                {
                    failed += ids.Count;
                    LpLog.Error($"search batch delete failed for {ids.Count} link(s)", ex);
                }
            }

            // 文件夹：逐条走 folders.delete（自带整树级联，一条命令覆盖整棵子树）。
            foreach (var folder in victims.Where(v => v.IsFolder))
            {
                try
                {
                    await _api.FolderDeleteAsync(folder.LinkId, "trash_links");
                    deleted++;
                    folderDeleted = true;
                }
                catch (Exception ex)
                {
                    failed++;
                    LpLog.Error($"search delete failed for folder '{folder.LinkId}'", ex);
                }
            }

            if (deleted > 0 || failed > 0)
            {
                if (deleted > 0) Selection.Clear();

                if (!string.IsNullOrWhiteSpace(LastQuery))
                {
                    // 纯链接删除 = 活结果集按 ID 对账（只移除这几行）；文件夹/部分失败改变整棵子树或存在
                    // 不确定状态，走全量快照，绝不让增量逻辑猜。
                    if (failed == 0 && !folderDeleted && deletedLinkIds.Count > 0)
                        await ApplyLinkChangesAsync(deletedLinkIds);
                    else
                        await RefreshResultsAsync();
                }
            }

            if (failed == 0) return;

            if (deleted > 0)
            {
                // 部分成功：如实报"删了几条、几条失败"，失败项仍留在结果里可重试
                _notices?.Show(Loc.T("browser.status.deletedWithFailures", deleted, failed));
            }
            else
            {
                EmptyState = new SearchEmptyState("alert-outline", Loc.K("search.state.deleteFailed"),
                    Loc.K("err.unexpected"), "SurfaceContainerHighest", "OnSurface");
                // 结果集非空时 EmptyState 不可见：批量失败（含选中项已在别处被删的整批拒绝）
                // 必须再给一条提示，否则用户只看到"没删掉"却没有任何原因。
                _notices?.Show(Loc.T("search.state.deleteFailed"));
            }
        }
        catch (Exception ex)
        {
            LpLog.Error("search delete refresh failed", ex);
            EmptyState = new SearchEmptyState("alert-outline", Loc.K("search.state.deleteFailed"),
                Loc.K("err.unexpected"), "SurfaceContainerHighest", "OnSurface");
        }
        finally
        {
            IsDeleting = false;
        }
    }

    // —— 空态口径（文案与色调与原实现逐字一致） ——

    private bool HasNoScope => !SearchPath && !SearchUrl && !SearchTitle && !SearchDesc;

    private void ShowGuideState() => EmptyState = new SearchEmptyState("magnify", Loc.K("search.state.prompt"),
        Loc.K("search.state.promptHint"),
        "PrimaryContainer", "OnPrimaryContainer");

    private void ShowNoScopeState() => EmptyState = new SearchEmptyState("alert-circle-outline", Loc.K("search.err.scopeRequired"),
        Loc.K("search.err.scopeRequiredHint"),
        "SecondaryContainer", "OnSecondaryContainer");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

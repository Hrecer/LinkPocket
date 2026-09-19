using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>搜索页空态描述：视图据此渲染 MD3E 空态徽章（图标 + 标题 + 副文案 + 色调）。</summary>
public sealed record SearchEmptyState(string IconKind, string Title, string? Subtitle,
    string ContainerBrush, string OnContainerBrush);

/// <summary>
/// 搜索页 ViewModel（MVVM）：查询执行、范围守卫与防抖、选中态（**完整多选**）、
/// 页面动作命令（搜索/取消/跳转/打开网站/删除所选/移动选中/全选/清选中）全部在此；
/// 视图（Views/SearchView）只负责表格装配、单元格与空态渲染、关键词高亮、把"当前视觉顺序"注入本类。
/// 数据口径：结果 = 后端 search 协议（默认排序 名称升序，与主栏一致）；
/// 「位置」路径由组合根注入的解析器解析（与浏览页/智能列表同一目录树）。
/// 选中由全站共享的 <see cref="ListSelection"/> 承载（与浏览页/回收站/智能列表/去重明细同一实现）。
/// </summary>
public sealed class SearchViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _api;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly Func<string?, string> _resolveFolderPath;

    public SearchViewModel(EngineClient api, INavigationService navigation, IDialogService dialogs,
        Func<string?, string> resolveFolderPath)
    {
        _api = api;
        _navigation = navigation;
        _dialogs = dialogs;
        _resolveFolderPath = resolveFolderPath;

        Selection.Changed += OnSelectionChanged;

        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        CancelCommand = new RelayCommand(Cancel);
        JumpCommand = new RelayCommand(
            () => { if (PrimarySelected is { } item) _navigation.OpenLinkInBrowser(item.LinkId); },
            () => Selection.HasAny);
        OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => Selection.Count == 1);
        DeleteSelectionCommand = new RelayCommand(() => _ = DeleteSelectionAsync(), () => Selection.HasAny);
        SelectAllCommand = new RelayCommand(() => Selection.SelectAll(CurrentOrder()));
        ClearSelectionCommand = new RelayCommand(() => Selection.Clear());
        MoveSelectionCommand = new RelayCommand<object?>(p => MoveSelection(ParseDirection(p)));
        SelectLastCommand = new RelayCommand(SelectLast);
        EscapeCommand = new RelayCommand(Escape);
        RefreshCommand = new RelayCommand(() => _ = RefreshFromEventAsync());

        // 详情栏的页面动作命令：打开/编辑都进入浏览页的链接详情页（详情页自带完整编辑与删除入口）
        Details.OpenCommand = JumpCommand;
        Details.RenameCommand = JumpCommand;
        Details.OpenWebsiteCommand = OpenWebsiteCommand;
        Details.DeleteCommand = DeleteSelectionCommand;
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

    private IReadOnlyList<LinkItem>? _results;
    /// <summary>结果集（null = 无行，视图据此清空 ItemsSource 只留空态）。</summary>
    public IReadOnlyList<LinkItem>? Results
    {
        get => _results;
        private set { _results = value; OnPropertyChanged(); }
    }

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

    /// <summary>当前选中行（按结果集顺序；多选时为全部）。</summary>
    public IReadOnlyList<LinkItem> SelectedItems =>
        _results == null ? Array.Empty<LinkItem>() : _results.Where(r => Selection.Contains(r.LinkId)).ToList();

    public int SelectionCount => SelectedItems.Count;
    public bool HasSelection => SelectionCount > 0;
    private LinkItem? PrimarySelected => SelectedItems.FirstOrDefault();

    public bool JumpEnabled => Selection.HasAny;

    /// <summary>选中的**唯一投影点**：集合 → 属性通知 + 右栏（空占位 / 单选详情 / 多选计数三态）。</summary>
    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(JumpEnabled));
        var sel = SelectedItems;
        if (sel.Count == 0) Details.UpdateFrom(null, "");
        else if (sel.Count == 1) Details.UpdateFrom(sel[0], _resolveFolderPath(sel[0].ListId));
        else Details.ShowMulti(sel);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>搜索页右侧详情栏（与浏览页同一 DetailSidebar 控件数据契约）。</summary>
    public SearchDetailsViewModel Details { get; } = new();

    // —— 命令 ——

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand JumpCommand { get; }
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
    public void ClickItem(LinkItem item, ModifierKeys mods)
        => Selection.Click(item.LinkId,
            mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), CurrentOrder());

    private IReadOnlyList<string> CurrentOrder() => OrderProvider?.Invoke() ?? Array.Empty<string>();

    /// <summary>视图在取消/重置后获得焦点用（VM 不碰键盘焦点）。</summary>
    public event EventHandler? ResetRequested;

    /// <summary>「位置」列与详情栏共用的路径解析（组合根注入）。</summary>
    public string ResolveFolderPath(string? listId) => _resolveFolderPath(listId);

    // —— 导航生命周期（MainViewModel 的 search 路由事件转发到这里） ——

    /// <summary>进入搜索页（Shell 路由）：**不清空任何内容**——查询文本 / 结果 / 排序原样保留；
    /// 有已执行查询时静默刷新保最新（入口对齐，与浏览页/工具页同一模式）；随后把焦点收回搜索框。</summary>
    public void OnNavigatedTo()
    {
        _ = RefreshFromEventAsync();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>离开搜索页：只清选中与详情栏（保留查询与结果，返回时原样呈现）。</summary>
    public void OnNavigatedFrom()
    {
        Selection.Clear();
    }

    /// <summary>后端数据变更（UiEventHub 防抖路由）：
    /// 输入框文本 == 已执行查询（用户正在看结果）→ **静默刷新**（不闪加载态、不丢选中，与范围切换同一条路径）；
    /// 文本已改但未执行 → 保持原口径，按当前输入框文本重跑（而非上次的 LastQuery）。</summary>
    public Task RefreshFromEventAsync()
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query)) return Task.CompletedTask;
        if (string.Equals(query, LastQuery, StringComparison.Ordinal)) return RefreshResultsAsync();
        return SearchAsync();
    }

    // —— 查询执行 ——

    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ResetToEmpty();
            return;
        }

        // 范围守卫：四个范围全部取消勾选时没有可搜字段，直接给引导空态
        // （原行为会无视范围全量返回，与"搜索范围"语义矛盾）
        if (HasNoScope)
        {
            Selection.Clear();
            ShowNoScopeState();
            Results = null;
            return;
        }

        // 加载态：清空数据 + 加载占位
        Selection.Clear();
        EmptyState = new SearchEmptyState("magnify", "正在搜索…", null,
            "SecondaryContainer", "OnSecondaryContainer");
        Results = null;

        try
        {
            var dtos = await _api.SearchLinksAsync(query,
                searchTitle: SearchTitle, searchUrl: SearchUrl,
                searchDescription: SearchDesc, searchPath: SearchPath,
                sortBy: "title", sortOrder: "asc");

            LastQuery = query;

            // 无结果：空态占位显示"没有找到"；有结果：数据驱动渲染（排序状态保持）
            var results = dtos.Select(LinkItem.FromDto).ToList();
            EmptyState = results.Count == 0
                ? new SearchEmptyState("emoticon-sad-outline",
                    $"没有找到与「{query}」相关的内容",
                    "换个关键词，或用上方标签扩大搜索范围再试试",
                    "SecondaryContainer", "OnSecondaryContainer")
                : new SearchEmptyState("magnify", "想找点什么？",
                    "输入关键词，回车即可搜索；也可以用上方标签扩大或缩小范围",
                    "PrimaryContainer", "OnPrimaryContainer");
            Results = results;
        }
        catch (Exception ex)
        {
            EmptyState = new SearchEmptyState("alert-outline", "搜索出了点小问题",
                ex.Message, "SurfaceContainerHighest", "OnSurface");
            Results = null;
        }
    }

    private void ResetToEmpty()
    {
        _scopeDebounce?.Stop();
        Query = "";
        LastQuery = "";
        Selection.Clear();
        Results = null;
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        _scopeDebounce?.Stop();
        Query = "";
        LastQuery = "";
        Selection.Clear();
        Results = null;
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
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

    private void SelectLast()
    {
        var target = Selection.SelectLast(CurrentOrder());
        FocusOn(target);
    }

    private void FocusOn(string? linkId)
    {
        if (linkId == null) return;
        var item = _results?.FirstOrDefault(r => r.LinkId == linkId);
        if (item != null) FocusRowRequested?.Invoke(this, item);
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

    // —— 范围变化 → 静默刷新（非全量）：防抖 300ms 后只替换行集合，
    //    不出现「正在搜索…」加载态、不重置排序/表头，原选中项若仍在结果中则保持选中 ——

    private DispatcherTimer? _scopeDebounce;
    private int _scopeRefreshGen;

    private void OnScopeChanged()
    {
        _scopeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _scopeDebounce.Stop();
        _scopeDebounce.Tick -= ScopeDebounce_Tick;
        _scopeDebounce.Tick += ScopeDebounce_Tick;
        _scopeDebounce.Start();
    }

    private void ScopeDebounce_Tick(object? sender, EventArgs e)
    {
        _scopeDebounce?.Stop();
        if (!string.IsNullOrWhiteSpace(LastQuery))
            _ = RefreshResultsAsync();
    }

    /// <summary>
    /// 范围变化后的就地刷新：拿新范围的结果直接替换（跳过加载占位与清空闪烁）；
    /// 代次计数防止连续切换时旧结果覆盖新结果。范围全空时给引导空态（与搜索守卫同一口径）。
    /// </summary>
    private async Task RefreshResultsAsync()
    {
        var query = LastQuery;
        if (string.IsNullOrWhiteSpace(query)) return;

        int gen = ++_scopeRefreshGen;

        if (HasNoScope)
        {
            Selection.Clear();
            ShowNoScopeState();
            Results = null;
            return;
        }

        try
        {
            var dtos = await _api.SearchLinksAsync(query,
                searchTitle: SearchTitle, searchUrl: SearchUrl,
                searchDescription: SearchDesc, searchPath: SearchPath,
                sortBy: "title", sortOrder: "asc");
            if (gen != _scopeRefreshGen) return; // 已有更新的范围变化，放弃旧结果

            var results = dtos.Select(LinkItem.FromDto).ToList();
            Results = results;

            // 选中保持（多选亦然）：剔除已不在新结果里的 ID；其余保持（投影自动更新行与右栏）
            Selection.RemoveMissing(id => results.Any(r => r.LinkId == id));
        }
        catch { /* 静默刷新失败时保留旧列表 */ }
    }

    // —— 页面动作 ——

    /// <summary>搜索侧栏「打开网站」：默认浏览器打开并记录一次访问（与浏览页侧栏同口径）。</summary>
    private async Task OpenSelectedWebsiteAsync()
    {
        var item = PrimarySelected;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
        catch { /* 无法打开时保持静默 */ }
        try
        {
            await _api.LinkVisitRecordAsync(item.LinkId);
            if (Selection.Count == 1 && Selection.Contains(item.LinkId))
                Details.UpdateFrom(item, _resolveFolderPath(item.ListId)); // 统计行原位刷新
        }
        catch { /* 记账失败不打断 */ }
    }

    /// <summary>删除选中：1 条 = 原单条文案；多条 = 批量计数；完成后静默重跑当前搜索刷新结果。</summary>
    private async Task DeleteSelectionAsync()
    {
        var victims = SelectedItems;
        if (victims.Count == 0) return;

        var message = victims.Count == 1
            ? $"将链接「{(string.IsNullOrEmpty(victims[0].Title) ? victims[0].Url : victims[0].Title)}」移入回收站吗？"
            : $"将选中的 {victims.Count} 条链接移入回收站吗？";
        if (!_dialogs.Confirm("删除链接", message, "删除")) return;

        try
        {
            foreach (var v in victims)
                await _api.LinkTrashAsync(v.LinkId);
            Selection.Clear();

            // 重跑当前搜索刷新结果（无在搜关键词时只清详情）
            if (!string.IsNullOrWhiteSpace(LastQuery))
                await RefreshResultsAsync();
        }
        catch (Exception ex)
        {
            EmptyState = new SearchEmptyState("alert-outline", "删除出了点小问题",
                ex.Message, "SurfaceContainerHighest", "OnSurface");
        }
    }

    // —— 空态口径（文案与色调与原实现逐字一致） ——

    private bool HasNoScope => !SearchPath && !SearchUrl && !SearchTitle && !SearchDesc;

    private void ShowGuideState() => EmptyState = new SearchEmptyState("magnify", "想找点什么？",
        "输入关键词，回车即可搜索；也可以用上方标签扩大或缩小范围",
        "PrimaryContainer", "OnPrimaryContainer");

    private void ShowNoScopeState() => EmptyState = new SearchEmptyState("alert-circle-outline", "请先选择搜索范围",
        "至少勾选 路径 / URL / 标题 / 描述 之一，再进行搜索",
        "SecondaryContainer", "OnSecondaryContainer");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

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
/// 搜索页 ViewModel（MVVM）：查询执行、范围守卫与防抖、选中态、
/// 页面动作命令（搜索/取消/跳转/打开网站/删除）全部在此；视图（Views/SearchView）
/// 只负责表格装配、单元格与空态渲染、关键词高亮——界面不持有任何搜索逻辑。
/// 数据口径：结果 = 后端 search 协议（默认排序 名称升序，与主栏一致）；
/// 「位置」路径由组合根注入的解析器解析（与浏览页/智能列表同一目录树）。
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

        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        CancelCommand = new RelayCommand(Cancel);
        JumpCommand = new RelayCommand(
            () => { if (SelectedItem is { } item) _navigation.OpenLinkInBrowser(item.LinkId); },
            () => SelectedItem != null);
        OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => SelectedItem != null);
        DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => SelectedItem != null);

        // 详情栏的页面动作命令：打开/编辑都进入浏览页的链接详情页（详情页自带完整编辑与删除入口）
        Details.OpenCommand = new RelayCommand(
            () => { if (SelectedItem is { } item) _navigation.OpenLinkInBrowser(item.LinkId); },
            () => SelectedItem != null);
        Details.RenameCommand = Details.OpenCommand;
        Details.OpenWebsiteCommand = OpenWebsiteCommand;
        Details.DeleteCommand = DeleteCommand;
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

    private LinkItem? _selectedItem;
    /// <summary>选中行：视图 RowClick 调 <see cref="SelectItem"/>，刷新路径下由本类恢复。</summary>
    public LinkItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            _selectedItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(JumpEnabled));
            if (value != null)
                Details.UpdateFrom(value, _resolveFolderPath(value.ListId));
            else
                Details.UpdateFrom(null, "");
        }
    }

    public bool JumpEnabled => SelectedItem != null;

    /// <summary>搜索页右侧详情栏（与浏览页同一 DetailSidebar 控件数据契约）。</summary>
    public SearchDetailsViewModel Details { get; } = new();

    // —— 命令 ——

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand JumpCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>视图在用户点击行时调用（表格行选中 → 选中态 + 详情栏）。</summary>
    public void SelectItem(LinkItem item) => SelectedItem = item;

    /// <summary>视图在取消/重置后获得焦点用（VM 不碰键盘焦点）。</summary>
    public event EventHandler? ResetRequested;

    /// <summary>「位置」列与详情栏共用的路径解析（组合根注入）。</summary>
    public string ResolveFolderPath(string? listId) => _resolveFolderPath(listId);

    // —— 导航生命周期（MainViewModel 的 search 路由事件转发到这里） ——

    /// <summary>进入搜索页：回到初始引导态（清空查询与结果，视图获得焦点）。</summary>
    public void ResetToEmpty()
    {
        _scopeDebounce?.Stop();
        Query = "";
        LastQuery = "";
        SelectedItem = null;
        Results = null;
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>离开搜索页：只清选中与详情栏（保留查询与结果，返回时原样呈现）。</summary>
    public void OnNavigatedFrom()
    {
        SelectedItem = null;
    }

    /// <summary>后端数据变更（UiEventHub 防抖路由）：与原实现同口径——按当前输入框文本重跑（而非上次的 LastQuery）。</summary>
    public Task RefreshFromEventAsync()
    {
        if (!string.IsNullOrWhiteSpace(Query.Trim()))
            return SearchAsync();
        return Task.CompletedTask;
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
            SelectedItem = null;
            ShowNoScopeState();
            Results = null;
            return;
        }

        // 加载态：清空数据 + 加载占位
        SelectedItem = null;
        EmptyState = new SearchEmptyState("magnify", "正在搜索…", null,
            "SecondaryContainer", "OnSecondaryContainer");
        Results = null;

        try
        {
            var dtos = await _api.SearchLinksAsync(query,
                searchTitle: SearchTitle, searchUrl: SearchUrl,
                searchDescription: SearchDesc, searchPath: SearchPath,
                sortBy: "title", sortOrder: "asc");
            var results = dtos.Select(MapToLinkItem).ToList();

            LastQuery = query;
            SelectedItem = null;

            // 无结果：空态占位显示"没有找到"；有结果：数据驱动渲染（排序状态保持）
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

    private void Cancel()
    {
        _scopeDebounce?.Stop();
        Query = "";
        LastQuery = "";
        SelectedItem = null;
        Results = null;
        ShowGuideState();
        ResetRequested?.Invoke(this, EventArgs.Empty);
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
            SelectedItem = null;
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

            var results = dtos.Select(MapToLinkItem).ToList();
            Results = results;

            // 选中保持：原选中项仍在新结果里 → 恢复行选中与详情栏；不在 → 清空
            if (SelectedItem is { } prev)
            {
                var still = results.FirstOrDefault(r => r.LinkId == prev.LinkId);
                SelectedItem = still;   // null 时由 setter 清空详情栏，视图随之取消行高亮
            }
        }
        catch { /* 静默刷新失败时保留旧列表 */ }
    }

    // —— 页面动作 ——

    /// <summary>搜索侧栏「打开网站」：默认浏览器打开并记录一次访问（与浏览页侧栏同口径）。</summary>
    private async Task OpenSelectedWebsiteAsync()
    {
        var item = SelectedItem;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
        catch { /* 无法打开时保持静默 */ }
        try
        {
            await _api.LinkVisitRecordAsync(item.LinkId);
            if (SelectedItem?.LinkId == item.LinkId)
                Details.UpdateFrom(item, _resolveFolderPath(item.ListId)); // 统计行原位刷新
        }
        catch { /* 记账失败不打断 */ }
    }

    private async Task DeleteSelectedAsync()
    {
        var item = SelectedItem;
        if (item == null) return;

        var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
        if (!_dialogs.Confirm("删除链接", $"将链接「{name}」移入回收站吗？")) return;

        try
        {
            await _api.LinkTrashAsync(item.LinkId);
            SelectedItem = null;

            // 重跑当前搜索刷新结果（无在搜关键词时只清详情）
            if (!string.IsNullOrWhiteSpace(LastQuery))
                await SearchAsync();
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

    private static LinkItem MapToLinkItem(LinkDto l) => new()
    {
        LinkId = l.LinkId, Url = l.Url,
        Title = l.Title ?? "",
        Description = l.Description ?? "", FaviconUrl = l.FaviconUrl ?? "",
        ListId = l.ListId, LastVisitedAt = l.LastVisitedAt,
        VisitCount = l.VisitCount, IsImportant = l.IsImportant,
        CreatedAt = l.CreatedAt, UpdatedAt = l.UpdatedAt
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>一个重复组（同一 URL 的多条链接）在去重主表里的行数据。</summary>
public sealed class DedupGroupRow : INotifyPropertyChanged
{
    public string Url { get; init; } = string.Empty;

    private List<LinkDto> _links = new();
    /// <summary>组内链接（摘要扫描时为空；展开明细时按需加载）。</summary>
    public List<LinkDto> Links
    {
        get => _links;
        set
        {
            if (ReferenceEquals(_links, value)) return;
            _links = value;
            OnPropertyChanged();
        }
    }

    private int _count;
    /// <summary>组内链接数（摘要扫描直接给出，不依赖 Links 是否已加载）。</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            OnPropertyChanged();
        }
    }

    private LocValue _locationsSummary;
    public LocValue LocationsSummary
    {
        get => _locationsSummary;
        set
        {
            if (_locationsSummary.Equals(value)) return;
            _locationsSummary = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 两组"重复组"列表是否**渲染等价**（URL / 位置摘要 / 组内各条的关键字段逐项一致，含顺序）。
    /// 用途：重扫后判断"要不要重设主表 ItemsSource"——模板模式重设仍会重建视口行，
    /// 内容没变时纯属白烧（低 CPU 设备切页偶发明显卡顿）。
    /// </summary>
    public static bool SameSequence(IReadOnlyList<DedupGroupRow>? a, IReadOnlyList<DedupGroupRow>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Url != y.Url || x.Count != y.Count || x.LocationsSummary != y.LocationsSummary
                || x.Links.Count != y.Links.Count)
                return false;
            for (var j = 0; j < x.Links.Count; j++)
            {
                var p = x.Links[j];
                var q = y.Links[j];
                if (p.LinkId != q.LinkId || p.Title != q.Title || p.Url != q.Url || p.ListId != q.ListId
                    || p.UpdatedAt != q.UpdatedAt || p.LastVisitedAt != q.LastVisitedAt
                    || p.VisitCount != q.VisitCount) return false;
            }
        }
        return true;
    }
}

/// <summary>
/// 去重明细行（模板虚拟化用）：链接数据 + 已解析的位置/时间文案 + 勾选态。
/// </summary>
/// <remarks>
/// 明细表原来用工厂模式，行视觉由 <c>CellFactory</c> 每次整表重建；改模板模式后，
/// 行模板只绑定本投影，勾选与语言都靠属性通知按行更新，不再重建整表。
/// </remarks>
public sealed class DedupLinkRow : INotifyPropertyChanged
{
    public DedupLinkRow(LinkDto link, LocValue path)
    {
        Link = link;
        Path = path;
        TitleValue = string.IsNullOrWhiteSpace(link.Title)
            ? Loc.K("tools.untitled")
            : LocValue.Literal(link.Title);
        UpdatedValue = LocValue.Clock(link.UpdatedAt.ToLocalTime(), @short: false);
        LastVisitedValue = link.LastVisitedAt is { } visited
            ? LocValue.Clock(visited.ToLocalTime(), @short: false)
            : Loc.K("clock.never");
        VisitCountValue = Loc.K("count.viewsN", link.VisitCount);
    }

    public LinkDto Link { get; }
    public string LinkId => Link.LinkId;
    public string Url => Link.Url;
    public string FaviconUrl => Link.FaviconUrl;
    public LocValue TitleValue { get; }
    public LocValue Path { get; }
    public LocValue UpdatedValue { get; }
    public LocValue LastVisitedValue { get; }
    public LocValue VisitCountValue { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    private bool _isSelected;
    /// <summary>行选中的投影（唯一事实源 = <see cref="ToolsViewModel.DetailSelection"/>）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 工具页 ViewModel（MVVM）：三个工具的业务逻辑全部在此——
/// 去重（扫描/分组/位置缓存/勾选守卫/删除并局部更新）、ID 跳转（统一走
/// <see cref="IContentLocator"/> 组件）、书签导入导出（预检/导入/导出+自校验，
/// 算法全在引擎契约，本类只转发命令调用）。视图（Views/ToolsPage）只做
/// 表格装配、状态渲染与文件对话框——界面不持有任何命令调用与业务规则。
/// </summary>
public sealed class ToolsViewModel
{
    private readonly EngineClient _api;
    private readonly IContentLocator? _locator;
    private readonly Func<string?, Task<LocValue>> _resolveLinkPath;
    private readonly IBrowseEditState? _editState;
    private readonly IUiNoticeService? _notices;

    public ToolsViewModel(EngineClient api, IContentLocator? locator,
        Func<string?, Task<LocValue>> resolveLinkPath,
        IBrowseEditState? editState = null, IUiNoticeService? notices = null)
    {
        _api = api;
        _locator = locator;
        _resolveLinkPath = resolveLinkPath;
        _editState = editState;
        _notices = notices;
    }

    /// <summary>
    /// 编辑优先闸：待处置集合里只要有正在编辑的实体就整批拒绝（不删一半），
    /// 并走全站共享的 NoticeBar 提示。返回 true = 已阻断。
    /// </summary>
    public bool BlockDestructiveIfEditing(IEnumerable<string> ids)
    {
        if (_editState == null || !ids.Any(_editState.IsEditingEntity)) return false;
        _notices?.Show(Loc.T("browser.edit.blockedByEditing"));
        return true;
    }

    // ============================================================
    // —— 去重 ——
    // ============================================================

    /// <summary>本会话是否已跑过查重（外部数据变更后据此决定自动重跑）。</summary>
    public bool HasRunDedup { get; private set; }

    /// <summary>重复组（主表行集；扫描为空时为空列表）。</summary>
    public List<DedupGroupRow> Groups { get; private set; } = new();

    /// <summary>当前展开明细的组内链接（回主表后为 null）。</summary>
    public List<LinkDto>? CurrentGroupLinks { get; private set; }

    /// <summary>当前展开明细的组 URL。</summary>
    public string CurrentGroupUrl { get; private set; } = string.Empty;

    /// <summary>明细里勾选待删除的链接 ID（视图据此渲染勾选态与按钮可用性）。</summary>
    public HashSet<string> CheckedIds { get; } = new();

    /// <summary>
    /// 位置缓存：键 = 目录 ID（null 归一为空串）。去重结果里同一目录的链接通常很多，
    /// 按 ListId 解析一次即可；旧写法逐 LinkId 解析，深层大库里同一个文件夹会被走 N 遍。
    /// </summary>
    private Dictionary<string, LocValue> _pathCache = new(StringComparer.Ordinal);

    private static string PathKey(string? listId) => listId ?? string.Empty;

    /// <summary>
    /// 扫描重复 URL 并分组（UI 摘要模式：只取组 URL / 条数 / 首条目录，不取链接正文）。
    /// 组内链接在展开某组时按需加载（<see cref="LoadGroupLinksAsync"/>）。
    /// 外部宿主的 <c>dedup.scan</c> 默认仍是全量链接，不受这里影响。
    /// 抛出异常 = 读取数据失败（视图展示错误空态）；返回的列表可能为空（没有重复）。
    /// </summary>
    public async Task<List<DedupGroupRow>> RunDedupAsync()
    {
        // 摘要查询同样放线程池：进程内引擎是同步计算，留在 UI 线程会阻塞整窗。
        var scan = await Task.Run(() => _api.DedupScanAsync(includeLinks: false));

        HasRunDedup = true;

        if (scan.Count == 0)
        {
            _pathCache.Clear();
            Groups = new List<DedupGroupRow>();
            return Groups;
        }

        // 位置解析：每个首条目录一次（摘要只带首条目录）
        _pathCache = new Dictionary<string, LocValue>(StringComparer.Ordinal);
        foreach (var listId in scan.Select(g => g.FirstListId).Distinct(StringComparer.Ordinal))
            _pathCache[PathKey(listId)] = await _resolveLinkPath(listId);

        // 行对象与位置摘要是纯内存计算，同样移出 UI 线程（50k 组时仍有数万个对象）。
        var cache = _pathCache;
        Groups = await Task.Run(() => scan.Select(g => new DedupGroupRow
        {
            Url = g.Url,
            Count = g.Count,
            Links = new List<LinkDto>(),
            LocationsSummary = BuildSummary(g.FirstListId, g.Count, cache)
        }).ToList());
        return Groups;
    }

    /// <summary>清除查重结果（回到未运行态）。</summary>
    public void ClearDedup()
    {
        HasRunDedup = false;
        Groups = new List<DedupGroupRow>();
        _pathCache.Clear();
        LeaveGroup();
    }

    /// <summary>摘要模式的位置：首条所在位置 + 组内条数（与旧展示同口径）。</summary>
    private static LocValue BuildSummary(string? firstListId, int count, Dictionary<string, LocValue> cache)
    {
        var first = cache.TryGetValue(PathKey(firstListId), out var path) ? path : Loc.K("tools.pathRoot");
        return count <= 1 ? first : Loc.K("tools.pathsHint", first, count);
    }

    /// <summary>位置摘要：首条所在位置，多处时补「等 N 处」（不罗列全部，避免单元格噪音）。</summary>
    private LocValue BuildLocationsSummary(List<LinkDto> links)
        => BuildLocationsSummary(links, _pathCache);

    private static LocValue BuildLocationsSummary(List<LinkDto> links, Dictionary<string, LocValue> cache)
    {
        var paths = links
            .Select(l => cache.TryGetValue(PathKey(l.ListId), out var p) ? p : Loc.K("tools.pathRoot"))
            .ToList();
        return paths.Count == 1 ? paths[0] : Loc.K("tools.pathsHint", paths[0], paths.Count);
    }

    /// <summary>去重明细的行选中（**共享 ListSelection 核心**；对照页 = 只读：可读、可选、不可操作）。</summary>
    public ListSelection DetailSelection { get; } = new();

    /// <summary>展开某组明细：记录当前组并清空勾选与行选中；链接由 <see cref="LoadGroupLinksAsync"/> 按需加载。</summary>
    public void EnterGroup(DedupGroupRow row)
    {
        CurrentGroupUrl = row.Url;
        CurrentGroupLinks = null;
        CheckedIds.Clear();
        DetailSelection.Clear();
    }

    /// <summary>
    /// 读取当前组的全部链接（按需）：外部/界面看到的仍是同一 URL 的全量链接。
    /// 返回 null = 读取期间用户已切换组/返回主表（结果丢弃，不写回）。
    /// </summary>
    public async Task<List<LinkDto>?> LoadGroupLinksAsync(DedupGroupRow row)
    {
        var links = await Task.Run(() => _api.LinkFindByUrlAsync(row.Url));
        if (!string.Equals(CurrentGroupUrl, row.Url, StringComparison.Ordinal)) return null;

        var sorted = links
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.LinkId, StringComparer.Ordinal)
            .ToList();

        // 明细「位置」列需要组内每个目录的路径；通常只有少数几个目录。
        foreach (var listId in sorted.Select(l => l.ListId).Distinct(StringComparer.Ordinal))
        {
            var key = PathKey(listId);
            if (!_pathCache.ContainsKey(key)) _pathCache[key] = await _resolveLinkPath(listId);
        }

        row.Links = sorted;
        row.Count = sorted.Count;
        CurrentGroupLinks = sorted;
        return sorted;
    }

    /// <summary>退出明细回主表：清当前组、勾选与行选中。</summary>
    public void LeaveGroup()
    {
        CheckedIds.Clear();
        CurrentGroupLinks = null;
        CurrentGroupUrl = string.Empty;
        DetailSelection.Clear();
    }

    /// <summary>
    /// 切换某条链接的勾选态。返回 false = 触发「至少保留一条」守卫（视图闪提示，不改状态）。
    /// </summary>
    public bool ToggleChecked(string linkId)
    {
        if (CheckedIds.Contains(linkId))
        {
            CheckedIds.Remove(linkId);
            return true;
        }
        var total = CurrentGroupLinks?.Count ?? 0;
        if (total > 0 && CheckedIds.Count >= total - 1) return false;
        CheckedIds.Add(linkId);
        return true;
    }

    /// <summary>明细表某行的「位置」列（与主表同一缓存，按目录 ID 命中）。</summary>
    public LocValue ResolvePath(LinkDto link)
        => _pathCache.TryGetValue(PathKey(link.ListId), out var p) ? p : Loc.K("tools.pathRoot");

    /// <summary>
    /// 删除勾选的重复链接（单命令批量入站，整批原子），随后**局部**更新当前组：
    /// 返回非 null = 仍有多条（值为新组内链接，主表行已同步）；
    /// 返回 null = 已剩一条及以下（组已从 <see cref="Groups"/> 移除，视图应回主表）。
    /// 不再全库重读：删掉几条链接不会改变其它 URL 的分组，只有当前组需要重算。
    /// </summary>
    public async Task<List<LinkDto>?> DeleteCheckedAsync()
    {
        if (CheckedIds.Count == 0 || CurrentGroupLinks is null) return CurrentGroupLinks;

        // 编辑优先：正在就地改名 / 整页编辑的那一条不能被去重删除（整批拒绝，避免半删）。
        if (BlockDestructiveIfEditing(CheckedIds)) return CurrentGroupLinks;

        var deleted = CheckedIds.ToList();
        // 批量入站：单命令单事务；任一 ID 已消失/并发变化 → 整批回滚，不会出现删一半。
        await _api.LinkTrashBatchAsync(deleted);

        var deletedSet = new HashSet<string>(deleted, StringComparer.Ordinal);
        var url = CurrentGroupUrl;
        var rest = CurrentGroupLinks.Where(l => !deletedSet.Contains(l.LinkId)).ToList();
        CheckedIds.Clear();

        var index = Groups.FindIndex(g => string.Equals(g.Url, url, StringComparison.Ordinal));
        if (rest.Count > 1)
        {
            if (index >= 0)
            {
                var row = Groups[index];
                row.Links = rest;
                row.Count = rest.Count;
                row.LocationsSummary = BuildLocationsSummary(rest);
            }
            CurrentGroupLinks = rest;
        }
        else
        {
            if (index >= 0) Groups.RemoveAt(index);
            LeaveGroup();
        }

        // 目录树/计数的刷新由后端事件链统一承担（UiEventHub 300ms 防抖 → MainViewModel.OnBackendRefresh
        // 总会 LoadFolderTreeAsync）。这里不再同步再刷一次：删除动作因此从「等一次树加载」变成
        // 「只做批量入站 + 本地行更新」，也避免同一次变更触发两遍树刷新。
        return rest.Count > 1 ? rest : null;
    }

    // ============================================================
    // —— ID 跳转（统一走 IContentLocator 组件） ——
    // ============================================================

    /// <summary>跳转统一入口：全部经定位组件（进入目标目录并选中该行），本类不做任何定位算法。</summary>
    public async Task<LocateResult> JumpAsync(string id)
    {
        if (_locator == null)
            return new LocateResult(LocateStatus.NoHost, null, null, id, Loc.K("locate.unavailable"));
        return await _locator.LocateAsync(id);
    }

    // ============================================================
    // —— 书签导入 / 导出（算法全在引擎契约里，本类只转发命令调用） ——
    // ============================================================

    /// <summary>导入/导出流程忙态（互斥：同一时刻只允许一个流程在跑）。</summary>
    public bool BookmarkBusy { get; private set; }

    /// <summary>尝试开始一个书签流程；已有流程在跑时返回 false。</summary>
    public bool TryBeginBookmarkFlow()
    {
        if (BookmarkBusy) return false;
        BookmarkBusy = true;
        return true;
    }

    /// <summary>结束书签流程（视图在 finally 中调用）。</summary>
    public void EndBookmarkFlow() => BookmarkBusy = false;

    /// <summary>最近一次成功导出的产物路径（「打开所在文件夹」用）。</summary>
    public string LastExportPath { get; set; } = string.Empty;

    /// <summary>导入前只读预检：格式识别 + 条目统计（不写任何数据）。</summary>
    public Task<BookmarkFileInspectionDto> InspectBookmarkAsync(string filePath)
        => _api.BookmarksInspectAsync(filePath);

    /// <summary>导入：追加式还原文件夹层级，返回导入条数（引擎命令的结果 JsonElement）。</summary>
    public async Task<int> ImportBookmarksAsync(string filePath)
    {
        var result = await _api.BookmarksImportAsync(filePath);
        return ReadCount(result.Data, "links_created") + ReadCount(result.Data, "folders_created");
    }

    /// <summary>
    /// 导出：写到 <paramref name="directory"/> 下的时间戳文件名，并对产物自校验
    /// （把刚写出的文件再解析一遍，用产物自身的数据报数）。返回产物路径与校验信息。
    /// 引擎命令输出的 file_path 才是权威产物路径（含同名自动编号），以它为准。
    /// </summary>
    public async Task<(string OutputPath, BookmarkFileInspectionDto Info)> ExportBookmarksAsync(string directory)
    {
        var suggestedPath = System.IO.Path.Combine(directory,
            Loc.T("tools.export.fileName", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
        var result = await _api.BookmarksExportAsync(suggestedPath);
        var outputPath = ReadString(result.Data, "file_path") ?? suggestedPath;
        var info = await _api.BookmarksInspectAsync(outputPath);
        LastExportPath = outputPath;
        return (outputPath, info);
    }

    private static int ReadCount(JsonElement? data, string property)
    {
        if (data is { } d && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return 0;
    }

    private static string? ReadString(JsonElement? data, string property)
    {
        if (data is { } d && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }
}

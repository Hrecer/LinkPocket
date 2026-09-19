using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>回收站键盘语义分栏（主栏 / 左栏树——快捷键按活跃栏解析作用域）。</summary>
public enum TrashPane
{
    Main,
    Tree,
}

/// <summary>回收站面包屑段（可点击跳转；UnitId = null 表示根「回收站」）。</summary>
public class TrashCrumbViewModel
{
    public TrashCrumbViewModel(string? unitId, string name)
    {
        UnitId = unitId;
        Name = name;
    }

    public string? UnitId { get; }
    public string Name { get; }

    /// <summary>是否为当前位置（面包屑最后一级，高亮显示）。</summary>
    public bool IsLast { get; init; }
}

/// <summary>
/// 回收站页的数据模型（与浏览页 BrowserViewModel 同构的"回收站浏览器"）：
/// <list type="bullet">
/// <item>单一快照 = <c>trash.overview</c>（全量单元 + 全量链接快照）——树 / 主栏 / 面包屑 / 地址栏全部从它投影；</item>
/// <item>导航 = 快照内按层过滤（不逐单元取数；深层内容随子单元再打开）；历史栈 = 共享的 <see cref="NavigationHistory"/>；</item>
/// <item>选中 = 唯一 ID 集合 + 投影（行/树同源）；</item>
/// <item>写操作 = 永久删除（单条/批量，两阶段确认，见 partial Purge 文件）；
/// 刷新由引擎事件（300ms 防抖）驱动，写操作**不显式刷新**。</item>
/// </list>
/// ⚠️ 回收站**只读语义**（用户定稿 2026-09-19）：无撤销/重做、无新建/重命名/编辑、无剪贴板搬运；
/// **站内不可搬移**（条目只能被打开查看或永久删除）。
/// </summary>
public partial class TrashViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _client;

    /// <summary>UI 端口槽位（对话框等；组合根持有，命令执行时惰性读取）。</summary>
    private readonly Services.UiPortProvider _ports;

    /// <summary>根显示名（不是实体、无 ID——零哨兵）。</summary>
    public const string RootDisplayName = "回收站";

    public TrashViewModel(EngineClient client, Services.UiPortProvider ports)
    {
        _client = client;
        _ports = ports;
        Details = new TrashSidebarModel();
        DetailPane = new TrashDetailPaneModel();   // 只读详情覆盖层 = 共享详情页（与浏览页同一份界面）

        GoBackCommand = new RelayCommand(() => _ = NavigateAsync(Controller.GoBack()), () => Controller.CanGoBack);
        GoForwardCommand = new RelayCommand(() => _ = NavigateAsync(Controller.GoForward()), () => Controller.CanGoForward);
        GoUpCommand = new RelayCommand(() => _ = NavigateAsync(GetParentId(CurrentUnitId)), () => !IsAtRoot);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(navigating: true));
        CrumbClickCommand = new RelayCommand<TrashCrumbViewModel?>(crumb => _ = NavigateAsync(crumb?.UnitId));
        OpenRowCommand = new RelayCommand<TrashRowViewModel?>(row => _ = OpenRowAsync(row));
        OpenNodeCommand = new RelayCommand<TrashNode?>(node => _ = OpenNodeAsync(node));
        OpenSelectionCommand = new RelayCommand(() => _ = OpenSelectedAsync(), () => SelectionCount == 1);
        PurgeSelectionCommand = new RelayCommand(() => _ = PurgeSelectionGuardedAsync(), () => HasSelection && !IsDetailOverlayOpen);
        PurgeNodeCommand = new RelayCommand<TrashNode?>(node => _ = PurgeNodeAsync(node));
        RestoreSelectionCommand = new RelayCommand(() => _ = RestoreSelectionAsync("origin"), () => HasSelection && !IsDetailOverlayOpen);
        RestoreSelectionToRootCommand = new RelayCommand(() => _ = RestoreSelectionAsync("root"), () => HasSelection && !IsDetailOverlayOpen);

        // 详情覆盖层的动作：作用对象 = **覆盖层正在展示的那一项**，与"有没有被选中"无关，也不受覆盖层门禁约束
        // （门禁的意义是"别作用于被覆盖层挡住、看不见的选中"，而眼前这一项正是当前操作对象）。
        OpenDetailWebsiteCommand = new RelayCommand(OpenDetailWebsite, () => _detailLinkId != null);
        RestoreDetailCommand = new RelayCommand(() => _ = RestoreDetailAsync("origin"), () => _detailLinkId != null);
        RestoreDetailToRootCommand = new RelayCommand(() => _ = RestoreDetailAsync("root"), () => _detailLinkId != null);
        PurgeDetailCommand = new RelayCommand(() => _ = PurgeDetailAsync(), () => _detailLinkId != null);
        CloseDetailOverlayCommand = new RelayCommand(CloseDetailOverlay);
        CopyDetailUrlCommand = new RelayCommand(CopyDetailUrl);
        CopyDetailIdCommand = new RelayCommand(CopyDetailId);
        RestoreNodeCommand = new RelayCommand<TrashNode?>(node => _ = RestoreNodeAsync(node, "origin"),
            node => node is { IsRoot: false, IsLink: false });
        RestoreNodeToRootCommand = new RelayCommand<TrashNode?>(node => _ = RestoreNodeAsync(node, "root"),
            node => node is { IsRoot: false, IsLink: false });
        ShowContextMenuCommand = new RelayCommand(ShowContextMenuForSelection);
        CopyLinkAddressCommand = new RelayCommand<TrashRowViewModel?>(CopyLinkAddress, row => row is { IsFolder: false });
        SelectAllCommand = new RelayCommand(SelectAllRows);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        // 点空白清选中（唯一实现 = UIKit BlankClick 附加行为，按区域挂载；命令里带栏归属语义）：
        // 列表卡空白 = 主栏获得键盘语义归属；页面其它空白 = 保持当前归属（清选中 + 焦点收回页内）。
        ClearMainPaneSelectionCommand = new RelayCommand(() => { ActivatePane(TrashPane.Main); ClearSelection(); });
        ClearPageSelectionCommand = new RelayCommand(() => { ActivatePane(ActivePane); ClearSelection(); });
        MoveSelectionCommand = new RelayCommand<object?>(p => MoveMainSelection(ParseDirection(p)));
        SelectLastCommand = new RelayCommand(SelectLastRow);
        MoveTreeSelectionCommand = new RelayCommand<object?>(p => MoveTreeSelection(ParseDirection(p)));
        ToggleTreeExpandCommand = new RelayCommand(ToggleFocusedTreeExpand);
        EnterPathEditCommand = new RelayCommand(() => { if (!IsPathEditing) EnterPathEdit(); }, () => !IsPathEditing);
        ConfirmPathCommand = new RelayCommand(ConfirmPath);
        CancelPathEditCommand = new RelayCommand(CancelPathEdit);
        CompletePathCommand = new RelayCommand(CompletePath);

        // 右侧栏动作 = **复用本页既有命令**（绝不另写一套逻辑）：
        // 打开/详情 = OpenSelectionCommand（链接 → 只读详情覆盖层；单元 → 进入）；
        // 还原 / 还原到根目录 / 删除 = 作用于**选中集合**的既有命令。
        Details.OpenCommand = OpenSelectionCommand;
        Details.DeleteCommand = PurgeSelectionCommand;
        Details.RestoreCommand = RestoreSelectionCommand;
        Details.RestoreToRootCommand = RestoreSelectionToRootCommand;

        // 共享详情页（只读覆盖层）的动作**按展示项构造**（不是"作用于选中集合"的第二套逻辑：
        // 命令槽仍复用本页既有能力，只有作用对象 = 覆盖层正在展示的那一项，见 _detailLinkId）：
        DetailPane.BackCommand = CloseDetailOverlayCommand;
        DetailPane.OpenCommand = OpenDetailWebsiteCommand;                  // 主药丸 = 打开网站（浏览器打开）
        DetailPane.RestoreCommand = RestoreDetailCommand;                   // 还原（到原位置）
        DetailPane.RestoreToRootCommand = RestoreDetailToRootCommand;       // 还原到根目录
        DetailPane.DeleteCommand = PurgeDetailCommand;                      // 垃圾桶 = 永久删除
        DetailPane.CopyUrlCommand = CopyDetailUrlCommand;
        DetailPane.CopyIdCommand = CopyDetailIdCommand;
    }

    private IDialogService? Dialogs => _ports.Dialogs;

    // ================= 状态与投影 =================

    public NavigationHistory Controller { get; } = new();

    /// <summary>回收站树（与浏览页 FolderTree 同构：只装一个虚根节点，层级在它下面）。</summary>
    public ObservableCollection<TrashNode> Tree { get; } = new();

    /// <summary>主栏行（当前位置的直接内容：子单元 + 直接链接）。</summary>
    public ObservableCollection<TrashRowViewModel> Rows { get; } = new();

    /// <summary>面包屑（根「回收站」+ 逐级单元）。</summary>
    public ObservableCollection<TrashCrumbViewModel> Breadcrumbs { get; } = new();

    /// <summary>当前所在单元（null = 回收站根）。</summary>
    public string? CurrentUnitId => Controller.CurrentFolderId;
    public bool IsAtRoot => Controller.CurrentFolderId == null;
    public bool IsInUnit => !IsAtRoot;

    /// <summary>当前单元显示名（状态栏用；根 = 「回收站」）。</summary>
    public string CurrentUnitDisplayName
        => CurrentUnitId != null && _unitById.TryGetValue(CurrentUnitId, out var u) ? u.Name : RootDisplayName;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } }
    }

    private bool _isNavigating;
    /// <summary>导航加载中（遮罩唯一来源：只有用户发起的导航/刷新才置位；事件驱动的后台刷新静默）。</summary>
    public bool IsNavigating
    {
        get => _isNavigating;
        private set { if (_isNavigating != value) { _isNavigating = value; OnPropertyChanged(); } }
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        private set { if (_hasError != value) { _hasError = value; OnPropertyChanged(); } }
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(); } }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        internal set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    private TrashPane _activePane = TrashPane.Main;
    /// <summary>键盘语义归属栏（点哪栏哪栏活跃；快捷键按它解析作用域）。</summary>
    public TrashPane ActivePane
    {
        get => _activePane;
        private set { if (_activePane != value) { _activePane = value; OnPropertyChanged(); } }
    }

    /// <summary>栏被激活（视图据此把焦点收进页面——快捷键按焦点路由）。</summary>
    public event EventHandler<TrashPane>? PaneActivated;

    public void ActivatePane(TrashPane pane)
    {
        ActivePane = pane;
        PaneActivated?.Invoke(this, pane);
    }

    /// <summary>刷新链结束（参数 = 该次刷新是否导航加载）：视图据此决定播不播入场动画。</summary>
    public event EventHandler<bool>? RefreshCompleted;

    /// <summary>视图请求：为当前选中行弹右键菜单（Shift+F10 / 菜单键）。</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>视图请求：把某行滚入视口（跳转/键盘移动后）。</summary>
    public event EventHandler<TrashRowViewModel>? FocusRowRequested;

    /// <summary>视图请求：打开只读详情页（视图提供覆盖层）。</summary>
    public event EventHandler<TrashRowViewModel>? ShowLinkDetailRequested;

    /// <summary>只读详情**覆盖层**的共享页模型（<c>Views.LinkDetailPane</c>——与浏览页详情页同一份界面）。</summary>
    public TrashDetailPaneModel DetailPane { get; }

    /// <summary>
    /// 右栏只读详情栏（共享 <see cref="Views.DetailSidebar"/> 的数据源）：与浏览页 <c>BrowserViewModel.Details</c>
    /// **同构**——它是**选中集合的投影**，在唯一投影点 <see cref="ApplySelectionToView"/> 里按选中项数重建
    /// （空占位 / 单选详情 / 多选计数），视图只做绑定，绝不另持一份状态。
    /// </summary>
    public TrashSidebarModel Details { get; }

    // ================= 选中（唯一事实来源 + 投影） =================

    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private string? _anchorId;

    /// <summary>本行/节点是否在选中集合（行与树都是它的只读投影）。</summary>
    public bool IsSelectedId(string id) => _selectedIds.Contains(id);

    public int SelectionCount => _selectedIds.Count;
    public bool HasSelection => _selectedIds.Count > 0;

    /// <summary>选中提示文案（状态栏药丸）：单选显示名称、多选显示项数。</summary>
    public string SelectionInfoText => SelectionCount == 1
        ? (Rows.FirstOrDefault(r => _selectedIds.Contains(r.Id))?.Name ?? "已选中 1 项")
        : $"已选中 {SelectionCount} 项";

    /// <summary>当前选中行（主栏视角；顺序 = 行序）。</summary>
    public IEnumerable<TrashRowViewModel> SelectedRows => Rows.Where(r => _selectedIds.Contains(r.Id));

    /// <summary>选中投影到行 + 树 + 右栏详情栏（唯一写入入口之后的唯一投影点）。</summary>
    private void ApplySelectionToView()
    {
        foreach (var r in Rows) r.InvalidateIsSelected();
        foreach (var node in AllTreeNodes()) node.InvalidateIsSelected();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionInfoText));
        ProjectDetails();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>选中 → 右栏详情栏投影：空占位 / 单选详情 / 多选计数（同一投影点，视图不另设刷新入口）。</summary>
    private void ProjectDetails()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0) Details.Clear();
        else if (selected.Count == 1) Details.Show(selected[0]);
        else Details.ShowMulti(selected);
    }

    /// <summary>写入选中集合的**唯一入口**（覆盖式）：空集合 = 清空。</summary>
    public void SetSelection(IEnumerable<string> ids)
    {
        _selectedIds.Clear();
        foreach (var id in ids) _selectedIds.Add(id);
        ApplySelectionToView();
    }

    public void ClearSelection() => SetSelection(Array.Empty<string>());

    /// <summary>行点击选择（Ctrl 翻转 / Shift 区间；无修饰键 = 单选）。</summary>
    public void SelectRowWithModifiers(TrashRowViewModel row, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control)
        {
            var next = new HashSet<string>(_selectedIds, StringComparer.Ordinal);
            if (!next.Remove(row.Id)) next.Add(row.Id);
            SetSelection(next);
            _anchorId = row.Id;
            return;
        }

        if (modifiers == ModifierKeys.Shift && _anchorId != null)
        {
            var ids = Rows.Select(r => r.Id).ToList();
            var from = ids.IndexOf(_anchorId);
            var to = ids.IndexOf(row.Id);
            if (from >= 0 && to >= 0)
            {
                SetSelection(ids.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1));
                return;
            }
        }

        SetSelection(new[] { row.Id });
        _anchorId = row.Id;
    }

    public void SelectAllRows() => SetSelection(Rows.Select(r => r.Id));

    // ================= 快照与加载 =================

    private List<TrashFolderDto> _unitList = new();
    private Dictionary<string, TrashFolderDto> _unitById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrashEntryDto>> _linksByUnit = new(StringComparer.Ordinal);

    /// <summary>排序口径（默认删除时间倒序，用户定稿）；列头点击切换。</summary>
    public string SortField { get; private set; } = "deleted_at";
    public bool SortAscending { get; private set; }

    private bool _loadPending;
    private bool _loadPendingNavigating;

    /// <summary>
    /// 重新拉取快照并重建视图（树 / 主栏 / 面包屑 / 选中与落点投影）。
    /// <paramref name="navigating"/> = 用户发起的加载（F5 / 切页进入）：亮遮罩与入场动画；
    /// 事件驱动的后台刷新一律静默（与浏览页同口径）。
    /// </summary>
    public async Task LoadAsync(bool navigating = false)
    {
        if (IsLoading)
        {
            _loadPending = true;   // 重入守卫：加载中又来请求 → 收尾补刷（navigating 按最后一次计）
            _loadPendingNavigating |= navigating;
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        if (navigating) IsNavigating = true;
        var wasNavigation = navigating;
        var selectedIds = _selectedIds.ToList();      // 刷新保留选中（按 ID 重新投影；已删 ID 自动消失）

        try
        {
            var snapshot = await _client.TrashOverviewAsync();
            _unitList = snapshot.Folders;
            _unitById = snapshot.Folders.ToDictionary(f => f.TrashFolderId, StringComparer.Ordinal);
            _linksByUnit.Clear();
            foreach (var l in snapshot.Links)
            {
                var key = l.TrashFolderId ?? string.Empty;
                if (!_linksByUnit.TryGetValue(key, out var list)) _linksByUnit[key] = list = new List<TrashEntryDto>();
                list.Add(l);
            }
            _allLinks = snapshot.Links;

            // 当前位置已不存在（外部清空/永久删除）→ 回根，避免"停在一个不存在的单元里"
            if (CurrentUnitId != null && !_unitById.ContainsKey(CurrentUnitId))
                Controller.NavigateTo(null);

            RebuildTree();
            RebuildRows();
            RebuildBreadcrumbs();
            RemoveMissingFromSelection(selectedIds);
            // 详情覆盖层的条目已被还原/永久删除 → 覆盖层自动关闭（"条目消失即关"，状态在 VM、视图不持）
            if (IsDetailOverlayOpen && (_detailLinkId == null || !_allLinks.Any(l => l.Id == _detailLinkId)))
                CloseDetailOverlay();
            SetStatusText();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载回收站失败: {ex.Message}";
            StatusText = ErrorMessage;
            Logger.Error("回收站加载失败", ex);   // 观测面：失败必须留痕
        }
        finally
        {
            IsLoading = false;
            if (navigating) IsNavigating = false;
            CommandManager.InvalidateRequerySuggested();
            RefreshCompleted?.Invoke(this, wasNavigation);
            if (_loadPending)
            {
                _loadPending = false;
                var pendingNavigating = _loadPendingNavigating;
                _loadPendingNavigating = false;
                await LoadAsync(pendingNavigating);
            }
        }
    }

    private List<TrashEntryDto> _allLinks = new();

    /// <summary>全部链接快照（详情/提示用）。</summary>
    internal IReadOnlyList<TrashEntryDto> AllLinks => _allLinks;

    /// <summary>刷新后清理已不存在的选中 ID（被永久删除的条目不得残留选中态）。</summary>
    private void RemoveMissingFromSelection(IReadOnlyList<string> previous)
    {
        if (previous.Count == 0) return;
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in _unitList) alive.Add(f.TrashFolderId);
        foreach (var l in _allLinks) alive.Add(l.Id);
        SetSelection(previous.Where(alive.Contains));
    }

    /// <summary>
    /// 重建树：虚根「回收站」（**恒展开**）+ 单元层级（环保护：坏数据兜底挂根）+ 各节点直接链接叶子。
    /// 排序与浏览页同口径：**单元组在前、链接组在后，各自名称升序**；展开态按用户记忆恢复。
    /// </summary>
    private void RebuildTree()
    {
        var expanded = CollectExpandedIds(Tree);
        Tree.Clear();

        var rootLinks = _linksByUnit.TryGetValue(string.Empty, out var rootLinkList) ? rootLinkList.Count : 0;
        var root = new TrashNode
        {
            IsRoot = true,
            Name = RootDisplayName,
            Host = this,
            IsExpanded = true,   // 虚根恒展开（与浏览页同口径）
            LinkCount = rootLinks,
        };
        var nodeById = new Dictionary<string, TrashNode>(StringComparer.Ordinal);
        foreach (var f in _unitList)
        {
            nodeById[f.TrashFolderId] = new TrashNode
            {
                Id = f.TrashFolderId,
                ParentId = f.ParentTrashFolderId,
                Name = f.Name,
                LinkCount = f.LinkCount,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
                IsExpanded = expanded.Contains(f.TrashFolderId),
                Host = this,
            };
        }

        foreach (var f in _unitList)
        {
            var node = nodeById[f.TrashFolderId];
            if (f.ParentTrashFolderId != null
                && nodeById.TryGetValue(f.ParentTrashFolderId, out var parent)
                && !IsSelfInLineage(f.ParentTrashFolderId, f.TrashFolderId))
            {
                parent.Children.Add(node);
            }
            else
            {
                root.Children.Add(node);   // 根级 / 父缺失 / 成环坏数据：兜底挂根（不死循环、不凭空消失）
            }
        }

        // 链接叶子：挂到归属单元（null → 根）；归属单元缺失（坏数据）同样兜底挂根
        foreach (var (key, links) in _linksByUnit)
        {
            var target = key.Length == 0 ? root
                : nodeById.TryGetValue(key, out var unitNode) ? unitNode : root;
            foreach (var l in links) target.Children.Add(ToLinkNode(l));
        }

        SortChildren(root);
        Tree.Add(root);
    }

    /// <summary>排序投影（单元在前、链接在后，各自名称升序）——递归应用。</summary>
    private static void SortChildren(TrashNode node)
    {
        var sorted = node.Children
            .OrderBy(n => n.IsLink ? 1 : 0)
            .ThenBy(n => n.Name, StringComparer.CurrentCulture)
            .ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }

    private TrashNode ToLinkNode(TrashEntryDto link) => new()
    {
        Id = link.Id,
        ParentId = link.TrashFolderId,
        Name = string.IsNullOrEmpty(link.Name) ? (link.Url ?? string.Empty) : link.Name,
        IsLink = true,
        Host = this,
    };

    /// <summary>沿 parent 链向上查找：self 是否出现在祖先链中（是 = 成环坏数据）。guard 防不死循环。</summary>
    private bool IsSelfInLineage(string? start, string self)
    {
        var cur = start;
        for (var guard = 0; cur != null && guard < 64; guard++)
        {
            if (cur == self) return true;
            if (!_unitById.TryGetValue(cur, out var p)) return false;
            cur = p.ParentTrashFolderId;
        }
        return false;
    }

    private HashSet<string> CollectExpandedIds(IEnumerable<TrashNode> nodes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Id.Length > 0) ids.Add(node.Id);
            ids.UnionWith(CollectExpandedIds(node.Children));
        }
        return ids;
    }

    /// <summary>树的所有节点（含虚根与链接叶子；深度优先）。</summary>
    internal IEnumerable<TrashNode> AllTreeNodes()
    {
        foreach (var node in Tree) foreach (var n in Walk(node)) yield return n;
        static IEnumerable<TrashNode> Walk(TrashNode node)
        {
            yield return node;
            foreach (var child in node.Children) foreach (var n in Walk(child)) yield return n;
        }
    }

    /// <summary>
    /// 重建主栏：当前位置的直接内容（子单元 + 直接链接），按当前排序投影。
    /// 导航 = 快照内过滤（不逐单元取数）；深层内容随子单元再打开（与契约口径一致）。
    /// </summary>
    private void RebuildRows()
    {
        var loc = CurrentUnitId;
        var rows = new List<TrashRowViewModel>();

        foreach (var f in _unitList.Where(f => f.ParentTrashFolderId == loc))
        {
            rows.Add(new TrashRowViewModel(f.TrashFolderId, isFolder: true, f.Name)
            {
                LinkCount = f.LinkCount,
                Description = f.Description,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
                Host = this,
            });
        }

        var key = loc ?? string.Empty;
        if (_linksByUnit.TryGetValue(key, out var links))
        {
            foreach (var l in links)
            {
                rows.Add(new TrashRowViewModel(l.Id, isFolder: false,
                    string.IsNullOrEmpty(l.Name) ? (l.Url ?? string.Empty) : l.Name)
                {
                    Url = l.Url,
                    Description = l.Description,
                    FaviconUrl = l.FaviconUrl,
                    OriginPath = l.OriginPath,
                    DeletedAt = l.DeletedAt,
                    Host = this,
                });
            }
        }

        SortRows(rows);

        Rows.Clear();
        foreach (var r in rows)
        {
            if (!r.IsFolder) r.Favicon = FaviconService.LoadFromCache(r.FaviconUrl);
            Rows.Add(r);
        }

        ApplySelectionToView();
        OnPropertyChanged(nameof(HasRows));
    }

    public bool HasRows => Rows.Count > 0;

    /// <summary>列头排序（快照内排序；默认删除时间倒序）：重排同一批数据、保留选中与所在位置。</summary>
    public void ApplySort(string field, bool ascending)
    {
        SortField = string.IsNullOrEmpty(field) ? "deleted_at" : field;
        SortAscending = ascending;
        RebuildRows();
    }

    private void SortRows(List<TrashRowViewModel> rows)
    {
        var sign = SortAscending ? 1 : -1;
        Comparison<TrashRowViewModel> byField = SortField switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) * sign,
            "type" => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCulture) * sign,
            "origin_path" => (a, b) => string.Compare(a.OriginText, b.OriginText, StringComparison.CurrentCulture) * sign,
            _ => (a, b) => a.DeletedAt.CompareTo(b.DeletedAt) * sign,
        };
        rows.Sort((a, b) =>
        {
            // 回收站主栏 = 平铺口径（单元与链接混排，保留 Windows 回收站语义：按删除时间倒序等）；
            // 分组只存在于左栏树（单元在前、链接在后），主栏不分组——用户定稿 2026-09-19
            var byValue = byField(a, b);
            return byValue != 0 ? byValue : string.CompareOrdinal(a.Id, b.Id);   // ID 兜底（次序确定）
        });
    }

    private void SetStatusText()
    {
        var where = IsInUnit ? CurrentUnitDisplayName : RootDisplayName;
        StatusText = $"{where} · {Rows.Count} 项";
        OnPropertyChanged(nameof(CurrentUnitDisplayName));
    }

    // ================= 导航 =================

    /// <summary>
    /// 导航到某单元（null = 回收站根）：本地过滤 + 历史栈；**点是当前位置 = 无条件重载一次**
    /// （Windows 直觉，与浏览页同口径）。
    /// </summary>
    public Task NavigateAsync(string? unitId)
    {
        if (unitId != null && !_unitById.ContainsKey(unitId))
        {
            StatusText = "该单元已不存在";
            return Task.CompletedTask;
        }

        if (unitId == CurrentUnitId)
            return LoadAsync(navigating: true);   // 点是当前位置：重载刷新一次

        Controller.NavigateTo(unitId);
        RebuildRows();
        RebuildBreadcrumbs();
        SetStatusText();
        CommandManager.InvalidateRequerySuggested();
        RefreshCompleted?.Invoke(this, true);
        return Task.CompletedTask;
    }

    /// <summary>取某单元的父单元（根 = null）。</summary>
    public string? GetParentId(string? unitId)
        => unitId != null && _unitById.TryGetValue(unitId, out var f) ? f.ParentTrashFolderId : null;

    /// <summary>当前位置链（根之后逐级，用于地址栏文本）。</summary>
    private List<(string Id, string Name)> BuildPathChain()
    {
        var path = new List<(string, string)>();
        var cur = CurrentUnitId;
        for (var guard = 0; cur != null && guard < 64; guard++)
        {
            if (!_unitById.TryGetValue(cur, out var f)) break;
            path.Insert(0, (f.TrashFolderId, f.Name));
            cur = f.ParentTrashFolderId;
        }
        return path;
    }

    private void RebuildBreadcrumbs()
    {
        var chain = new List<TrashCrumbViewModel> { new(null, RootDisplayName) };
        foreach (var (id, name) in BuildPathChain()) chain.Add(new TrashCrumbViewModel(id, name));

        Breadcrumbs.Clear();
        for (var i = 0; i < chain.Count; i++)
            Breadcrumbs.Add(new TrashCrumbViewModel(chain[i].UnitId, chain[i].Name) { IsLast = i == chain.Count - 1 });

        OnPropertyChanged(nameof(IsInUnit));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CurrentUnitDisplayName));
    }

    // ================= 打开（双击 / Enter / 树节点单击） =================

    private async Task OpenRowAsync(TrashRowViewModel? row)
    {
        if (row == null) return;
        if (row.IsFolder)
        {
            SetSelection(new[] { row.Id });
            await NavigateAsync(row.Id);
            return;
        }
        ShowLinkDetailRequested?.Invoke(this, row);
    }

    /// <summary>打开只读详情覆盖层（共享详情页）：选中该行（与浏览页打开详情同口径）+ 填共享面 + 置开页标志。</summary>
    public void OpenLinkDetail(TrashRowViewModel row)
    {
        SetSelection(new[] { row.Id });
        _detailLinkId = row.Id;
        DetailPane.Show(row);
        IsDetailOverlayOpen = true;
    }

    /// <summary>详情覆盖层当前展示的条目 ID（刷新收尾据此判断"条目消失即关"）。</summary>
    private string? _detailLinkId;

    private void CloseDetailOverlay()
    {
        _detailLinkId = null;
        IsDetailOverlayOpen = false;
    }

    private void CopyDetailUrl()
    {
        try
        {
            if (!string.IsNullOrEmpty(DetailPane.Url)) System.Windows.Clipboard.SetText(DetailPane.Url);
        }
        catch { /* 剪贴板被占用时不阻断 */ }
    }

    private void CopyDetailId()
    {
        try
        {
            if (!string.IsNullOrEmpty(_detailLinkId)) System.Windows.Clipboard.SetText(_detailLinkId);
        }
        catch { /* 剪贴板被占用时不阻断 */ }
    }

    /// <summary>详情覆盖层的「打开网站」：即使是已废弃（在回收站里）的条目也应能打开；
    /// **不记访问**（回收站条目不在主表，没有访问统计口径）。</summary>
    private void OpenDetailWebsite()
    {
        var url = DetailPane.Url;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开网站失败（静默返回，页面保持）", ex);   // 观测面：失败留痕
        }
    }

    /// <summary>树节点被点击（视图转发；与浏览页 SelectTreeNodeAsync 同口径）：单元 = 选中 + 进入；链接叶子 = 主栏定位选中；虚根 = 回根。</summary>
    public Task SelectTreeNodeAsync(TrashNode node) => OpenNodeAsync(node);

    /// <summary>树节点动作（与浏览页同一路径语义）：单元 = 选中 + 进入；链接叶子 = 主栏定位选中；虚根 = 回根。</summary>
    private async Task OpenNodeAsync(TrashNode? node)
    {
        if (node == null) return;
        if (node.IsRoot)
        {
            await NavigateAsync(null);   // 虚根：回根（不写选中——根不是实体）
            return;
        }
        if (node.IsLink)
        {
            await LocateLinkAsync(node);
            return;
        }
        SetSelection(new[] { node.Id });
        await NavigateAsync(node.Id);
    }

    /// <summary>定位链接：进其所属单元 + 主栏选中该行（与浏览页"跳转"语义同口径；已在则不重载）。</summary>
    private Task LocateLinkAsync(TrashNode link)
    {
        var parent = link.ParentId;   // null = 根
        if (parent != CurrentUnitId)
        {
            Controller.NavigateTo(parent);
            RebuildRows();
            RebuildBreadcrumbs();
            SetStatusText();
        }
        SetSelection(new[] { link.Id });
        var row = Rows.FirstOrDefault(r => r.Id == link.Id);
        if (row != null) FocusRowRequested?.Invoke(this, row);
        return Task.CompletedTask;
    }

    private async Task OpenSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await OpenRowAsync(row);
    }

    // ================= 键盘（主栏 / 左栏） =================

    private static int ParseDirection(object? parameter)
        => parameter as string == "up" ? -1 : 1;

    /// <summary>主栏 ↑/↓：移动选中（单选）+ 滚入视口；到边界**停住**；无选中时从顶/底开始。</summary>
    private void MoveMainSelection(int delta)
    {
        if (Rows.Count == 0 || delta == 0) return;
        var current = SelectedRows.Select(r => Rows.IndexOf(r)).Where(i => i >= 0).OrderBy(i => i).LastOrDefault(-1);
        var next = current < 0
            ? (delta > 0 ? 0 : Rows.Count - 1)
            : Math.Clamp(current + delta, 0, Rows.Count - 1);
        var target = Rows[next];
        SetSelection(new[] { target.Id });
        _anchorId = target.Id;
        FocusRowRequested?.Invoke(this, target);
    }

    private void SelectLastRow()
    {
        if (Rows.Count == 0) return;
        var target = Rows[^1];
        SetSelection(new[] { target.Id });
        FocusRowRequested?.Invoke(this, target);
    }

    /// <summary>左栏 ↑/↓：按**可见视觉顺序**移动；落点语义与鼠标点树完全同一条路径（与浏览页同口径）。</summary>
    private void MoveTreeSelection(int delta)
    {
        if (delta == 0) return;
        var flat = VisibleTreeNodes().ToList();
        if (flat.Count == 0) return;

        var current = CurrentFocusedTreeNode() is { } focused ? flat.IndexOf(focused) : -1;
        var next = current < 0
            ? (delta > 0 ? 0 : flat.Count - 1)
            : Math.Clamp(current + delta, 0, flat.Count - 1);
        var target = flat[next];
        _treeNavNode = target;   // 下一次移动从这次落点继续（游标独立于选中/位置）
        _ = OpenNodeAsync(target);
    }

    private void ToggleFocusedTreeExpand()
    {
        var node = CurrentFocusedTreeNode();
        if (node == null || node.IsLink || node.IsRoot) return;
        node.IsExpanded = !node.IsExpanded;
    }

    /// <summary>键盘游标（节点对象引用；虚根无 ID、只有引用能表示它——与浏览页同法；树重建后旧引用自然失效）。</summary>
    private TrashNode? _treeNavNode;

    /// <summary>当前聚焦的树节点（与浏览页同优先级：键盘游标 → 选中实体 → 当前所在单元/虚根）。</summary>
    private TrashNode? CurrentFocusedTreeNode()
    {
        var all = AllTreeNodes().ToList();
        if (_treeNavNode != null && all.Contains(_treeNavNode)) return _treeNavNode;

        foreach (var node in all)
        {
            if (node.IsRoot) continue;
            if (_selectedIds.Contains(node.Id)) return node;
        }

        var currentId = CurrentUnitId;
        foreach (var node in all)
            if (currentId == null ? node.IsRoot : node.Id == currentId) return node;
        return null;
    }

    /// <summary>树节点的**可见**深度优先序列（未展开节点的子级不在序列内——没看到就不进入）。</summary>
    internal IEnumerable<TrashNode> VisibleTreeNodes()
    {
        foreach (var root in Tree)
            foreach (var node in Walk(root))
                yield return node;

        static IEnumerable<TrashNode> Walk(TrashNode node)
        {
            yield return node;
            if (!node.IsExpanded) yield break;
            foreach (var child in node.Children)
                foreach (var n in Walk(child))
                    yield return n;
        }
    }

    // ================= 地址栏（面包屑内联路径编辑；与浏览页同口径，解析器共享） =================

    private bool _isDetailOverlayOpen;
    /// <summary>只读详情覆盖层是否打开（视图回写）：打开时**处置动作一律让位**——
    /// 永久删除/还原这类危险键不得作用于"被覆盖层挡住、看不见"的选中（与浏览页详情页的门控对称）。</summary>
    public bool IsDetailOverlayOpen
    {
        get => _isDetailOverlayOpen;
        set
        {
            if (_isDetailOverlayOpen == value) return;
            _isDetailOverlayOpen = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isPathEditing;
    public bool IsPathEditing
    {
        get => _isPathEditing;
        set
        {
            if (_isPathEditing == value) return;
            _isPathEditing = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _pathEditText = string.Empty;
    public string PathEditText
    {
        get => _pathEditText;
        set
        {
            if (_pathEditText == value) return;
            _pathEditText = value;
            OnPropertyChanged();
            IsPathInvalid = false;
            UpdatePathCandidates();
        }
    }

    private bool _isPathInvalid;
    public bool IsPathInvalid
    {
        get => _isPathInvalid;
        set { if (_isPathInvalid != value) { _isPathInvalid = value; OnPropertyChanged(); } }
    }

    private List<string> _pathCandidates = new();
    public List<string> PathCandidates
    {
        get => _pathCandidates;
        private set { _pathCandidates = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPathCandidates)); }
    }

    public bool HasPathCandidates => IsPathEditing && _pathCandidates.Count > 0;

    private int _selectedCandidateIndex = -1;
    public int SelectedCandidateIndex
    {
        get => _selectedCandidateIndex;
        set { if (_selectedCandidateIndex != value) { _selectedCandidateIndex = value; OnPropertyChanged(); } }
    }

    /// <summary>路径解析/候选（唯一实现在 UIKit Views.PathResolver；本页只提供被删单元层级数据源）。</summary>
    private Views.PathResolver? _pathResolver;
    private Views.PathResolver Paths => _pathResolver ??= new Views.PathResolver(
        RootDisplayName,
        parentId => _unitList
            .Where(f => f.ParentTrashFolderId == parentId)
            .Select(f => new Views.PathNode(f.TrashFolderId, f.Name))
            .ToList());

    private void EnterPathEdit()
    {
        PathEditText = Paths.BuildText(BuildPathChain());
        IsPathInvalid = false;
        IsPathEditing = true;
    }

    private void CancelPathEdit()
    {
        IsPathEditing = false;
        PathCandidates = new List<string>();
    }

    /// <summary>Enter：逐级按名解析路径（根段名「回收站」被跳过）。失败 → 输入区标红并提示。</summary>
    private void ConfirmPath()
    {
        if (Paths.TryResolve(PathEditText, out var unitId, out var invalidSegment))
        {
            IsPathEditing = false;
            PathCandidates = new List<string>();
            _ = NavigateAsync(unitId);
        }
        else
        {
            IsPathInvalid = true;
            StatusText = $"路径不存在：{invalidSegment}";
        }
    }

    /// <summary>Tab：用当前候选补全最后一级。</summary>
    private void CompletePath()
    {
        if (SelectedCandidateIndex >= 0 && SelectedCandidateIndex < PathCandidates.Count)
            ChooseCandidate(PathCandidates[SelectedCandidateIndex]);
    }

    /// <summary>选择候选（点击或 Tab）：改写文本后保留编辑态，继续输入下一级。</summary>
    public void ChooseCandidate(string name)
    {
        PathEditText = Views.PathResolver.ApplyCandidate(PathEditText ?? string.Empty, name);   // 候选名含 / 时同样转义写入
        SelectedCandidateIndex = 0;
    }

    /// <summary>↑/↓ 移动候选高亮（由视图键盘事件调用）。</summary>
    public void MoveCandidate(int delta)
    {
        if (PathCandidates.Count == 0) return;
        SelectedCandidateIndex = Math.Clamp(SelectedCandidateIndex + delta, 0, PathCandidates.Count - 1);
    }

    private void UpdatePathCandidates()
    {
        PathCandidates = Paths.Candidates(_pathEditText ?? string.Empty).ToList();
        SelectedCandidateIndex = PathCandidates.Count > 0 ? 0 : -1;
    }

    // ================= 右键菜单（Shift+F10 / 菜单键） =================

    private TrashRowViewModel? _contextRow;

    /// <summary>右键命中的行（永久删除文案按"这一次会删掉什么"算）。</summary>
    public void SetContextRow(TrashRowViewModel? row) => _contextRow = row;

    private void ShowContextMenuForSelection()
    {
        if (SelectedRows.FirstOrDefault() is not { } row) return;
        _contextRow = row;
        FocusRowRequested?.Invoke(this, row);
        ContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>复制链接地址（只复制 URL 文本，不涉及条目剪贴板——回收站无剪贴板搬运）。</summary>
    private static void CopyLinkAddress(TrashRowViewModel? row)
    {
        if (row is not { IsFolder: false } || string.IsNullOrEmpty(row.Url)) return;
        try { System.Windows.Clipboard.SetText(row.Url); } catch { /* 剪贴板被占用时不阻断 */ }
    }

    // ================= 通知 =================

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>回收站树没有改名：为面板模板绑定面提供恒空占位（防绑定噪音）。</summary>
    public string EditingName
    {
        get => string.Empty;
        set { }
    }

    /// <summary>回收站树没有改名：恒禁用的提交/取消命令占位（面板模板绑定面）。</summary>
    public ICommand CommitRenameCommand { get; } = new RelayCommand(() => { }, () => false);
    public ICommand CancelRenameCommand { get; } = new RelayCommand(() => { }, () => false);

    // ================= 命令（公开面） =================

    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CrumbClickCommand { get; }
    public ICommand OpenRowCommand { get; }
    public ICommand OpenNodeCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    public ICommand PurgeSelectionCommand { get; }
    public ICommand PurgeNodeCommand { get; }
    public ICommand RestoreSelectionCommand { get; }
    public ICommand RestoreSelectionToRootCommand { get; }
    public ICommand RestoreNodeCommand { get; }
    public ICommand RestoreNodeToRootCommand { get; }
    public ICommand ShowContextMenuCommand { get; }
    public ICommand CopyLinkAddressCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand MoveSelectionCommand { get; }
    public ICommand SelectLastCommand { get; }
    public ICommand MoveTreeSelectionCommand { get; }
    public ICommand ToggleTreeExpandCommand { get; }
    public ICommand EnterPathEditCommand { get; }
    public ICommand ConfirmPathCommand { get; }
    public ICommand CancelPathEditCommand { get; }
    public ICommand CompletePathCommand { get; }
    public ICommand CloseDetailOverlayCommand { get; }
    public ICommand CopyDetailUrlCommand { get; }
    public ICommand CopyDetailIdCommand { get; }
    /// <summary>详情覆盖层：打开网站 / 还原 / 还原到根目录 / 永久删除（作用对象 = 当前展示项）。</summary>
    public ICommand OpenDetailWebsiteCommand { get; }
    public ICommand RestoreDetailCommand { get; }
    public ICommand RestoreDetailToRootCommand { get; }
    public ICommand PurgeDetailCommand { get; }
    /// <summary>点空白清选中：列表卡（主栏获得键盘语义归属）/ 页面其它空白（保持当前栏归属）。</summary>
    public ICommand ClearMainPaneSelectionCommand { get; }
    public ICommand ClearPageSelectionCommand { get; }
}

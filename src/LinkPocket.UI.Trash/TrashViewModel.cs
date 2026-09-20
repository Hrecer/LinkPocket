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

        // 选中集合（共享 ListSelection 核心）变化 → 唯一的投影点（行 + 树 + 右栏 + 命令可用性）
        Selection.Changed += ApplySelectionToView;

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
        // Esc（分层，用户令 2026-09-19）：只读详情覆盖层打开 → 先退出覆盖层；否则清空选中。
        EscapeCommand = new RelayCommand(Escape);
        // 右栏「跳转」= 把选中的那一行**滚回视口**（本页内定位）。仅单选（多选没有"某一项"可定位）。
        JumpSelectionCommand = new RelayCommand(JumpSelectionToRow, () => Selection.Count == 1);
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
        Details.JumpCommand = JumpSelectionCommand;   // 右栏「跳转」= 本页内把选中行滚回视口（同一命令实例）

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
            CommandRefresh.Request();
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
            CommandRefresh.Request();
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
    /// <summary>Esc：覆盖层打开 → 退出覆盖层；否则取消选中（键位在 ShortcutCatalog）。</summary>
    public ICommand EscapeCommand { get; }
    /// <summary>右栏「跳转」：把选中的那一行滚回视口（同页内定位；仅单选可用）。</summary>
    public ICommand JumpSelectionCommand { get; }
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

    /// <summary>
    /// 右栏「跳转」：把选中的那一行**滚回视口**（同页内定位）。
    /// 回收站条目不在主表（引擎 `locate.resolve` 查不到它），所以本页的"跳转"不是"跳去浏览页"，而是
    /// "把视角移回那一项"——复用与浏览页 / 结果页跳转**同一个视图原语** <see cref="FocusRowRequested"/>
    /// （视图侧落到 `ScrollItemIntoView`），不新增第二套定位实现。
    /// 用户令 2026-09-20：一页上百项时"选中了又滑走要找别的项"要能一键回到它。
    /// </summary>
    private void JumpSelectionToRow()
    {
        if (SelectedRows.FirstOrDefault() is not { } row) return;
        FocusRowRequested?.Invoke(this, row);
    }
}

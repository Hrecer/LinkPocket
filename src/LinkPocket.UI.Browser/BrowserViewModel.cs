using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>浏览页的三处落点区：主栏（目录内容列表）/ 左栏（文件夹树）/ 顶部面包屑地址栏。
/// 键盘语义按栏归属——例如 ↑/↓ 在主栏与左栏含义不同（主栏移动行选中；左栏按树的视觉顺序移动）。</summary>
public enum BrowserPane
{
    /// <summary>主栏（内容列表）。</summary>
    Main,
    /// <summary>左栏（文件夹树）。</summary>
    Tree,
    /// <summary>顶部面包屑地址栏（拖放落点；Windows 11 口径：路径段可接收拖来的文件）。</summary>
    Breadcrumb
}

/// <summary>
/// 资源管理器式浏览页（P4）：一切数据经引擎查询命令（folders.contents 等）获取，
/// 渲染由 XAML ItemsControl + DataTemplate 完成，本类不持有任何控件引用。
///
/// <para>传输（移动/复制）的**唯一实现**在 `BrowserViewModel.Transfer.cs`（partial）：
/// 拖拽落点、右键拖拽菜单、剪贴板粘贴三个入口共用同一条流水线，这里不再有第二份逐项循环。</para>
/// </summary>
public partial class BrowserViewModel : INotifyPropertyChanged
{
    /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
    private readonly EngineClient _client;

    /// <summary>UI 端口槽位（组合根持有；笔者端口：对话框/导航）。null = 无 UI 环境（无头/单测），弹窗退化系统 MessageBox。</summary>
    private readonly Services.UiPortProvider? _ports;

    /// <summary>刷新挂起标志：加载进行中又来刷新请求时置位，当前加载收尾后自动补刷一次（最后请求胜出）。</summary>
    private bool _refreshPending;
    /// <summary>挂起补刷是否要求清空选中（导航语义）：进入目录时触发，被挂起的原地刷新不可误清。</summary>
    private bool _clearSelectionOnPendingRefresh;

    /// <summary>补刷递归深度：最后一次补刷的 finally 里自身再次触发最多 3 层，
    /// 超过说明数据在持续高频变动——此时放弃"必处理"承诺，交还给 300ms 事件防抖继续追平，杜绝无限递归。</summary>
    private int _refreshRecursionDepth;

    /// <summary>补刷递归深度上限（超过后不再递归补刷）。</summary>
    private const int MaxRefreshRecursion = 3;

    public NavigationHistory Controller { get; } = new();

    public ObservableCollection<BrowserRowViewModel> Rows { get; } = new();
    public ObservableCollection<BrowserCrumbViewModel> Breadcrumbs { get; } = new();

    /// <summary>左侧文件夹树：虚拟根节点"全部书签"（IsRoot，无 FolderId）+ 各级子文件夹。</summary>
    public ObservableCollection<FolderNode> FolderTree { get; } = new();

    /// <summary>当前目录 ID（null = 根目录），供左侧树同步选中态。</summary>
    private string? _currentFolderId;
    public string? CurrentFolderId
    {
        get => _currentFolderId;
        private set { _currentFolderId = value; OnPropertyChanged(); }
    }

    private LocValue _statusText = Loc.K("status.ready");
    public LocValue StatusText
    {
        get => _statusText;
        set { if (!_statusText.Equals(value)) { _statusText = value; OnPropertyChanged(); } }
    }

    private bool _isLoading;
    /// <summary>刷新在途标志（同时是 RefreshAsync 的重入守卫）：任何刷新都会置位，含后台事件刷新。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _isDeleting;
    /// <summary>
    /// 删除在途标志：命令可用性据此置灰，删除入口再挡一次。
    /// </summary>
    /// <remarks>
    /// 删除是"每项一条命令"的循环写；连点/连发时第二次会对着已被移入回收站的行再删一次，
    /// 逐项报失败（界面在成功之后又说失败）。第二套防护在 <c>DeleteItemsAsync</c> 入口。
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
    /// 导航加载（界面加载遮罩的唯一来源）：**只有用户发起的导航/刷新**才为 true——
    /// 打开文件夹、跳转定位、返回上级、后退/前进、F5；事件驱动的后台刷新一律静默（不闪动画）。
    /// 与 <see cref="IsLoading"/> 的区别：后者是"有没有刷新在途"（内部重入守卫），前者是"该次刷新是否显示加载态"。
    /// </summary>
    public bool IsNavigating
    {
        get => _isNavigating;
        private set { if (_isNavigating == value) return; _isNavigating = value; OnPropertyChanged(); }
    }

    /// <summary>挂起补刷是否属于导航加载（与 <see cref="_clearSelectionOnPendingRefresh"/> 同机制，逐轮继承）。</summary>
    private bool _navigatingOnPendingRefresh;

    /// <summary>当前刷新链（含挂起补刷）里是否出现过导航加载——链条结束一次性告知界面。</summary>
    private bool _navigatingInChain;

    /// <summary>
    /// 一次刷新链（含挂起补刷）**完成**时触发；参数 = 该链是否属于"导航加载"。
    /// 界面据此决定"行入场动画"播不播：只有打开文件夹这类导航才播，后台刷新（含写操作后的防抖刷新）静默——
    /// 绝不按"集合有没有变更"来播（曾在每次刷新都重播，出现"移动后那次刷新还有动画"）。
    /// </summary>
    public event EventHandler<bool>? RefreshCompleted;

    private BrowserPane _activePane = BrowserPane.Main;

    /// <summary>
    /// 当前活跃栏（键盘语义归属）：用户点击哪一栏，哪一栏就活跃（默认主栏）。
    /// 快捷键按栏路由的事实源（如 ↑/↓ 在两栏语义不同）；视图据 <see cref="PaneActivated"/> 把键盘焦点收进页面。
    /// </summary>
    public BrowserPane ActivePane
    {
        get => _activePane;
        private set { if (_activePane == value) return; _activePane = value; OnPropertyChanged(); }
    }

    /// <summary>某一栏被用户激活（点击行/空白）。视图订阅后把键盘焦点归位到页内。</summary>
    public event EventHandler<BrowserPane>? PaneActivated;

    /// <summary>激活某一栏：记录归属 + 通知视图（焦点归位）。任何"点击某栏"的入口都只调这一个方法。</summary>
    public void ActivatePane(BrowserPane pane)
    {
        ActivePane = pane;
        PaneActivated?.Invoke(this, pane);
    }

    /// <summary>
    /// 「列表上下文」是否活跃：**详情页 / 编辑器页打开时列表动作一律让位**——
    /// 撤销/重做/剪贴板/删除/新建/改名这些会改数据的键只在"确实处于浏览页整理文件"时可执行
    /// （危险键绝不能在任何别的上下文里被误触而不自知）。
    /// </summary>
    public bool IsListContextActive => !IsDetailPageOpen && !IsEditorPageOpen;

    // —— 链接详情页（全页覆盖层，参考链接页书签详情） ——

    public LinkDetailPageViewModel DetailPage { get; }

    private bool _isDetailPageOpen;
    /// <summary>链接详情页是否打开（打开时覆盖整个浏览模块内容区）。</summary>
    public bool IsDetailPageOpen
    {
        get => _isDetailPageOpen;
        private set
        {
            if (_isDetailPageOpen == value) return;
            _isDetailPageOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListContextActive));
            CommandRefresh.Request();   // 列表动作让位/复位（危险键门）
        }
    }

    /// <summary>打开链接详情页（行命令 / 右键菜单 / 右侧栏操作卡共用）。</summary>
    public async Task OpenDetailPageAsync(BrowserRowViewModel? row)
    {
        if (row == null || row.IsFolder) return;
        SelectRow(row);
        IsDetailPageOpen = true;
        await DetailPage.LoadAsync(row.Id);
    }

    /// <summary>关闭链接详情页（由 DetailPage VM 回调）。</summary>
    public void CloseDetailPage() => IsDetailPageOpen = false;

    /// <summary>
    /// 按链接 ID 直接打开详情页（无需该链接出现在当前目录的行里）。
    /// 供搜索页「跳转」使用——老「链接」页删除后，跳转目标改为本页详情页。
    /// </summary>
    public async Task OpenDetailPageByIdAsync(string linkId)
    {
        if (string.IsNullOrEmpty(linkId)) return;
        IsDetailPageOpen = true;
        await DetailPage.LoadAsync(linkId);
    }

    // —— 链接编辑器（新建/编辑共用，整页覆盖层，与详情页同层级设计） ——

    private LinkEditorViewModel? _editorPage;
    /// <summary>当前编辑器页实例（每次打开重建，区分新建/编辑）。</summary>
    public LinkEditorViewModel? EditorPage
    {
        get => _editorPage;
        private set { _editorPage = value; OnPropertyChanged(); }
    }

    private bool _isEditorPageOpen;
    /// <summary>编辑器页是否打开（打开时覆盖整个浏览模块内容区，位于详情页之上）。</summary>
    public bool IsEditorPageOpen
    {
        get => _isEditorPageOpen;
        private set
        {
            if (_isEditorPageOpen == value) return;
            _isEditorPageOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListContextActive));
            CommandRefresh.Request();   // 列表动作让位/复位（危险键门）
        }
    }

    /// <summary>新建链接：在当前目录创建（不再选择所属目录），打开整页编辑器。</summary>
    public void OpenEditorForCreate()
    {
        // 必须先置开页标志再建实例：编辑器 ctor 会 fire-and-forget 预填（编辑模式），
        // 其 await 续体可能同步 inline —— 若开页标志还没设，会被「用户已取消」守卫吞掉 → 字段空白（实测复现）。
        IsEditorPageOpen = true;
        EditorPage = new LinkEditorViewModel(_client, this, IsAtRoot() ? null : CurrentFolderId);
    }

    /// <summary>编辑链接：整页编辑器预填数据（不改变所属目录）。</summary>
    public void OpenEditorForEdit(string linkId)
    {
        IsEditorPageOpen = true;
        EditorPage = LinkEditorViewModel.ForEdit(_client, this, linkId);
    }

    /// <summary>关闭编辑器页（由编辑器 VM 回调）。</summary>
    public void CloseEditorPage() => IsEditorPageOpen = false;

    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RowClickCommand { get; }
    public ICommand RowOpenCommand { get; }
    public ICommand CrumbClickCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand RenameRowCommand { get; }
    /// <summary>链接行右键「编辑」：打开整页编辑器（URL / 名称 / 描述 / 图标）。</summary>
    public ICommand EditRowCommand { get; }
    public ICommand DeleteRowCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewLinkCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand RenameNodeCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand SelectAllCommand { get; }
    /// <summary>Esc（分层，Windows 口径）：有剪切态先取消剪切，否则清空选中（见 <see cref="HandleEscape"/>）。</summary>
    public ICommand EscapeCommand { get; }
    /// <summary>点空白清选中（BlankClick 附加行为按区域挂载）：列表卡 / 页面其它空白两档归属语义。</summary>
    public ICommand ClearMainPaneSelectionCommand { get; }
    public ICommand ClearPageSelectionCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand RenameSelectionCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    /// <summary>Alt+D / 点地址栏空白：聚焦地址栏（进编辑态，由视图聚焦并全选）。</summary>
    public ICommand EnterPathEditCommand { get; }
    /// <summary>F5：真刷新当前目录（主栏 + 左栏树，带加载动画）。</summary>
    public ICommand RefreshCommand { get; }
    /// <summary>Ctrl+E / Ctrl+F（Global）：切到搜索页。</summary>
    public ICommand NavigateToSearchCommand { get; }
    /// <summary>Ctrl+Shift+E：展开左栏树到当前所在位置（只展开，不选中）。</summary>
    public ICommand ExpandTreeToCurrentCommand { get; }
    /// <summary>Ctrl+Shift+C：复制当前目录路径（面包屑文本）。</summary>
    public ICommand CopyPathCommand { get; }
    /// <summary>主栏 ↑/↓：移动选中（单选）+ 滚入视口，到边界停住。</summary>
    public ICommand MoveSelectionCommand { get; }
    /// <summary>主栏 End：选中末项 + 滚入视口。</summary>
    public ICommand SelectLastCommand { get; }
    /// <summary>左栏 ↑/↓：按树的可见视觉顺序移动（文件夹 = 选中 + 进入；链接叶子 = 定位）。</summary>
    public ICommand MoveTreeSelectionCommand { get; }
    /// <summary>左栏 ←/→：折叠 / 展开当前树节点（链接叶子无操作）。</summary>
    public ICommand ToggleTreeExpandCommand { get; }
    /// <summary>Ctrl+Z / Ctrl+Y：撤销 / 重做最近一条可撤销命令。</summary>
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    /// <summary>Shift+F10 / 菜单键：对当前选中行弹出右键菜单（视图订阅 ContextMenuRequested 执行）。</summary>
    public ICommand ShowContextMenuCommand { get; }
    public ICommand ConfirmPathCommand { get; }
    public ICommand CancelPathEditCommand { get; }
    public ICommand CompletePathCommand { get; }

    // —— 多选（Windows 资源管理器语义：锚点 + Ctrl/Shift 修饰键）——
    // 核心 = 全站共享的 ListSelection（与回收站/搜索/智能列表/去重明细同一实现）

    /// <summary>主栏选中集合（**共享 ListSelection 核心**：唯一选中集合 + 锚点 + 修饰键语义）。
    /// 主栏行的 IsSelected 与目录树的叶子高亮都从它投影（见 <see cref="ApplySelectionToView"/> /
    /// <see cref="SyncTreeSelection"/>）：任何一次 Rows/Tree 重建都按该集合重放，
    /// 不依赖行对象引用存活、不依赖 TreeView 容器时序。点空白 = 清空此集合 = 主栏与树同时取消。</summary>
    public ListSelection Selection { get; } = new();

    /// <summary>目标 ID 当前是否处于选中集合（行投影与树叶子投影共用此判据）。</summary>
    public bool IsSelectedId(string? id) => Selection.Contains(id);

    public IEnumerable<BrowserRowViewModel> SelectedRows => Rows.Where(r => Selection.Contains(r.Id));
    public int SelectionCount => Rows.Count(r => Selection.Contains(r.Id));
    public bool HasSelection => SelectionCount > 0;
    public bool HasMultipleSelection => SelectionCount > 1;
    public LocValue SelectionInfoText => HasSelection ? Loc.K("count.selectedItems", SelectionCount) : LocValue.Empty;

    /// <summary>
    /// 右键命中的行（由视图在 ContextMenuOpening 时告知）。
    /// 删除文案必须按"这一次点下去会删掉什么"来算，所以需要知道命中的是哪一行。
    /// </summary>
    private BrowserRowViewModel? _contextRow;

    /// <summary>告知 VM 当前右键命中的行；null = 非行内菜单（空白区），文案退回选中集合项数。</summary>
    public void SetContextRow(BrowserRowViewModel? row)
    {
        if (ReferenceEquals(_contextRow, row)) return;
        _contextRow = row;
        OnPropertyChanged(nameof(DeleteMenuHeader));
    }

    // —— 对话框端口 ——
    // VM 不再直用 MessageBox / ConfirmDialog 静态入口；优先走 IDialogService 端口
    //（组合根注入 UiPortProvider，MainWindow 登记实现），无端口（无头/单测）时退化为系统弹窗，
    // 保证 VM 零控件依赖、行为等价。

    /// <summary>对话框端口（详情页等宿主内 VM 共用）。null = 无 UI 环境。</summary>
    internal Services.IDialogService? Dialogs => _ports?.Dialogs;

    /// <summary>错误提示：优先端口 Alert，无端口退化 MessageBox（与旧行为视觉一致）。</summary>
    private void ShowError(string title, string message)
    {
        var dlg = _ports?.Dialogs;
        if (dlg != null) { dlg.Alert(title, message); return; }
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>删除确认（Windows 口径「将 X 移入回收站吗？」）：优先端口 Confirm，无端口退化 ConfirmDialog 直用。</summary>
    private bool ConfirmDelete(string title, string message)
    {
        var dlg = _ports?.Dialogs;
        if (dlg != null) return dlg.Confirm(title, message);
        return Views.ConfirmDialog.Show(title, message, Loc.T("common.delete"));
    }

    /// <summary>
    /// 「删除」菜单文案，口径与 <see cref="DeleteRowAsync"/> 的删除目标严格一致，且只在"数字有意义"时才报数：
    /// 单个链接 / 空文件夹 → 只显示「删除」；单个文件夹 → 显示其内链接数（删除文件夹 = 其中链接进回收站）；
    /// 右键多选中的行 → 显示选中项数。
    /// </summary>
    public LocValue DeleteMenuHeader
    {
        get
        {
            var row = _contextRow;
            if (row != null && !(row.IsSelected && SelectionCount > 1))
                return row.IsFolder && row.LinkCount > 0 ? Loc.K("browser.btn.deleteCount", row.LinkCount) : Loc.K("common.delete");
            return HasSelection ? Loc.K("browser.btn.deleteCount", SelectionCount) : Loc.K("common.delete");
        }
    }

    /// <summary>右侧详情栏状态（P4.5 现代化重设计）：选中态变化时同步刷新。</summary>
    public BrowserDetailsViewModel Details { get; }

    /// <summary>选中态变化时由行 VM 回调（行是 INPC 通知源，VM 借此刷新派生属性与命令状态）。</summary>
    internal void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionInfoText));
        OnPropertyChanged(nameof(DeleteMenuHeader));
        Details.UpdateFrom(SelectedRows.ToList(), this);
        CommandRefresh.Request();
    }

    // —— 拖拽落点（悬停高亮 + 「移动到 X」提示的唯一事实来源）——
    // 状态机 = 独立控制器 BrowserDropState（覆盖式更新，铁律 9）；VM 只做"写状态 + 投影"。

    /// <summary>拖拽落点状态（**控制器**）：目标 / 模式 / 提示文案的唯一事实来源。</summary>
    public BrowserDropState DropState { get; } = new();

    /// <summary>当前目录显示名（根 = 「全部书签」）：列表空白落点的提示文案用。</summary>
    public string CurrentFolderDisplayName
        => Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Name : BookmarkDisplay.Segment(BookmarkPath.RootToken);

    /// <summary>主栏某行是否为当前落点（行 <c>IsDropTarget</c> 直接读这里——行是只读投影）。</summary>
    public bool IsDropTargetRow(string id) => DropState.IsRowTarget(id);

    /// <summary>
    /// 当前拖拽落点（只读投影）：视图在拖拽收尾时读它——左键拖拽的成环判定与右键拖拽的
    /// 「复制到此处 / 移动到此处」菜单都用<b>这一个</b>事实来源，绝不各自再做一次命中测试或修饰键判定。
    /// </summary>
    public BrowserDropTarget? DropTarget => DropState.Target;

    /// <summary>当前落点会做什么（无落点 = 移动，仅作默认值；调用方只在有落点时用它）。</summary>
    public TransferMode DropTargetMode => DropState.Mode;

    /// <summary>落点提示文案（空串 = 不显示）：`移动到「X」` / `复制到「X」`——文案口径在
    /// <see cref="Views.DragSupport.HintText"/>（唯一实现，与回收站页共用）。</summary>
    public string DropTargetHintText => DropState.HintText;

    /// <summary>
    /// 修饰键 → 传输模式的**唯一实现**（默认移动；按住 Ctrl = 复制——Windows 单卷口径）。
    /// 三处调用（DragOver 的光标与提示、QueryContinueDrag 的即时刷新、Drop 的最终动作）都走这里，
    /// 不允许任何地方再写第二份 Ctrl 判定。
    /// </summary>
    public static TransferMode ResolveDropMode(bool controlPressed) => BrowserDropState.ResolveMode(controlPressed);

    /// <summary>
    /// 写入拖拽落点：拖拽悬停的**唯一入口**，**覆盖式**（每次 DragOver 重写当前值，既不清零也不累积）。
    /// 传 <c>null</c> = 指针不在任何可落点上（空白 / 非法目标 / 链接）→ 两栏高亮熄灭、提示不显示；
    /// 非法目标（拖到自己或自己的后代）也传 null：光标已用禁止态表达，不该再高亮或提示"移动到"。
    /// </summary>
    public void SetDropTarget(BrowserDropTarget? target) => DropState.Set(target);

    /// <summary>
    /// 落点**不变、只换动作**（拖拽中按下/松开 Ctrl）：鼠标没动时 OLE 不会再派发 DragOver，
    /// 但 `QueryContinueDrag` 每次修饰键变化都会触发 → 由此把模式补进落点状态，保证
    /// **松手时执行的动作与提示条说的一致**（光标由 OLE 决定，可能滞后一次，见 WARNINGS）。
    /// </summary>
    public void SetDropTargetMode(TransferMode mode) => DropState.SetMode(mode);

    /// <summary>拖拽结束（松手 / Esc 取消 / 拖出可落点）统一清空落点：绝不留残留高亮。</summary>
    public void ClearDropTarget() => DropState.Clear();

    /// <summary>落点状态变化 → 投影：行高亮（拉刷）+ 树高亮（推送）+ 提示文案属性。</summary>
    private void OnDropStateChanged()
    {
        foreach (var r in Rows) r.InvalidateIsDropTarget();
        SyncTreeDropTarget();
        OnPropertyChanged(nameof(DropTargetHintText));
    }

    /// <summary>目录树落点投影（节点侧是推送式，与 IsSelected 同构）：只有指针所在栏是树、
    /// 且节点实体 ID 等于当前落点时才高亮；链接叶子与虚根永不作落点。</summary>
    private void SyncTreeDropTarget()
    {
        foreach (var node in AllTreeNodes())
        {
            string? entityId = node.IsLink ? node.Id : node.FolderId;
            node.IsDropTarget = !node.IsLink && DropState.IsNodeTarget(entityId);
        }
    }

    /// <summary>
    /// 文件夹完整路径展示（详情栏用）："全部书签 / A / B"；根返回Loc.K("tools.pathRoot")。
    /// includeSelf=false 时用于「选中文件夹本身」的场景：位置只显示其祖先链，不包含自己。
    /// </summary>
    public string GetFolderPathDisplay(string? folderId, bool includeSelf = true)
    {
        if (FolderIds.IsRoot(folderId)) return BookmarkDisplay.Segment(BookmarkPath.RootToken);
        var chain = BuildBreadcrumbIds(folderId).ToList();
        if (!includeSelf && chain.Count > 0) chain.RemoveAt(chain.Count - 1);
        // 先拼 canonical 再投影：转义与分隔只有一份实现，显示层不自己拼路径
        return BookmarkDisplay.Path(BookmarkPath.Build(BookmarkPath.RootToken, chain.Select(c => c.Name)));
    }

    // —— 就地重命名（Windows 口径：主栏行与目录树节点都能就地改名，链接改的是标题）——
    // **唯一事实来源** = 下面的重命名会话状态（目标 ID + 是否文件夹 + 编辑面 + 编辑文本 + 原名）；
    // 行与树节点上的 IsRenaming 全部是它的投影（与 IsSelected 同构），
    // 绝不各自持一份"编辑中"的标记，也绝不靠"谁先谁后"的时序去拉齐（架构红线）。

    /// <summary>改名会话状态机（**控制器**）：目标 / 编辑面 / 原名 / 编辑文本 / 挂起提交的唯一事实来源。</summary>
    private readonly BrowserRenameController _rename = new();

    /// <summary>改名编辑中的文本（编辑框 TwoWay 绑定；输入即回写）。</summary>
    public string EditingName
    {
        get => _rename.EditingName;
        set { if (_rename.EditingName == value) return; _rename.EditingName = value; OnPropertyChanged(); }
    }

    /// <summary>是否正在就地改名（页面级动作一律让位：编辑语义优先）。</summary>
    public bool IsRenaming => _rename.IsActive;

    /// <summary>某实体此刻是否显示改名编辑框（投影判据：目标一致 + 编辑面一致）。</summary>
    public bool IsRenamingId(string? id, BrowserPane surface) => _rename.IsRenamingId(id, surface);

    /// <summary>Enter / 失焦：提交改名（空名或未改 = 还原，Windows 口径）。</summary>
    public ICommand CommitRenameCommand { get; }

    /// <summary>Esc：取消改名（还原原名，不动数据）。</summary>
    public ICommand CancelRenameCommand { get; }

    /// <summary>进入改名（主栏行入口：F2 / 右键菜单「重命名」/ 再次单击已选中行）。</summary>
    public void BeginRenameRow(BrowserRowViewModel? row)
        => BeginRename(row?.Id, row?.IsFolder ?? false, row?.Name, BrowserPane.Main);

    /// <summary>进入改名（左栏树入口：F2 / 右键菜单「重命名」）。虚根「全部书签」不是实体 → 不进入。</summary>
    public void BeginRenameNode(FolderNode? node)
    {
        if (node == null || node.IsRoot) return;
        BeginRename(node.IsLink ? node.Id : node.FolderId, !node.IsLink, node.Name, BrowserPane.Tree);
    }

    private void BeginRename(string? id, bool isFolder, string? name, BrowserPane surface)
    {
        if (string.IsNullOrEmpty(id) || name == null) return;

        // 切换改名目标 = 旧会话按 Windows 口径**提交**（不丢已输入内容；无改动等价无操作）。
        // 顺序不可换：先捕获旧会话快照 → 收旧会话 → 起新会话 → 最后才异步提交旧快照
        //（否则提交逻辑会读到刚上位的**新**会话）。目标相同（重复进入同一次改名）时不重复提交。
        var previous = _rename.Capture();
        if (previous != null) EndRename();

        _rename.Begin(id, isFolder, name, surface);
        ApplyRenameToView();
        OnPropertyChanged(nameof(EditingName));   // 文本由控制器直写，属性通知在这里补
        OnPropertyChanged(nameof(IsRenaming));
        CommandRefresh.Request();

        // 行若已实例化：滚入视口（编辑框由 InlineNameEditor 自己在显示后聚焦并全选）
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row != null && surface == BrowserPane.Main) FocusRowRequested?.Invoke(this, row);

        if (previous != null && !string.Equals(previous.Id, id, StringComparison.Ordinal))
            _ = CommitSessionAsync(previous);
    }

    /// <summary>结束改名会话（提交与取消的**唯一收口**；会话状态一次性归零并重投影）。</summary>
    private void EndRename()
    {
        if (_rename.End() == null) return;
        ApplyRenameToView();
        OnPropertyChanged(nameof(EditingName));   // 文本已清空（控制器直写），属性通知在这里补
        OnPropertyChanged(nameof(IsRenaming));
        CommandRefresh.Request();
    }

    /// <summary>取消改名（Esc）：只收会话，不写数据。</summary>
    public void CancelRename() => EndRename();

    /// <summary>
    /// 页面级收尾动作：**收掉当前改名的编辑态**（右键菜单打开时调用）。
    /// Windows 口径：改名进行中右键 → 改名立即退出，且**已输入的名字保留**（不丢输入）。
    ///
    /// <para>提交动作**推迟到菜单关闭**（<see cref="FlushDeferredCommit"/>）：改名提交会让引擎写库 →
    /// 事件刷新（300ms 防抖）→ 重建行容器 → 承载菜单的行被销毁 → **菜单被连带关闭**（表现为"菜单一闪就没了"）。
    /// 推迟只影响"名字何时落库"，不影响可见语义（编辑框立即收起、菜单稳定可用）。</para>
    /// </summary>
    public void CommitActiveRename()
    {
        FlushDeferredCommit();               // 上一次挂起的先落地，避免被本次覆盖而丢失
        var session = _rename.Capture();
        if (session == null) return;
        EndRename();                         // 编辑态立即收起（投影归零）
        _rename.Defer(session);
    }

    /// <summary>落地挂起的改名提交（菜单关闭时调用）。</summary>
    public void FlushDeferredCommit()
    {
        if (_rename.TakeDeferred() is { } session)
            _ = CommitSessionAsync(session);
    }

    /// <summary>
    /// 提交改名：文件夹 → <c>folders.update{name}</c>；链接 → <c>links.update{title}</c>（重命名 = 标题）。
    /// 空名 / 未改 = 视为取消并还原（Windows 口径）。**重命名不入撤销栈**。
    /// 同层撞名的自动编号由**引擎**负责（Kernel 唯一命名服务 IFolderNaming），界面只如实展示引擎返回的最终名。
    /// </summary>
    public async Task CommitRenameAsync()
    {
        var session = _rename.Capture();
        if (session == null) return;
        EndRename();                       // 先收会话：此后任何失焦/重复提交都成为空操作
        await CommitSessionAsync(session);
    }

    /// <summary>提交一个**已捕获**的会话快照（切换目标时提交旧会话也走这里，不丢用户输入）。</summary>
    private async Task CommitSessionAsync(BrowserRenameController.Session session)
    {
        var name = (session.Name ?? string.Empty).Trim();
        if (name.Length == 0 || string.Equals(name, session.OriginalName, StringComparison.Ordinal)) return;

        try
        {
            if (session.IsFolder)
            {
                var updated = await _client.FolderUpdateAsync(session.Id, name: name);
                StatusText = Loc.K("browser.status.renamed", updated.Data?.Name ?? name);
            }
            else
            {
                await _client.LinkUpdateAsync(session.Id, title: name);
                StatusText = Loc.K("browser.status.renamed", name);
            }
            // 刷新交给后端事件（300ms 防抖）：事件链刷新本就保留选中（选中在 Selection，不随重建丢）
        }
        catch (Exception)
        {
            ShowError(Loc.T("status.renameFailed"), Loc.T("err.unexpected"));
        }
    }

    /// <summary>重命名态投影：行与树节点上的 IsRenaming 全部从会话状态派生（与选中投影同构）。</summary>
    private void ApplyRenameToView()
    {
        foreach (var r in Rows) r.InvalidateIsRenaming();
        foreach (var node in AllTreeNodes())
            node.IsRenaming = IsRenamingId(node.IsLink ? node.Id : node.FolderId, BrowserPane.Tree);
    }

    // —— 剪贴板（Ctrl+X / C / V，载荷见 ClipboardManager.BrowserClipboardPayload）——
    // 语义（剪切/复制/取消/载荷构造）= 控制器 BrowserClipboardController；存储 = Clipboard；传输 = TransferAsync（唯一流水线）。

    public Managers.ClipboardManager Clipboard { get; } = new();

    /// <summary>剪贴板语义控制器（构造于 ctor：注入拖动集合出口 / 剪切视觉投影 / 状态文案）。</summary>
    private readonly BrowserClipboardController _clipboardCtl;

    // —— 面包屑内联路径编辑（Explorer 地址栏两态）——
    // 状态机 = 独立控制器 BrowserPathEditController（编辑态 / 文本 / 校验 / 候选的唯一事实来源）；
    // 解析算法在 UIKit Views.PathResolver；VM 只转发属性 + 提供"当前路径文本"。

    /// <summary>路径编辑控制器（构造于 ctor：注入解析器 + 导航 / 提示回调）。</summary>
    private readonly BrowserPathEditController _pathEdit;

    public bool IsPathEditing => _pathEdit.IsEditing;

    public string PathEditText
    {
        get => _pathEdit.Text;
        set => _pathEdit.Text = value;
    }

    public bool IsPathInvalid => _pathEdit.IsInvalid;

    public List<string> PathCandidates => _pathEdit.Candidates.ToList();
    public bool HasPathCandidates => _pathEdit.HasCandidates;

    public int SelectedCandidateIndex
    {
        get => _pathEdit.SelectedCandidateIndex;
        set => _pathEdit.SelectedCandidateIndex = value;
    }

    /// <summary>路径编辑状态变化 → 属性通知 + 命令可用性（控制器只发一个 Changed）。
    /// ⚠️ **顺序敏感**（实测）：文本类通知必须先于 `IsPathEditing` —— 控件在"进入编辑态"的通知里
    /// 做聚焦 + 整名全选，若此时 TextBox 还是旧文本，随后的文本更新会把选区清掉（渲染检查"进入即全选"红）。</summary>
    private void OnPathEditChanged()
    {
        OnPropertyChanged(nameof(PathEditText));
        OnPropertyChanged(nameof(IsPathInvalid));
        OnPropertyChanged(nameof(PathCandidates));
        OnPropertyChanged(nameof(HasPathCandidates));
        OnPropertyChanged(nameof(SelectedCandidateIndex));
        OnPropertyChanged(nameof(IsPathEditing));   // 最后发：控件据此聚焦+全选，此时文本已就位
        CommandRefresh.Request();
    }

    /// <summary>文件夹 ID → 父 ID 映射（含名称），用于面包屑与"返回上级"。</summary>
    private Dictionary<string, (string? ParentId, string Name)> _folderMap = new();

    public BrowserViewModel(EngineClient client, Services.UiPortProvider? ports = null,
        Services.IContentLocator? locator = null)
    {
        _client = client;
        _ports = ports;
        Details = new BrowserDetailsViewModel(client, locator);
        // 剪贴板语义（**控制器**）：载荷构造 / 剪切态视觉 / 取消都在控制器内；
        // VM 只注入"拖动集合唯一出口 + 行半透明投影 + 状态文案"三个回调。
        _clipboardCtl = new BrowserClipboardController(
            Clipboard, () => BuildDragItems(), () => Controller.CurrentFolderId, ApplyCutVisual, s => StatusText = s);
        // 路径编辑（**控制器**）：解析器用本页文件夹映射（与 BuildPathText 同源）；导航/提示回注本类
        _pathEdit = new BrowserPathEditController(
            new Views.PathResolver(BookmarkPath.RootToken,
                parentId => _folderMap
                    .Where(kvp => kvp.Value.ParentId == parentId)
                    .Select(kvp => new Views.PathNode(kvp.Key, kvp.Value.Name))
                    .ToList()),
            folderId => _ = LoadAsync(folderId),
            msg => StatusText = msg);
        _pathEdit.Changed += OnPathEditChanged;
        GoBackCommand = new RelayCommand(() => _ = LoadAsync(Controller.GoBack()), () => Controller.CanGoBack);
        GoForwardCommand = new RelayCommand(() => _ = LoadAsync(Controller.GoForward()), () => Controller.CanGoForward);
        GoUpCommand = new RelayCommand(() => _ = LoadAsync(GetParentId(Controller.CurrentFolderId)), () => !IsAtRoot() && !IsPathEditing && !IsRenaming);
        RowClickCommand = new RelayCommand<BrowserRowViewModel?>(SelectRow);
        // 双击打开：改名编辑中让位（编辑框内的双击绝不打开目录/详情）
        RowOpenCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenRowAsync(row), _ => !IsRenaming);
        CrumbClickCommand = new RelayCommand<BrowserCrumbViewModel?>(crumb => _ = LoadAsync(crumb?.FolderId));
        CopyUrlCommand = new RelayCommand<BrowserRowViewModel?>(CopyUrl);
        // 重命名 = **就地编辑**（Windows 口径）：行/树右键菜单与 F2 都只是"进入编辑"，不再弹输入框
        RenameRowCommand = new RelayCommand<BrowserRowViewModel?>(BeginRenameRow);
        EditRowCommand = new RelayCommand<BrowserRowViewModel?>(row => { if (row is { IsFolder: false }) OpenEditorForEdit(row.Id); });
        DeleteRowCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = DeleteRowAsync(row));
        NewFolderCommand = new RelayCommand<object?>(param => _ = NewFolderAsync(param as string),
            _ => !IsPathEditing && !IsRenaming && IsListContextActive);
        NewLinkCommand = new RelayCommand(OpenEditorForCreate,
            () => !IsPathEditing && !IsRenaming && IsListContextActive);
        OpenDetailCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenDetailPageAsync(row));
        DetailPage = new LinkDetailPageViewModel(client, this);
        RenameNodeCommand = new RelayCommand<FolderNode?>(BeginRenameNode);
        DeleteNodeCommand = new RelayCommand<FolderNode?>(node => _ = DeleteNodeAsync(node));
        CutCommand = new RelayCommand(_clipboardCtl.Cut, () => HasSelection && !IsPathEditing && !IsRenaming && IsListContextActive);
        CopyCommand = new RelayCommand(_clipboardCtl.Copy, () => HasSelection && !IsPathEditing && !IsRenaming && IsListContextActive);
        PasteCommand = new RelayCommand(() => _ = PasteAsync(), () => Clipboard.BrowserPayload is { IsEmpty: false } && !IsPathEditing && !IsRenaming && IsListContextActive);
        SelectAllCommand = new RelayCommand(SelectAllRows, () => !IsPathEditing && !IsRenaming);
        // Esc 在路径编辑态里归属「取消路径编辑」；改名编辑态里归编辑框自己（控件级编辑语义）；本命令两处都让位。
        // 分层语义：有剪切态 → 先取消剪切（应用级剪贴板状态，与所在目录无关）；无剪切态 → 清空选中。
        EscapeCommand = new RelayCommand(HandleEscape, () => !IsPathEditing && !IsRenaming && !IsEditorPageOpen);
        // 点空白清选中（唯一实现 = UIKit BlankClick 附加行为，XAML 按区域挂载）：
        // 列表卡空白 = 主栏获得键盘语义归属；页面其它空白 = 保持当前归属（清选中 + 焦点收回页内）。
        ClearMainPaneSelectionCommand = new RelayCommand(ClearMainPaneSelection);
        ClearPageSelectionCommand = new RelayCommand(ClearPageSelection);
        DeleteSelectionCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => HasSelection && !IsPathEditing && !IsRenaming && IsListContextActive && !IsDeleting);
        RenameSelectionCommand = new RelayCommand(BeginRenameSelection, () => SelectionCount == 1 && !IsPathEditing && !IsRenaming && IsListContextActive);
        OpenSelectionCommand = new RelayCommand(() => _ = OpenSelectedAsync(), () => SelectionCount == 1 && !IsPathEditing && !IsRenaming);
        // 改名编辑框（InlineNameEditor）只发命令：提交/取消都收口到同一会话状态
        CommitRenameCommand = new RelayCommand(() => _ = CommitRenameAsync());
        CancelRenameCommand = new RelayCommand(CancelRename);
        EnterPathEditCommand = new RelayCommand(() => { if (!IsPathEditing) EnterPathEdit(); }, () => !IsPathEditing && !IsRenaming);
        // F5 = 真刷新（导航加载口径）：主栏内容 + 左栏树一起重建，亮遮罩与入场动画（Windows 口径）
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(navigating: true));
        NavigateToSearchCommand = new RelayCommand(() => _ports?.Navigation?.NavigateToSearch());
        ExpandTreeToCurrentCommand = new RelayCommand(ExpandTreeToCurrentLocation);
        CopyPathCommand = new RelayCommand(CopyCurrentPath);
        MoveSelectionCommand = new RelayCommand<object?>(p => MoveMainSelection(ParseDirection(p)));
        SelectLastCommand = new RelayCommand(SelectLastRow);
        MoveTreeSelectionCommand = new RelayCommand<object?>(p => MoveTreeSelection(ParseDirection(p)));
        ToggleTreeExpandCommand = new RelayCommand(ToggleFocusedTreeExpand);
        UndoCommand = new RelayCommand(() => _ = UndoRedoAsync(redo: false), () => CanUndo && !IsRenaming && IsListContextActive);
        RedoCommand = new RelayCommand(() => _ = UndoRedoAsync(redo: true), () => CanRedo && !IsRenaming && IsListContextActive);
        ShowContextMenuCommand = new RelayCommand(ShowContextMenuForSelection, () => !IsRenaming);
        ConfirmPathCommand = new RelayCommand(ConfirmPath);
        CancelPathEditCommand = new RelayCommand(CancelPathEdit);
        CompletePathCommand = new RelayCommand(CompletePath);

        // 剪贴板载荷变化（含被其他页面/操作清空）→ 刷新粘贴命令可用性
        Clipboard.ClipboardChanged += (_, _) => CommandRefresh.Request();

        // 选中集合（共享 ListSelection 核心）变化 → 唯一的投影点（主栏行 + 树 + 派生状态）
        Selection.Changed += ApplySelectionToView;

        // 拖拽落点状态（控制器）变化 → 唯一的投影点（行高亮拉刷 + 树高亮推送 + 提示文案属性）
        DropState.Changed += OnDropStateChanged;
    }

    // —— 排序（服务端排序：视图层共享数据表控件 SortableDataTable 点列头后经 SortChanged 事件转到这里）——

    private const string DefaultSortBy = "title";
    private const string DefaultSortOrder = "asc";

    public string SortBy { get; private set; } = DefaultSortBy;
    public string SortOrder { get; private set; } = DefaultSortOrder;

    // —— 交互 ——

    private async Task OpenRowAsync(BrowserRowViewModel? row)
    {
        if (row == null) return;
        SelectRow(row);

        if (row.IsFolder)
        {
            await LoadAsync(row.Id);
            return;
        }

        // 双击链接：打开链接详情页（不再直接打开网站；打开网站改由详情页顶栏按钮承担）
        await OpenDetailPageAsync(row);
    }

    /// <summary>当前目录的父目录 ID；已在根目录时返回 null（根没有父级）。</summary>
    private string? GetParentId(string? folderId)
    {
        if (IsAtRoot() || folderId == null) return null;
        return _folderMap.TryGetValue(folderId, out var info) ? info.ParentId : null;
    }

    private bool IsAtRoot() => Controller.CurrentFolderId == null;

    /// <summary>
    /// Esc 分层语义：
    /// ① 有剪切态（剪贴板里是待粘贴的剪切载荷）→ **取消剪切**（清载荷 + 清半透明视觉 + 状态栏反馈）；
    /// ② 无剪切态 → 清空选中（原语义）。
    /// 剪切态是应用级剪贴板状态，**与当前所在目录无关**：任何目录按 Esc 都能取消（Windows 同口径——
    /// Explorer 的 Esc 是全局清剪贴板，不是"仅当前文件夹"）；换目录/刷新也都不会遗忘它。
    /// 遗忘时机只有：Esc 取消 / 被新的复制剪切覆盖 / 粘贴完成（复制载荷不清，可多次粘贴）/ 关闭应用。
    /// </summary>
    private void HandleEscape()
    {
        // 分层（Windows 口径，比 Explorer 多一层）：
        // ① 详情页打开 → 退出详情页（与左上返回钮**同一条路径**：还原选中 + 原地刷新）；
        // ② 有剪切态 → 取消剪切；③ 否则清空选中。
        // ⚠️ 编辑器页打开时整条命令不分发（CanExecute 挡掉）——编辑页不加 Esc 快捷键，防误触。
        if (IsDetailPageOpen && DetailPage?.BackCommand is { } back && back.CanExecute(null))
        {
            back.Execute(null);
            return;
        }
        if (_clipboardCtl.HasCutPayload)
        {
            _clipboardCtl.Cancel();
            return;
        }
        ClearSelection();
    }

    /// <summary>剪切半透明视觉（行侧投影；传 null = 全清）。剪贴板语义在控制器，视觉投影留宿主。</summary>
    private void ApplyCutVisual(IReadOnlyList<DragItem>? items)
    {
        foreach (var r in Rows)
            r.IsCut = items != null && items.Any(i => string.Equals(i.Id, r.Id, StringComparison.Ordinal));
    }

    // 剪贴板语义（载荷构造 / 剪切 / 复制 / 取消）已上收控制器 `BrowserClipboardController`；
    // 传输入口见 `BrowserViewModel.Transfer.cs`：与拖拽共用同一条传输流水线（`TransferAsync`）。

    // 成环（非法目标）的弹窗文案在 `BrowserViewModel.Transfer.cs`（`BlockedTitle` / `BlockedMessage`，按 TransferMode 出词）：
    // 拖拽与粘贴**共用同一套**（反馈口径只有一处）。

    private void EnterPathEdit() => _pathEdit.Enter(BuildPathText(Controller.CurrentFolderId));

    private void CancelPathEdit() => _pathEdit.Cancel();

    private string BuildPathText(string? folderId)
        // canonical 再由 BookmarkDisplay 投影：根段随语言、段内 / 转义不丢（解析时两种形态都认）
        => BookmarkDisplay.Path(Paths.BuildCanonical(BuildBreadcrumbIds(folderId)));

    /// <summary>路径文本构建用的解析器（唯一实现在 UIKit Views.PathResolver；控制器内另持一份同源实例）。</summary>
    private Views.PathResolver? _pathResolver;
    private Views.PathResolver Paths => _pathResolver ??= new Views.PathResolver(
        BookmarkPath.RootToken,
        parentId => _folderMap
            .Where(kvp => kvp.Value.ParentId == parentId)
            .Select(kvp => new Views.PathNode(kvp.Key, kvp.Value.Name))
            .ToList());

    // 路径文本转义/切分与逐级解析/候选已上收 UIKit（Views.PathText / Views.PathResolver，浏览页与回收站共用同一实现）。

    private void ConfirmPath() => _pathEdit.Confirm();

    /// <summary>Tab：用当前候选补全最后一级。</summary>
    private void CompletePath() => _pathEdit.Complete();

    /// <summary>选择候选（点击或 Tab）：改写文本后保留编辑态，继续输入下一级。</summary>
    public void ChooseCandidate(string name) => _pathEdit.Choose(name);

    /// <summary>↑/↓ 移动候选高亮（由视图键盘事件调用）。</summary>
    public void MoveCandidate(int delta) => _pathEdit.MoveCandidate(delta);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

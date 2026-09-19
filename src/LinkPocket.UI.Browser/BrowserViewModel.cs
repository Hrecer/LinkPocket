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
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>浏览页的两栏：主栏（目录内容列表）/ 左栏（文件夹树）。键盘语义按栏归属——
/// 例如 ↑/↓ 在两栏含义不同（主栏移动行选中；左栏按树的视觉顺序移动）。</summary>
public enum BrowserPane
{
    /// <summary>主栏（内容列表）。</summary>
    Main,
    /// <summary>左栏（文件夹树）。</summary>
    Tree
}

/// <summary>
/// 资源管理器式浏览页（P4）：一切数据经引擎查询命令（folders.contents 等）获取，
/// 渲染由 XAML ItemsControl + DataTemplate 完成，本类不持有任何控件引用。
/// </summary>
public class BrowserViewModel : INotifyPropertyChanged
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
    /// 超过说明数据在持续高频变动——本轮放弃"必处理"承诺，交还给 300ms 事件防抖继续追平，杜绝无限递归。</summary>
    private int _refreshRecursionDepth;

    /// <summary>补刷递归深度上限（超过后本轮不再递归补刷）。</summary>
    private const int MaxRefreshRecursion = 3;

    public BrowserHistory Controller { get; } = new();

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

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    /// <summary>刷新在途标志（同时是 RefreshAsync 的重入守卫）：任何刷新都会置位，含后台事件刷新。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _isNavigating;
    /// <summary>
    /// 导航加载（界面加载遮罩的唯一来源）：**只有用户发起的导航/刷新**才为 true——
    /// 打开文件夹、跳转定位、返回上级、后退/前进、F5；事件驱动的后台刷新一律静默（不闪动画）。
    /// 与 <see cref="IsLoading"/> 的区别：后者是"有没有刷新在途"（内部重入守卫），前者是"这次刷新要不要给用户看"。
    /// </summary>
    public bool IsNavigating
    {
        get => _isNavigating;
        private set { if (_isNavigating == value) return; _isNavigating = value; OnPropertyChanged(); }
    }

    /// <summary>挂起补刷是否属于导航加载（与 <see cref="_clearSelectionOnPendingRefresh"/> 同机制，逐轮继承）。</summary>
    private bool _navigatingOnPendingRefresh;

    /// <summary>本轮刷新链（含挂起补刷）里是否出现过导航加载——链条结束一次性告知界面。</summary>
    private bool _navigatingInChain;

    /// <summary>
    /// 一次刷新链（含挂起补刷）**完成**时触发；参数 = 该链是否属于"导航加载"。
    /// 界面据此决定"行入场动画"播不播：只有打开文件夹这类导航才播，后台刷新（含写操作后的防抖刷新）静默——
    /// 绝不按"集合有没有变更"来播（曾在每次刷新都重播，用户报"移动后那次刷新还有动画"）。
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

    // —— 链接详情页（全页覆盖层，参考链接页书签详情） ——

    public LinkDetailPageViewModel DetailPage { get; }

    private bool _isDetailPageOpen;
    /// <summary>链接详情页是否打开（打开时覆盖整个浏览模块内容区）。</summary>
    public bool IsDetailPageOpen
    {
        get => _isDetailPageOpen;
        private set { _isDetailPageOpen = value; OnPropertyChanged(); }
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
        private set { _isEditorPageOpen = value; OnPropertyChanged(); }
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
    public ICommand DeleteSelectionCommand { get; }
    public ICommand RenameSelectionCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    public ICommand TogglePathEditCommand { get; }
    /// <summary>Alt+D：聚焦地址栏（进编辑态，由视图聚焦并全选）。</summary>
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

    /// <summary>Shift 区间选择的起点行 ID。</summary>
    private string? _anchorId;

    /// <summary>主栏选中集合 = 整个浏览模块选中状态的唯一事实来源。
    /// 主栏行的 IsSelected 与目录树的叶子高亮都从它投影（见 <see cref="ApplySelectionToRows"/> 与
    /// <see cref="ApplyTreeSelection"/>）：任何一次 Rows/Tree 重建都按该集合重放，
    /// 不依赖行对象引用存活、不依赖 TreeView 容器时序。点空白 = 清空此集合 = 主栏与树同时取消。</summary>
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

    /// <summary>目标 ID 当前是否处于选中集合（行投影与树叶子投影共用此判据）。</summary>
    public bool IsSelectedId(string? id) => id != null && _selectedIds.Contains(id);

    public IEnumerable<BrowserRowViewModel> SelectedRows => Rows.Where(r => _selectedIds.Contains(r.Id));
    public int SelectionCount => Rows.Count(r => _selectedIds.Contains(r.Id));
    public bool HasSelection => SelectionCount > 0;
    public bool HasMultipleSelection => SelectionCount > 1;
    public string SelectionInfoText => HasSelection ? $"已选中 {SelectionCount} 项" : string.Empty;

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
        return Views.ConfirmDialog.Show(title, message, "删除");
    }

    /// <summary>
    /// 「删除」菜单文案，口径与 <see cref="DeleteRowAsync"/> 的删除目标严格一致，且只在"数字有意义"时才报数：
    /// 单个链接 / 空文件夹 → 只显示「删除」；单个文件夹 → 显示其内链接数（删除文件夹 = 其中链接进回收站）；
    /// 右键多选中的行 → 显示选中项数。
    /// </summary>
    public string DeleteMenuHeader
    {
        get
        {
            var row = _contextRow;
            if (row != null && !(row.IsSelected && SelectionCount > 1))
                return row.IsFolder && row.LinkCount > 0 ? $"删除 ({row.LinkCount} 项)" : "删除";
            return HasSelection ? $"删除 ({SelectionCount} 项)" : "删除";
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
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>把选中集合 <see cref="_selectedIds"/> 投影到主栏行 + 目录树，并刷新派生状态。
    /// 在选中写入（<see cref="SetSelection"/>）与 Rows/Tree 重建后（RefreshAsync）调用；
    /// 行与树都是该集合的只读投影，无任何独立选中状态。</summary>
    private void ApplySelectionToView()
    {
        SyncMainRowSelection();
        SyncTreeSelection();
        NotifySelectionChanged();
    }

    /// <summary>主栏行投影：每行 IsSelected = 其 Id 是否在选中集合（行只读，集合是唯一事实）。
    /// 行的 IsSelected getter 已直接读 <see cref="IsSelectedId"/>，此处仅为强制刷新绑定。</summary>
    private void SyncMainRowSelection()
    {
        foreach (var r in Rows) r.InvalidateIsSelected();
    }

    /// <summary>
    /// 目录树投影：树节点高亮 = 用户在选中集合中真正选中的实体，**与当前所处目录无关**。
    /// 「位于某文件夹 / 根目录」是导航位置，由面包屑表达，绝不转换为树高亮——
    /// 进入某个文件夹不代表该文件夹"被选中"（用户 2026-09-18/19 明确：位置 ≠ 选中）。
    /// 树不持久任何选中状态，全部由唯一事实来源 <see cref="_selectedIds"/> 派生：
    /// 链接叶子高亮 = 该链接在集合；文件夹节点高亮 = 其 FolderId 在集合（当且仅当用户选中了该文件夹实体）。
    /// </summary>
    private void SyncTreeSelection()
    {
        foreach (var node in AllTreeNodes())
        {
            // 虚拟根「全部书签」不是实体：不因位于根目录而高亮；仅当用户选中了真实实体（链接叶子或文件夹）才高亮
            string? entityId = node.IsLink ? node.Id : node.FolderId;
            node.IsSelected = entityId != null && _selectedIds.Contains(entityId);
        }
    }



    /// <summary>
    /// 文件夹完整路径展示（详情栏用）："全部书签 / A / B"；根返回"全部书签"。
    /// includeSelf=false 时用于「选中文件夹本身」的场景：位置只显示其祖先链，不包含自己。
    /// </summary>
    public string GetFolderPathDisplay(string? folderId, bool includeSelf = true)
    {
        if (FolderIds.IsRoot(folderId)) return FolderIds.RootDisplayName;
        var chain = BuildBreadcrumbIds(folderId).ToList();
        if (!includeSelf && chain.Count > 0) chain.RemoveAt(chain.Count - 1);
        if (chain.Count == 0) return "全部书签";
        return "全部书签 / " + string.Join(" / ", chain.Select(c => c.Name));
    }

    // —— 就地重命名（Windows 口径：主栏行与目录树节点都能就地改名，链接改的是标题）——
    // **唯一事实来源** = 下面的重命名会话状态（目标 ID + 是否文件夹 + 编辑面 + 编辑文本 + 原名）；
    // 行与树节点上的 IsRenaming 全部是它的投影（与 IsSelected 同构），
    // 绝不各自持一份"我在编辑"的标记，也绝不靠"谁先谁后"的时序去拉齐（用户硬性红线）。

    /// <summary>正在改名的实体 ID（null = 未在改名）。</summary>
    private string? _renameId;

    /// <summary>改名目标是不是文件夹（决定提交走 folders.update 还是 links.update）。</summary>
    private bool _renameIsFolder;

    /// <summary>编辑面：主栏行 or 左栏树——同一实体只在**一处**显示编辑框，避免两个编辑框互相抢焦点/双重提交。</summary>
    private BrowserPane _renameSurface;

    /// <summary>进入改名时的原名（提交时判"没改"用；不依赖行对象存活）。</summary>
    private string _renameOriginalName = string.Empty;

    private string _editingName = string.Empty;

    /// <summary>改名编辑中的文本（编辑框 TwoWay 绑定；输入即回写）。</summary>
    public string EditingName
    {
        get => _editingName;
        set { if (_editingName == value) return; _editingName = value; OnPropertyChanged(); }
    }

    /// <summary>是否正在就地改名（页面级动作一律让位：编辑语义优先）。</summary>
    public bool IsRenaming => _renameId != null;

    /// <summary>某实体此刻是否显示改名编辑框（投影判据：目标一致 + 编辑面一致）。</summary>
    public bool IsRenamingId(string? id, BrowserPane surface)
        => id != null && _renameId != null && _renameSurface == surface
           && string.Equals(_renameId, id, StringComparison.Ordinal);

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
        var previous = CaptureSession();
        if (previous != null) EndRename();

        _renameId = id;
        _renameIsFolder = isFolder;
        _renameSurface = surface;
        _renameOriginalName = name;
        EditingName = name;
        ApplyRenameToView();
        OnPropertyChanged(nameof(IsRenaming));
        CommandManager.InvalidateRequerySuggested();

        // 行若已实例化：滚入视口（编辑框由 InlineNameEditor 自己在显示后聚焦并全选）
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row != null && surface == BrowserPane.Main) FocusRowRequested?.Invoke(this, row);

        if (previous != null && !string.Equals(previous.Id, id, StringComparison.Ordinal))
            _ = CommitSessionAsync(previous);
    }

    /// <summary>结束改名会话（提交与取消的**唯一收口**；会话状态一次性归零并重投影）。</summary>
    private void EndRename()
    {
        if (_renameId == null) return;
        _renameId = null;
        _renameIsFolder = false;
        _renameOriginalName = string.Empty;
        EditingName = string.Empty;
        ApplyRenameToView();
        OnPropertyChanged(nameof(IsRenaming));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>取消改名（Esc）：只收会话，不写数据。</summary>
    public void CancelRename() => EndRename();

    /// <summary>改名会话快照（提交动作的**唯一凭据**：不读会话字段，杜绝异步途中被切换目标串味）。</summary>
    private sealed record RenameSession(string Id, bool IsFolder, string OriginalName, string Name);

    /// <summary>捕获当前会话快照；未在改名 → null。</summary>
    private RenameSession? CaptureSession()
        => _renameId == null
            ? null
            : new RenameSession(_renameId, _renameIsFolder, _renameOriginalName, EditingName ?? string.Empty);

    /// <summary>
    /// 页面级收尾动作：**收掉当前改名的编辑态**（右键菜单打开时调用）。
    /// Windows 口径：改名进行中右键 → 改名立即退出，且**已输入的名字保留**（不丢输入）。
    ///
    /// <para>提交动作**推迟到菜单关闭**（<see cref="FlushDeferredCommit"/>）：改名提交会让引擎写库 →
    /// 事件刷新（300ms 防抖）→ 重建行容器 → 承载菜单的行被销毁 → **菜单被连带关闭**（用户看到的"菜单一闪就没了"）。
    /// 推迟只影响"名字何时落库"，不影响用户可见语义（编辑框立即收起、菜单稳定可用）。</para>
    /// </summary>
    public void CommitActiveRename()
    {
        FlushDeferredCommit();               // 上一次挂起的先落地，避免被本次覆盖而丢失
        var session = CaptureSession();
        if (session == null) return;
        EndRename();                         // 编辑态立即收起（投影归零）
        _deferredCommit = session;
    }

    /// <summary>待提交的改名快照（右键收尾时挂起，菜单关闭后落地；单元素，非状态源——会话状态已由 EndRename 收口）。</summary>
    private RenameSession? _deferredCommit;

    /// <summary>落地挂起的改名提交（菜单关闭时调用）。</summary>
    public void FlushDeferredCommit()
    {
        var session = _deferredCommit;
        if (session == null) return;
        _deferredCommit = null;
        _ = CommitSessionAsync(session);
    }

    /// <summary>
    /// 提交改名：文件夹 → <c>folders.update{name}</c>；链接 → <c>links.update{title}</c>（重命名 = 标题）。
    /// 空名 / 未改 = 视为取消并还原（Windows 口径）。**重命名不入撤销栈**（用户 2026-09-19 定稿）。
    /// 同层撞名的自动编号由**引擎**负责（Kernel 唯一命名服务 IFolderNaming），界面只如实展示引擎返回的最终名。
    /// </summary>
    public async Task CommitRenameAsync()
    {
        var session = CaptureSession();
        if (session == null) return;
        EndRename();                       // 先收会话：此后任何失焦/重复提交都成为空操作
        await CommitSessionAsync(session);
    }

    /// <summary>提交一个**已捕获**的会话快照（切换目标时提交旧会话也走这里，不丢用户输入）。</summary>
    private async Task CommitSessionAsync(RenameSession session)
    {
        var name = (session.Name ?? string.Empty).Trim();
        if (name.Length == 0 || string.Equals(name, session.OriginalName, StringComparison.Ordinal)) return;

        try
        {
            if (session.IsFolder)
            {
                var updated = await _client.FolderUpdateAsync(session.Id, name: name);
                StatusText = $"已重命名为「{updated.Data?.Name ?? name}」";
            }
            else
            {
                await _client.LinkUpdateAsync(session.Id, title: name);
                StatusText = $"已重命名为「{name}」";
            }
            // 刷新交给后端事件（300ms 防抖）：事件链刷新本就保留选中（选中在 _selectedIds，不随重建丢）
        }
        catch (Exception ex)
        {
            ShowError("重命名失败", ex.Message);
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

    public Managers.ClipboardManager Clipboard { get; } = new();

    // —— 面包屑内联路径编辑（Explorer 地址栏两态）——

    private bool _isPathEditing;
    public bool IsPathEditing
    {
        get => _isPathEditing;
        set
        {
            if (_isPathEditing == value) return;
            _isPathEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PathEditIconKind));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string PathEditIconKind => IsPathEditing ? "close-circle-outline" : "pencil";

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
        set { _selectedCandidateIndex = value; OnPropertyChanged(); }
    }

    /// <summary>文件夹 ID → 父 ID 映射（含名称），用于面包屑与"返回上级"。</summary>
    private Dictionary<string, (string? ParentId, string Name)> _folderMap = new();

    public BrowserViewModel(EngineClient client, Services.UiPortProvider? ports = null)
    {
        _client = client;
        _ports = ports;
        Details = new BrowserDetailsViewModel(client);
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
        NewFolderCommand = new RelayCommand<object?>(param => _ = NewFolderAsync(param as string));
        NewLinkCommand = new RelayCommand(OpenEditorForCreate);
        OpenDetailCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenDetailPageAsync(row));
        DetailPage = new LinkDetailPageViewModel(client, this);
        RenameNodeCommand = new RelayCommand<FolderNode?>(BeginRenameNode);
        DeleteNodeCommand = new RelayCommand<FolderNode?>(node => _ = DeleteNodeAsync(node));
        CutCommand = new RelayCommand(CutSelection, () => HasSelection && !IsPathEditing && !IsRenaming);
        CopyCommand = new RelayCommand(CopySelection, () => HasSelection && !IsPathEditing && !IsRenaming);
        PasteCommand = new RelayCommand(() => _ = PasteAsync(), () => Clipboard.BrowserPayload is { IsEmpty: false } && !IsPathEditing && !IsRenaming);
        SelectAllCommand = new RelayCommand(SelectAllRows, () => !IsPathEditing && !IsRenaming);
        // Esc 在路径编辑态里归属「取消路径编辑」；改名编辑态里归编辑框自己（控件级编辑语义）；本命令两处都让位。
        // 分层语义：有剪切态 → 先取消剪切（应用级剪贴板状态，与所在目录无关）；无剪切态 → 清空选中。
        EscapeCommand = new RelayCommand(HandleEscape, () => !IsPathEditing && !IsRenaming);
        DeleteSelectionCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => HasSelection && !IsPathEditing && !IsRenaming);
        RenameSelectionCommand = new RelayCommand(BeginRenameSelection, () => SelectionCount == 1 && !IsPathEditing && !IsRenaming);
        OpenSelectionCommand = new RelayCommand(() => _ = OpenSelectedAsync(), () => SelectionCount == 1 && !IsPathEditing && !IsRenaming);
        // 改名编辑框（InlineNameEditor）只发命令：提交/取消都收口到同一会话状态
        CommitRenameCommand = new RelayCommand(() => _ = CommitRenameAsync());
        CancelRenameCommand = new RelayCommand(CancelRename);
        TogglePathEditCommand = new RelayCommand(TogglePathEdit);
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
        UndoCommand = new RelayCommand(() => _ = UndoRedoAsync(redo: false), () => CanUndo && !IsRenaming);
        RedoCommand = new RelayCommand(() => _ = UndoRedoAsync(redo: true), () => CanRedo && !IsRenaming);
        ShowContextMenuCommand = new RelayCommand(ShowContextMenuForSelection, () => !IsRenaming);
        ConfirmPathCommand = new RelayCommand(ConfirmPath);
        CancelPathEditCommand = new RelayCommand(CancelPathEdit);
        CompletePathCommand = new RelayCommand(CompletePath);

        // 剪贴板载荷变化（含被其他页面/操作清空）→ 刷新粘贴命令可用性
        Clipboard.ClipboardChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
    }

    // —— 排序（服务端排序：视图层共享数据表控件 SortableDataTable 点列头后经 SortChanged 事件转到这里）——

    private const string DefaultSortBy = "title";
    private const string DefaultSortOrder = "asc";

    public string SortBy { get; private set; } = DefaultSortBy;
    public string SortOrder { get; private set; } = DefaultSortOrder;

    /// <summary>
    /// 应用排序（字段与方向已由共享表控件切换完毕）并重排。
    /// 走 RefreshPreservingSelectionAsync：重排不丢选中（Windows 点列头也不丢）。
    /// 点列头 = 真刷新：刚置入项的临时置尾同时归位（Windows 口径）。
    /// </summary>
    public void ApplySort(string? field, bool ascending)
    {
        if (string.IsNullOrEmpty(field)) return;
        SortBy = field;
        SortOrder = ascending ? "asc" : "desc";
        ClearRecentlyPinned();
        _ = RefreshPreservingSelectionAsync();
    }

    /// <summary>进入指定目录（null = 根）。首次显示页面时调用 LoadAsync(null)。
    /// 这是"用户发起的导航"（树行点击/面包屑/后退前进/返回上级/F5）→ 带加载遮罩（真刷新，
    /// 在 <see cref="RefreshAsync"/> 里归位置尾）；导航即作废未消费的粘贴定位请求。</summary>
    public async Task LoadAsync(string? folderId)
    {
        _pendingFocusId = null;
        Controller.NavigateTo(folderId);
        CurrentFolderId = Controller.CurrentFolderId;
        await RefreshAsync(navigating: true);
    }

    /// <summary>
    /// 重新加载当前目录（事件推送订阅 / 导航显式调用；写操作不自行刷新，见 WARNINGS #18）。
    /// 选中的唯一事实来源是 <see cref="_selectedIds"/>（行与树均为投影），故此方法本身不恢复选中——
    /// 集合并未因刷新而消失。仅当 <paramref name="clearSelection"/> 为 true（导航切换目录）时清空选中。
    /// 重入守卫 = 「最后请求必被处理」：加载进行中又来新请求（导航切换 / 防抖事件刷新）只置挂起标志，
    /// 当前加载收尾后自动补刷一次——绝不静默吞掉请求（曾导致：导航后列表停在旧目录）。
    /// </summary>
    public async Task RefreshAsync(bool clearSelection = false, bool navigating = false)
    {
        // 导航切换目录：清空选中集合（行/树投影一起归零）；原地刷新则保留
        if (clearSelection)
        {
            _selectedIds.Clear();
            _anchorId = null;
        }
        if (IsLoading)
        {
            _refreshPending = true;
            _clearSelectionOnPendingRefresh |= clearSelection;
            _navigatingOnPendingRefresh |= navigating;
            return;
        }
        // 数据持续高频变动时，补刷递归不能无限延续（见 finally 内的深度计数）
        if (_refreshRecursionDepth >= MaxRefreshRecursion)
        {
            _refreshPending = false;   // 弃掉挂起：交还 300ms 事件防抖继续追平（不丢数据，只是晚一拍）
            return;
        }
        // 遮罩只在"这次加载真的开始了"且属于**用户发起的导航/刷新**时亮：被挂起/被丢弃的请求不亮，
        // 挂起补刷按 _navigatingOnPendingRefresh 逐轮继承（事件驱动的后台刷新一律静默，不闪动画）。
        // 真刷新（导航加载 = 进入目录 / 点当前位置重载 / F5）同时让"刚置入项临时置尾"归位（Windows 口径）。
        if (navigating) { IsNavigating = true; _navigatingInChain = true; ClearRecentlyPinned(); }
        IsLoading = true;
        try
        {
            // 单快照：folders.overview 一次返回 目录页+全量树+根级计数，
            // 三个数据源在引擎同一读池 UoW 内（不再跨命令漂移；原三连查 FolderContents/Tree/Stats 已收敛为一条）。
            var contents = await _client.FoldersOverviewAsync(Controller.CurrentFolderId, sortBy: SortBy, sortOrder: SortOrder);

            // 文件夹映射：面包屑 + 返回上级需要父链；同时重建左侧文件夹树
            //（与目录页同快照的树/计数 + 全量链接叶子：每文件夹直接链接一并注入，Windows 资源管理器语义）
            var tree = contents.Tree ?? new List<FolderDto>();
            _folderMap = tree.ToDictionary(f => f.FolderId, f => (f.ParentId, f.Name));
            RebuildFolderTree(tree, contents.RootLinkCount ?? 0, contents.TreeLinks ?? new List<LinkDto>());
            // 树已重建：选中态由 _selectedIds（唯一事实）派生重放，无需容器时序

            Rows.Clear();
            SetContextRow(null); // 行对象已重建：右键命中行引用作废（删除文案随之复位）

            // Windows 逻辑：升序时文件夹在前，降序时文件夹在后（任何排序维度都如此）
            var folderRows = new List<BrowserRowViewModel>();
            foreach (var folder in contents.SubFolders)
            {
                folderRows.Add(new BrowserRowViewModel(folder.FolderId, isFolder: true, folder.Name)
                {
                    LinkCount = folder.LinkCount,
                    ModifiedAt = folder.UpdatedAt, // 内核维护：文件夹内容（含子孙）最后变动时间
                    CreatedAt = folder.CreatedAt,  // 内核维护：文件夹创建时间
                    LastViewedAt = folder.LastVisitedAt, // 内核维护：子孙链接被查看时沿父链刷新
                    ViewCount = folder.VisitCount,       // 内核维护：子孙链接被查看时沿父链 +1
                    Host = this,
                    IsCut = IsCutInClipboard(folder.FolderId, true)
                });
            }

            var linkRows = new List<BrowserRowViewModel>();
            foreach (var link in contents.Links)
            {
                linkRows.Add(new BrowserRowViewModel(link.LinkId, isFolder: false, link.Title)
                {
                    Url = link.Url,
                    ModifiedAt = link.UpdatedAt,        // 内核维护：内容变动时间（查看不影响）
                    CreatedAt = link.CreatedAt,          // 内核维护：链接创建时间
                    LastViewedAt = link.LastVisitedAt,   // 内核维护：链接最后查看时间
                    ViewCount = link.VisitCount,         // 内核维护：链接查看次数
                    Favicon = Services.FaviconService.LoadFromCache(link.FaviconUrl),
                    Host = this,
                    IsCut = IsCutInClipboard(link.LinkId, false)
                });
            }

            // favicon 懒加载清单：磁盘缓存未命中时后台拉取，完成后补到对应行
            var missing = linkRows
                .Where(r => r.Favicon == null)
                .Select(r => contents.Links.First(l => l.LinkId == r.Id).FaviconUrl)
                .Where(url => !string.IsNullOrEmpty(url))
                .Distinct()
                .ToList();

            // 组装顺序：升序 = 文件夹 → 链接；降序 = 链接 → 文件夹（Windows 逻辑）。
            // ⚠️ 行必须先同步就位（favicon 属附属数据，网络预取绝不阻塞行渲染——
            //    曾因「await 预取再建行」在网络慢时把 Rows 长时间留在上一目录，跳转定位读到旧行集 → RowMissing 间歇回归）。
            var ordered = SortOrder == "desc"
                ? linkRows.Concat(folderRows).ToList()
                : folderRows.Concat(linkRows).ToList();

            // 临时置尾（Windows）：刚粘贴的项追加到列表末尾（不参与排序），直到真刷新才按排序归位。
            // 只对"属于当前目录且此刻仍在数据里"的 ID 生效——已被移走/删除的置尾项自动跳过。
            var pinned = ActivePinnedIds();
            if (pinned.Count == 0)
            {
                foreach (var row in ordered) Rows.Add(row);
            }
            else
            {
                var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);
                var byId = ordered.ToDictionary(r => r.Id, StringComparer.Ordinal);
                foreach (var row in ordered)
                    if (!pinnedSet.Contains(row.Id)) Rows.Add(row);
                foreach (var id in pinned)
                    if (byId.TryGetValue(id, out var row)) Rows.Add(row);
            }

            // favicon 后台预取 + Dispatcher 回填：行已可见，失败只丢图标（下次事件刷新追平）
            if (missing.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.WhenAll(missing.Select(Services.FaviconService.PrefetchAndCacheAsync)); }
                    catch { /* 网络失败属预期波动，行保持无图标 */ }
                    foreach (var row in linkRows.Where(r => r.Favicon == null))
                    {
                        var dto = contents.Links.FirstOrDefault(l => l.LinkId == row.Id);
                        if (dto != null)
                        {
                            var img = Services.FaviconService.LoadFromCache(dto.FaviconUrl);
                            if (img != null)
                                System.Windows.Application.Current?.Dispatcher.Invoke(() => row.SetFavicon(img));
                        }
                    }
                });
            }

            // 选中的唯一事实来源是 _selectedIds：Rows 已重建且行是投影，这里只需把集合同步到
            // 主栏行 + 树 + 派生状态（数量/详情/命令）。不改变 _selectedIds 本身。
            ApplySelectionToView();
            // 重命名态同样是投影：刷新重建行/树后按会话状态重放（编辑框在重建出的行/节点上重新出现并自动聚焦）
            ApplyRenameToView();

            // 粘贴完成后的定位：新行已在本轮重建中就位 → 滚入视口（行不在本轮数据里则留待下次刷新）
            ConsumePendingFocus();

            // 面包屑（含 ID，可点击跳转；最后一级为当前目录，高亮显示）
            Breadcrumbs.Clear();
            var chain = BuildBreadcrumbIds(Controller.CurrentFolderId).ToList();
            Breadcrumbs.Add(new BrowserCrumbViewModel(null, "全部书签") { IsLast = chain.Count == 0 });
            for (int i = 0; i < chain.Count; i++)
            {
                Breadcrumbs.Add(new BrowserCrumbViewModel(chain[i].Id, chain[i].Name)
                {
                    IsLast = i == chain.Count - 1
                });
            }

            StatusText = $"共 {contents.SubFolders.Count + contents.Links.Count} 项" +
                         $"（{contents.SubFolders.Count} 个文件夹 / {contents.Links.Count} 个链接）";
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            Services.Logger.Error("浏览目录刷新失败", ex);   // 失败必须留痕，不能只有一行状态文案
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
            // 撤销/重做可用性轻量同步（Ctrl+Z/Y 的 CanExecute 要准）：每次刷新链收尾取一次 undo 栈态。
            // 只读查询、不产生事件 → 不会引发刷新循环；失败静默保持保守禁用（见 RefreshUndoStateAsync）。
            _ = RefreshUndoStateAsync();

            // 加载期间有新的刷新请求（导航/防抖事件）→ 立即补刷一次，保证最后请求被处理
            if (_refreshPending)
            {
                _refreshPending = false;
                var clear = _clearSelectionOnPendingRefresh;
                _clearSelectionOnPendingRefresh = false;
                var nav = _navigatingOnPendingRefresh;
                _navigatingOnPendingRefresh = false;
                _refreshRecursionDepth++;
                try { await RefreshAsync(clearSelection: clear, navigating: nav); }
                finally { _refreshRecursionDepth--; }
            }
            else
            {
                IsNavigating = false;   // 本轮（含挂起补刷链）全部结束 → 收加载遮罩
                var wasNavigation = _navigatingInChain;
                _navigatingInChain = false;
                RefreshCompleted?.Invoke(this, wasNavigation);   // 链结束只发一次（行入场动画据此判定）
            }
        }
    }

    // —— 交互 ——

    /// <summary>主栏单选：把选中集合收敛为仅 <paramref name="row"/>.Id（唯一事实来源写入）。</summary>
    private void SelectRow(BrowserRowViewModel? row)
    {
        if (row == null) return;
        SetSelection(new[] { row.Id }, row.Id);
    }

    /// <summary>
    /// 带修饰键的选择路由（由视图在鼠标抬起时调用，读 Keyboard.Modifiers）。
    /// 修改的是 <see cref="_selectedIds"/>（唯一事实来源），绝不直接改行状态；
    /// 计算出的目标集合经 <see cref="SetSelection"/> 一处落盘并投影到主栏行 + 树。
    /// </summary>
    public void SelectRowWithModifiers(BrowserRowViewModel? row, ModifierKeys mods)
    {
        if (row == null) return;

        if (mods.HasFlag(ModifierKeys.Control))
        {
            // 单行翻转：基于当前集合增/删目标 ID
            SetSelection(mutate: s =>
            {
                if (!s.Remove(row.Id)) s.Add(row.Id);
            }, anchor: _anchorId ?? row.Id);
        }
        else if (mods.HasFlag(ModifierKeys.Shift))
        {
            var anchor = Rows.FirstOrDefault(r => r.Id == _anchorId) ?? row;
            var i1 = Rows.IndexOf(anchor);
            var i2 = Rows.IndexOf(row);
            if (i1 > i2) (i1, i2) = (i2, i1);
            SetSelection(Rows.Where((_, i) => i >= i1 && i <= i2).Select(r => r.Id), _anchorId ?? row.Id);
        }
        else
        {
            SetSelection(new[] { row.Id }, row.Id);
        }
    }

    public void SelectAllRows()
    {
        SetSelection(Rows.Select(r => r.Id), _anchorId ?? Rows.FirstOrDefault()?.Id ?? string.Empty);
    }

    /// <summary>
    /// 选中唯一的写入入口：本次调用是主栏选中/清除动作的目标 ID 集，全部落在 <see cref="_selectedIds"/>，
    /// 随后把集合投影到主栏行 + 目录树 + 派生状态。任何选择路径（行点击/树点击/全选/清空）都只调这一个方法，
    /// 不直接在行对象或树节点上写选中——选中事实来源唯一、且跨 Rows/Tree 重建存活。
    /// </summary>
    private void SetSelection(IEnumerable<string>? ids = null, string? anchor = null, Action<HashSet<string>>? mutate = null)
    {
        // ⚠️ mutate 的起点必须是**当前集合**：Ctrl 翻转 = "在当前选中上增/删目标 ID"。
        // 曾把起点写成空集合 → Ctrl+点击退化成单选（多选永远做不到），用例已锁死。
        var next = ids != null
            ? new HashSet<string>(ids, StringComparer.Ordinal)
            : new HashSet<string>(_selectedIds, StringComparer.Ordinal);
        mutate?.Invoke(next);
        _selectedIds.Clear();
        foreach (var id in next) _selectedIds.Add(id);
        _anchorId = string.IsNullOrEmpty(anchor) ? _anchorId : anchor;
        ApplySelectionToView();
    }

    /// <summary>把指定 ID 纳入选中集合（不清空其他选中）并投影两栏——用于从详情页返回等"还原选中"语义。</summary>
    public void RestoreSelection(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _selectedIds.Add(id);
        _anchorId ??= id;
        ApplySelectionToView();
    }

    /// <summary>
    /// 视图应把某一行滚入视口（定位/跳转后保证选中项可见）。
    /// 由 <see cref="NavigateAndSelectAsync"/> 触发，BrowserView 订阅处理；
    /// 视图不在场（无 UI 的会话）时无人订阅也不影响数据层结果。
    /// </summary>
    public event EventHandler<BrowserRowViewModel>? FocusRowRequested;

    /// <summary>
    /// 进入指定目录并选中其中一行（行可为链接或文件夹）——「跳转」的浏览页执行原语。
    /// 由定位组件（Services/ContentLocator）经 IBrowserLocateHost 端口调用；
    /// 目录与选中逻辑属于浏览页自身领域，故实现在此，界面只需滚动。
    /// 返回该行是否存在并被选中。
    /// </summary>
    /// <summary>
    /// 进入指定目录并选中其中一行（行可为链接或文件夹）——「跳转」的浏览页执行原语。
    /// 选中直接写 <see cref="_selectedIds"/>（唯一事实来源），不依赖行对象引用：
    /// 即使该行未在当前 Rows（分页/目录重建中），ID 也照常落在选中集合，树叶子按
    /// <see cref="SyncTreeSelection"/> 同步高亮；此后导航成功该行出现在 Rows 即由主栏行投影选中。
    /// 返回 true 表示定位目标已纳入选中集合；界面可据此滚动。
    /// </summary>
    public async Task<bool> NavigateAndSelectAsync(string? folderId, string rowId)
    {
        if (string.IsNullOrEmpty(rowId)) return false;

        // 已在目标目录时不必重载（避免无谓的列表重建与闪烁）
        // 注意：链接叶子定位=进根（folderId null）、CurrentFolderId 已是 null 时也直接下单选集，
        // 无需重载，避免异步重建导致"主栏闪一下"。
        if (Controller.CurrentFolderId != folderId)
            await LoadAsync(folderId);

        SetSelection(new[] { rowId }, rowId);
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row != null) FocusRowRequested?.Invoke(this, row);
        return true;
    }

    /// <summary>
    /// 点击树节点统一入口（展开 ≠ 选中 ≠ 进入，三者物理分离）：
    /// chevron 只负责展开/收起（模板内独立控件，绝不进入此方法）；行主体单击才到此。
    /// 只有两类行、两个动词，零特例：
    /// · **位置行**（文件夹 / 虚根「全部书签」）= 进入目录：无条件 `LoadAsync`——
    ///   点是当前位置同样重载刷新一次（Windows 口径：点当前文件夹、已在根点「全部书签」都刷新）；
    ///   重复导航不污染历史（<see cref="BrowserHistory.NavigateTo"/> 对同目录直接忽略）；
    ///   重载也不动选中（选中是独立集合，重载后按 ID 重新投影）。
    ///   两类位置行唯一差异 = 有没有实体身份：文件夹把自己写入选中集合（单击 = 选中该文件夹 + 进入）；
    ///   虚根 <c>FolderId == null</c>（不是实体、没有可高亮的身份）→ 只进入、不写选中。
    /// · **实体行**（链接叶子）= 定位：进入其所属目录（已在目标目录则免重载——行本来就在，无谓重建只会闪烁）
    ///   并把该链接写入选中集合。
    /// 树自身不持有持久选中状态：高亮完全由 <see cref="SyncTreeSelection"/> 从 <see cref="_selectedIds"/>
    /// 派生，与主栏行选中同一唯一事实来源，二者天然一致。
    /// </summary>
    public async Task SelectTreeNodeAsync(FolderNode node)
    {
        if (node.IsLink)
        {
            // 实体行：把链接 ID 写入选中集合（唯一事实），主栏与树同时投影高亮；
            // 即使该行尚未出现在 Rows（分页），也先记录选中，由导航/刷新投影补齐。
            SetSelection(new[] { node.Id }, node.Id);
            await NavigateAndSelectAsync(node.ParentId, node.Id);
            return;
        }

        // 位置行：进入目录（无条件重载 = 点是当前位置也刷新）；只有真实文件夹有实体身份，虚根不写选中。
        // 树高亮由 _selectedIds 派生，与"进入"本身无关：位置仍由面包屑表达（位置 ≠ 选中）。
        if (node.FolderId != null) SetSelection(new[] { node.FolderId }, node.FolderId);
        await LoadAsync(node.FolderId);
    }

    /// <summary>遍历整棵树（含虚拟根「全部书签」），返回全部节点的深度优先序列。</summary>
    private IEnumerable<FolderNode> AllTreeNodes()
    {
        foreach (var root in FolderTree)
            foreach (var node in EnumerateSelfAndChildren(root))
                yield return node;
    }

    private static IEnumerable<FolderNode> EnumerateSelfAndChildren(FolderNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var sub in EnumerateSelfAndChildren(child))
                yield return sub;
    }

    public void ClearSelection()
    {
        SetSelection(Enumerable.Empty<string>(), anchor: null);
    }

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

    /// <summary>由 folders.overview 的树快照重建左侧树（ParentId == null 即根级）。保留既有展开状态。
    /// rootLinkCount = 同快照的根级直挂链接数；treeLinks = 同快照的全库活动链接（每文件夹直接链接叶子注入源）。
    /// 纯同步：无 IO/等待，签名用 void 不误导调用方。
    /// ParentId == FolderId 的自环坏数据排除（绝不把自己挂成自己的子节点）。</summary>
    private void RebuildFolderTree(List<FolderDto> tree, int rootLinkCount, List<LinkDto> treeLinks)
    {
        var expandedIds = new HashSet<string?>();
        CollectExpandedIds(FolderTree, expandedIds);

        FolderTree.Clear();

        var root = new FolderNode { IsRoot = true, Name = FolderIds.RootDisplayName, IconKind = "folder-open-outline", IsExpanded = true, Host = this };
        var nodes = tree.ToDictionary(
            f => f.FolderId,
            f => new FolderNode
            {
                FolderId = f.FolderId,
                ParentId = f.ParentId,
                Name = f.Name,
                LinkCount = f.LinkCount,
                Host = this,
                IsExpanded = expandedIds.Contains(f.FolderId)
            });

        foreach (var node in nodes.Values)
        {
            if (node.ParentId != null
                && node.ParentId != node.FolderId   // 自环坏数据 → 按根级兜底，避免自引用节点
                && nodes.TryGetValue(node.ParentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                root.Children.Add(node);
            }
        }

        // 每文件夹直接链接叶子（全量注入，Windows 资源管理器语义：展开任意文件夹可见其直接书签）。
        // 用 ToLookup（允许 null 键 = 根级链接）分组，按所属目录挂到对应节点下
        var linksByParent = treeLinks.ToLookup(l => l.ListId);
        foreach (var node in nodes.Values)
            AppendTreeLinkLeaves(node, linksByParent[node.FolderId]);
        AppendTreeLinkLeaves(root, linksByParent[null]);

        // 根节点计数 = 顶层文件夹递归计数之和 + 根级直挂链接数（内核递归计数）
        root.LinkCount = tree.Where(f => f.ParentId == null)
            .Sum(f => f.LinkCount) + rootLinkCount;

        FolderTree.Add(root);
    }

    /// <summary>把某文件夹的直接链接作为叶子挂到该节点下：名称升序（树唯一排序口径）；
    /// 叶子 Id = 链接 ID、FolderId = null、ParentId = 所属目录（定位 = 进父目录 + 选中该行）。
    /// 文件夹节点先于链接组已由构建顺序保证（链接组恒排在文件夹之后，Windows 口径）。</summary>
    private void AppendTreeLinkLeaves(FolderNode folder, IEnumerable<LinkDto> links)
    {
        foreach (var l in links.OrderBy(l => l.Title, StringComparer.CurrentCulture))
        {
            folder.Children.Add(new FolderNode
            {
                IsLink = true,
                Id = l.LinkId,
                ParentId = folder.FolderId,
                Name = string.IsNullOrWhiteSpace(l.Title) ? (l.Url ?? "") : l.Title,
                Host = this
            });
        }
    }

    private static void CollectExpandedIds(IEnumerable<FolderNode> nodes, HashSet<string?> ids)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.FolderId != null) ids.Add(node.FolderId);
            CollectExpandedIds(node.Children, ids);
        }
    }

    // —— 右键菜单 / 拖拽移动（参考 Files、Alist 的文件管理范式）——

    /// <summary>目标文件夹是否为 folderId 自身或其后代（用于阻止把文件夹移进自己）。</summary>
    public bool IsSelfOrDescendant(string folderId, string? targetId)
    {
        var current = targetId;
        var visited = new HashSet<string>();   // 环保护：坏数据（父链成环）时终止而非死循环
        while (current != null && visited.Add(current))
        {
            if (current == folderId) return true;
            current = _folderMap.TryGetValue(current, out var info) ? info.ParentId : null;
        }
        return false;
    }

    /// <summary>
    /// 拖拽载荷构造（拖动集合的**唯一出口**）：把唯一选中集合 <see cref="_selectedIds"/> 投影成载荷项。
    /// 解析顺序：当前主栏行 → 目录树（树选中但不在当前视图的文件夹 / 链接叶子）。
    /// 解析不到的 ID（实体已被外部删除等）不进载荷——移动逻辑按真实数据校验，绝不猜类型。
    /// </summary>
    private IReadOnlyList<DragItem> BuildDragItems()
    {
        var items = new List<DragItem>();
        foreach (var id in _selectedIds)
        {
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row != null)
            {
                items.Add(new DragItem(row.Id, row.IsFolder, row.Name));
                continue;
            }

            var node = AllTreeNodes().FirstOrDefault(n => n.IsLink ? n.Id == id : n.FolderId == id);
            if (node != null) items.Add(new DragItem(id, !node.IsLink, node.Name));
        }
        return items;
    }

    /// <summary>
    /// 主栏行拖拽起点：未选中 → 先单选该行（Explorer 口径：拖未选中项先选中）；已选中 → 拖动整个选中集合。
    /// 返回本次拖动的载荷快照（视图据此调 DoDragDrop）。
    /// </summary>
    public IReadOnlyList<DragItem> PrepareDragFromRow(BrowserRowViewModel? row)
    {
        if (row == null) return [];
        if (!row.IsSelected) SelectRowWithModifiers(row, ModifierKeys.None);
        return BuildDragItems();
    }

    /// <summary>
    /// 树节点拖拽起点：语义与主栏**完全一致**（未选中 → 先单选该节点；已选中 → 拖动整个选中集合）。
    /// 实体 ID：文件夹 = <c>FolderId</c>、链接叶子 = <c>Id</c>；「全部书签」虚根不是实体 → 空载荷（不可拖）。
    /// 选中仍只经 <see cref="SetSelection"/>（唯一写入入口）落盘，不在此旁路写节点状态。
    /// </summary>
    public IReadOnlyList<DragItem> PrepareDragFromNode(FolderNode? node)
    {
        if (node == null) return [];
        var id = node.IsLink ? node.Id : node.FolderId;
        if (string.IsNullOrEmpty(id)) return [];
        if (!_selectedIds.Contains(id)) SetSelection(new[] { id }, id);
        return BuildDragItems();
    }

    /// <summary>批量拖拽 / 移动入口（载荷 = <see cref="DragItem"/>：主栏行与树节点拖拽共用同一条路径）。
    /// targetFolderId 为 null 表示根。非法项（目标在自身子树内、已在目标目录）逐项跳过。</summary>
    public async Task MoveItemsAsync(IReadOnlyList<DragItem> items, string? targetFolderId)
    {
        var target = targetFolderId;
        var moved = 0;
        var renamedNotes = new List<string>();
        // 撤销分组：一次拖拽多选 = 一个用户动作 → 合并为一条撤销记录
        var callOptions = new LinkPocket.Contracts.CallOptions(UndoGroupId: Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var item in items)
            {
                var id = item.Id;
                if (item.IsFolder)
                {
                    if (id == target || IsSelfOrDescendant(id, target)) continue;
                    if (NormalizeParentId(_folderMap.TryGetValue(id, out var info) ? info.ParentId : null) == target)
                        continue; // 已在目标目录
                    if (await MoveFolderAsync(id, target, renamedNotes, callOptions) == OpOutcome.Done) moved++;
                }
                else
                {
                    if (await MoveLinkAsync(id, target, callOptions) == OpOutcome.Done) moved++;
                }
            }
            StatusText = moved > 0 ? $"已移动 {moved} 项{FormatRenamedNotes(renamedNotes)}" : "没有需要移动的项目";
        }
        catch (Exception ex)
        {
            StatusText = "移动失败";
            ShowError("移动失败", ex.Message);
        }
        // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync），与删除流同口径：
        // 显式 + 事件双重刷新 = "移动/粘贴后主栏刷两遍"的根因；写操作也不得占用 IsLoading
        // （它是刷新的重入标志，被写操作占用期间事件刷新会被挂起）。
    }

    /// <summary>
    /// 单项移动/复制结果（**三态**，结果文案必须如实分派）：
    /// <see cref="Done"/> = 已执行；<see cref="Skipped"/> = 无操作跳过（同目录，不是错误）；
    /// <see cref="Failed"/> = 执行失败（源已消失、引擎拒绝等，必须留痕）。
    /// </summary>
    private enum OpOutcome { Done, Skipped, Failed }

    /// <summary>移动文件夹（目标层同层唯一编号由引擎负责，见 Kernel IFolderNaming）。
    /// 单项失败不中断整批（与 MoveLink/Copy* 一致），失败必须留痕（观测面铁律）。</summary>
    private async Task<OpOutcome> MoveFolderAsync(string folderId, string? target, List<string> renamedNotes,
        LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : "文件夹";
            var moved = await _client.FolderMoveAsync(folderId, target, o);
            // 编号由引擎统一负责（单一实现）；UI 只按返回名生成提示，绝不自己再补发一条改名命令
            //（那正是"移动 + 改名两次写、且批量内各算各的"造成 6 个同名文件夹的根因）
            var resolved = moved.Data?.Name;
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, name, StringComparison.Ordinal))
                renamedNotes.Add($"「{name}」→「{resolved}」");
            return OpOutcome.Done;
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"移动文件夹「{folderId}」失败", ex);
            return OpOutcome.Failed;
        }
    }

    /// <summary>移动链接：同目录 = <see cref="OpOutcome.Skipped"/>（无操作，非错误）；源已消失/引擎拒绝 = 失败（留痕）。</summary>
    private async Task<OpOutcome> MoveLinkAsync(string linkId, string? target, LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取后 FirstOrDefault）
            if (link == null)
            {
                Services.Logger.Error($"移动链接「{linkId}」失败：源已不存在");
                return OpOutcome.Failed;
            }
            if (NormalizeParentId(link.ListId) == target) return OpOutcome.Skipped;
            await _client.LinkUpdateAsync(linkId, listId: target, o: o);
            return OpOutcome.Done;
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"移动链接「{linkId}」失败", ex);
            return OpOutcome.Failed;
        }
    }

    /// <summary>把父目录 ID 归一化成可比较的值（null = 根）。</summary>
    private static string? NormalizeParentId(string? parentId) => parentId;

    private static string FormatRenamedNotes(List<string> notes)
        => notes.Count > 0 ? $"（重命名：{string.Join("、", notes)}）" : string.Empty;

    // —— 剪切 / 复制 / 粘贴（Ctrl+X / C / V）——

    // —— 刚置入项临时置尾（Windows 资源管理器语义）——
    // 粘贴（复制/剪切）完成后，新项**临时排在列表末尾**（不参与排序、不按名称归位），并被选中、滚入视口——
    // 文件多、滚到中部的场景下也能立刻看到刚粘贴的东西（微软官方口径：避免文件多时找不到）。
    // **只有真刷新才归位**：重新进入目录（含点当前目录的树行/虚根）、点列头排序、F5（用户发起的导航刷新）；
    // 后台事件刷新（写操作后的 300ms 防抖）**绝不归位**——否则粘贴后的那次刷新就把置尾效果抹掉了。

    /// <summary>置尾 ID（按置入顺序，后一批在后）；仅对 <see cref="_pinnedFolderId"/> 目录生效。</summary>
    private readonly List<string> _recentlyPinned = new();

    /// <summary>置尾所属目录（null = 根目录）；与当前目录不一致时置尾自动失效并清空。</summary>
    private string? _pinnedFolderId;

    /// <summary>粘贴完成后的定位目标（滚入视口）；行重建（事件刷新）后被消费一次。</summary>
    private string? _pendingFocusId;

    /// <summary>记录刚置入的项（粘贴完成时调用）：同 ID 先移除再追加（后到者排更后）。</summary>
    private void MarkRecentlyPinned(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        _pinnedFolderId = Controller.CurrentFolderId;
        foreach (var id in list) _recentlyPinned.Remove(id);
        _recentlyPinned.AddRange(list);
    }

    /// <summary>清空置尾（真刷新：导航加载 / 点列头排序 / F5）。</summary>
    private void ClearRecentlyPinned()
    {
        _recentlyPinned.Clear();
        _pinnedFolderId = null;
    }

    /// <summary>当前生效的置尾 ID（目录不匹配即失效清空——换目录后置尾无意义）。</summary>
    private IReadOnlyList<string> ActivePinnedIds()
    {
        if (_recentlyPinned.Count == 0) return _recentlyPinned;
        if (_pinnedFolderId != Controller.CurrentFolderId) ClearRecentlyPinned();
        return _recentlyPinned;
    }

    /// <summary>消费粘贴定位请求：把目标行滚入视口（行不在本轮数据里则保持待命，下轮再试）。</summary>
    private void ConsumePendingFocus()
    {
        if (_pendingFocusId == null) return;
        var row = Rows.FirstOrDefault(r => r.Id == _pendingFocusId);
        if (row == null) return;
        _pendingFocusId = null;
        FocusRowRequested?.Invoke(this, row);
    }

    // —— Esc 分层（Windows 口径）——

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
        if (Clipboard.BrowserPayload is { IsCut: true, IsEmpty: false })
        {
            CancelCut();
            return;
        }
        ClearSelection();
    }

    /// <summary>取消剪切：清空剪贴板载荷（复制载荷不受影响——Windows 里 Esc 只取消剪切），复位行半透明视觉。</summary>
    private void CancelCut()
    {
        Clipboard.SetBrowserPayload(null);
        foreach (var r in Rows) r.IsCut = false;
        StatusText = "已取消剪切";
    }

    private LinkPocket.Managers.BrowserClipboardPayload BuildPayload(IReadOnlyList<BrowserRowViewModel> source, bool isCut) => new()
    {
        FolderIds = source.Where(r => r.IsFolder).Select(r => r.Id).ToList(),
        LinkIds = source.Where(r => !r.IsFolder).Select(r => r.Id).ToList(),
        SourceFolderId = Controller.CurrentFolderId,
        IsCut = isCut
    };

    private void CutSelection()
    {
        var sel = SelectedRows.ToList();
        if (sel.Count == 0) return;
        Clipboard.SetBrowserPayload(BuildPayload(sel, isCut: true));
        foreach (var r in Rows) r.IsCut = false;
        foreach (var r in sel) r.IsCut = true;
        StatusText = $"已剪切 {sel.Count} 项（Ctrl+V 粘贴到目标文件夹）";
    }

    private void CopySelection()
    {
        var sel = SelectedRows.ToList();
        if (sel.Count == 0) return;
        Clipboard.SetBrowserPayload(BuildPayload(sel, isCut: false));
        foreach (var r in Rows) r.IsCut = false; // 复制覆盖剪切，清除半透明视觉
        StatusText = $"已复制 {sel.Count} 项";
    }

    private async Task PasteAsync()
    {
        var payload = Clipboard.BrowserPayload;
        if (payload == null || payload.IsEmpty) return;

        var target = Controller.CurrentFolderId;
        if (payload.IsCut && payload.SourceFolderId == target)
        {
            // 剪切到源目录 = 无操作（Windows 同口径）；但必须明确提示——
            // 含糊的"没反应"曾让用户以为"剪切后粘贴不了 = 数据不一致"（实为同目录粘贴被静默早退）。
            // 载荷**保留**（剪切态不消费）：导航到目标文件夹后仍可粘贴。
            StatusText = "剪切的项目已在当前文件夹中（先进入目标文件夹再粘贴）";
            return;
        }

        var renamedNotes = new List<string>();
        var pasted = 0;
        var failed = 0;
        var blocked = new List<string>();   // 非法目标（自身 / 自己的子文件夹）→ 明确反馈，绝不静默（Explorer 同样拒绝并弹窗）
        var pinnedIds = new List<string>();   // 本次粘贴的落点 ID（复制 = 新 ID；剪切 = 原 ID 不变）→ 置尾 + 选中 + 定位
        // 撤销分组：一次粘贴 = 一个用户动作 → 引擎把这几步合并为**一条**撤销记录（一次 Ctrl+Z 撤销整批）
        var undoGroup = Guid.NewGuid().ToString("N");
        var callOptions = new LinkPocket.Contracts.CallOptions(UndoGroupId: undoGroup);
        try
        {
            foreach (var fid in payload.FolderIds)
            {
                _folderMap.TryGetValue(fid, out var info);
                var name = string.IsNullOrEmpty(info.Name) ? "文件夹" : info.Name;
                if (fid == target || IsSelfOrDescendant(fid, target))
                {
                    blocked.Add(name);   // 放进自己 / 自己的子文件夹：成环，拒绝（Explorer 口径）
                    continue;
                }
                if (payload.IsCut)
                {
                    if (NormalizeParentId(info.ParentId) == target) continue;   // 已在目标目录 = 无操作（非错误）
                    if (await MoveFolderAsync(fid, target, renamedNotes, callOptions) == OpOutcome.Done) { pasted++; pinnedIds.Add(fid); }
                    else failed++;
                }
                else
                {
                    var (outcome, newId) = await CopyFolderAsync(fid, target, renamedNotes, callOptions);
                    if (outcome == OpOutcome.Done && newId != null) { pasted++; pinnedIds.Add(newId); }
                    else failed++;
                }
            }

            foreach (var lid in payload.LinkIds)
            {
                if (payload.IsCut)
                {
                    var outcome = await MoveLinkAsync(lid, target, callOptions);
                    if (outcome == OpOutcome.Done) { pasted++; pinnedIds.Add(lid); }
                    else if (outcome == OpOutcome.Failed) failed++;   // Skipped（已在目标目录）= 无操作
                }
                else
                {
                    var (outcome, newId) = await CopyLinkAsync(lid, target, callOptions);
                    if (outcome == OpOutcome.Done && newId != null) { pasted++; pinnedIds.Add(newId); }
                    else failed++;
                }
            }

            if (pasted > 0)
            {
                // Windows 口径：刚粘贴的项临时置尾 + 被选中 + 滚入视口。
                // 行要等事件刷新重建后才出现（写操作不显式刷新），定位请求先待命、重建后消费（ConsumePendingFocus）。
                MarkRecentlyPinned(pinnedIds);
                SetSelection(pinnedIds, pinnedIds[0]);
                _pendingFocusId = pinnedIds[0];
            }

            // 剪切载荷消费：**只有真的有项被粘贴**才遗忘（Windows 同口径）。
            // 全部被拒/全部失败时必须保留——否则"粘贴进自己的子文件夹被拒"之后，
            // 用户导航到合法位置就再也粘贴不了了（那才是真正的"剪切不见了"）。
            if (payload.IsCut && pasted > 0)
            {
                Clipboard.SetBrowserPayload(null); // 剪切语义：粘贴完成即遗忘（复制载荷保留，可多次粘贴——Windows 同口径）
                foreach (var r in Rows) r.IsCut = false;
            }

            // 结果**如实分派**：成功数 / 失败数 / 无操作 分开说，绝不把"部分失败"含混成"已完成"
            var parts = new List<string>();
            if (pasted > 0) parts.Add($"已粘贴 {pasted} 项{FormatRenamedNotes(renamedNotes)}");
            if (failed > 0) parts.Add($"{failed} 项失败（详见日志）");
            StatusText = parts.Count > 0 ? string.Join("，", parts) : "没有可粘贴的项目";

            // 非法目标（成环）：明确弹窗说明——用户明确操作后"毫无反应"会被读成数据损坏（与"同目录粘贴"同一教训）
            if (blocked.Count > 0) ShowError(BlockedTitle(payload.IsCut), BlockedMessage(payload.IsCut, blocked));
        }
        catch (Exception ex)
        {
            StatusText = "粘贴失败";
            ShowError("粘贴失败", ex.Message);
        }
        // 刷新统一交给后端事件（与移动/删除同口径）：显式 + 事件双重刷新会让主栏刷两遍，
        // 且写操作占用 IsLoading 会让加载遮罩在粘贴期间无谓亮起。
    }

    /// <summary>
    /// 拖拽落到**非法目标（成环：拖到它自己或它的子文件夹）**后由视图调用：
    /// 按 Windows 口径**弹窗说明**（与粘贴共用同一套文案生成——反馈口径只有一处）。
    /// 为什么在拖拽**结束后**才弹：拖拽过程中鼠标还按着，弹窗会打断手势；Windows 也是松手后报错。
    /// </summary>
    public void ReportBlockedDrop(IReadOnlyList<DragItem> items)
        => ShowError(BlockedTitle(isCut: true), BlockedMessage(isCut: true, items.Select(i => i.Name).ToList()));

    /// <summary>非法粘贴目标（成环）的弹窗标题——按动作区分（剪切 = 移动 / 复制 = 复制）。</summary>
    private static string BlockedTitle(bool isCut) => isCut ? "无法移动" : "无法复制";

    /// <summary>
    /// 非法粘贴目标的说明文案（Explorer 口径：明确说清为何不能，而不是"操作后毫无反应"）。
    /// 成环 = 目标是自己或自己的子文件夹（文件夹不能成为自己的后代）；同目录粘贴**不在此列**（那只是无操作）。
    /// </summary>
    private static string BlockedMessage(bool isCut, IReadOnlyList<string> names)
    {
        var action = isCut ? "移动" : "复制";
        if (names.Count == 1)
            return $"无法将文件夹「{names[0]}」{action}到它自己或它的子文件夹里。";
        return $"以下文件夹无法{action}到它们自己或它们的子文件夹里：\n" +
               string.Join("、", names.Select(n => $"「{n}」"));
    }

    /// <summary>深拷贝文件夹（目标层同层唯一编号由引擎负责）。失败必须留痕（观测面铁律）。</summary>
    private async Task<(OpOutcome Outcome, string? NewId)> CopyFolderAsync(string folderId, string? target,
        List<string> renamedNotes, LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : "文件夹";
            var copy = await _client.FolderCopyAsync(folderId, target, o);
            var newId = copy.Data?.NewFolderId;
            if (string.IsNullOrEmpty(newId))
            {
                Services.Logger.Error($"复制文件夹「{name}」失败：引擎未返回新 ID");
                return (OpOutcome.Failed, null);
            }
            // 副本名由引擎编号决定（folders.copy 返回最终名）；UI 只按差异生成提示
            var resolved = copy.Data?.Name;
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, name, StringComparison.Ordinal))
                renamedNotes.Add($"「{name}」→「{resolved}」");
            return (OpOutcome.Done, newId);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"复制文件夹「{folderId}」失败", ex);
            return (OpOutcome.Failed, null);
        }
    }

    /// <summary>复制书签（全量字段）。失败必须留痕（观测面铁律）。
    /// 链接标题**不做唯一化**：链接身份 = URL，标题只是标签（用户 2026-09-19 定稿）。</summary>
    private async Task<(OpOutcome Outcome, string? NewId)> CopyLinkAsync(string linkId, string? target,
        LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取）
            if (link == null)
            {
                Services.Logger.Error($"复制链接「{linkId}」失败：源已不存在");
                return (OpOutcome.Failed, null);
            }

            // 复制书签 = 全量字段（URL/标题/描述/收藏/图标；内核无标签系统，无其它字段可丢）
            var created = await _client.LinkCreateAsync(link.Url,
                title: link.Title,
                description: string.IsNullOrEmpty(link.Description) ? null : link.Description,
                listId: target,
                isImportant: link.IsImportant,
                autoFetchMetadata: false,
                faviconUrl: string.IsNullOrEmpty(link.FaviconUrl) ? null : link.FaviconUrl,
                o: o);
            var newId = created.Data?.LinkId;
            return string.IsNullOrEmpty(newId) ? (OpOutcome.Failed, null) : (OpOutcome.Done, newId);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"复制链接「{linkId}」失败", ex);
            return (OpOutcome.Failed, null);
        }
    }

    /// <summary>刷新重建行后，按剪贴板载荷恢复剪切半透明视觉（仅剪切语义）。</summary>
    private bool IsCutInClipboard(string id, bool isFolder)
    {
        var p = Clipboard.BrowserPayload;
        return p is { IsCut: true } && (isFolder ? p.FolderIds.Contains(id) : p.LinkIds.Contains(id));
    }

    /// <summary>新建文件夹的默认名（Windows 口径；同层撞名由引擎自动编号「新建文件夹 (2)」）。</summary>
    private const string DefaultFolderName = "新建文件夹";

    /// <summary>
    /// 新建文件夹（Windows 口径，**不再弹输入框**）：
    /// 直接以默认名创建（同层撞名由引擎自动编号），随后**立刻进入就地改名**——
    /// 新项临时置尾 + 选中 + 滚入视口 + 聚焦编辑框；行要等事件刷新（300ms 防抖）重建后才出现，
    /// 故改名会话与定位请求先待命，重建时由投影/消费落地（**不做显式刷新**：显式 + 事件双重刷新是"刷两遍"的根因）。
    /// 参数为空 → 在当前目录新建；参数为目标文件夹 ID（行右键）→ 在该文件夹内新建
    /// （那种情况新文件夹不在当前视图里，只如实报告，不进入不可见的改名态）。
    /// </summary>
    private async Task NewFolderAsync(string? parentId)
    {
        // 参数为空 → 当前目录；参数为真实文件夹 ID → 在该文件夹内新建
        var target = FolderIds.IsRoot(parentId) ? Controller.CurrentFolderId : parentId;
        try
        {
            var created = await _client.FolderCreateAsync(DefaultFolderName, parentId: target);
            var newId = created.Data?.FolderId;
            if (string.IsNullOrEmpty(newId)) return;
            var name = created.Data?.Name ?? DefaultFolderName;
            StatusText = $"已创建文件夹「{name}」";

            // 新项不在当前视图（在别的文件夹内新建）→ 无可见行可改名，只报告
            if (NormalizeParentId(target) != NormalizeParentId(Controller.CurrentFolderId)) return;

            // Windows 口径：新项临时置尾（不参与排序）+ 选中 + 滚入视口，并直接进入就地改名
            MarkRecentlyPinned(new[] { newId });
            SetSelection(new[] { newId }, newId);
            _pendingFocusId = newId;
            BeginRename(newId, isFolder: true, name, BrowserPane.Main);
            // 刷新交给后端事件（300ms 防抖）——写操作后不做显式刷新（WARNINGS #18）
        }
        catch (Exception ex)
        {
            ShowError("新建文件夹失败", ex.Message);
        }
    }

    /// <summary>
    /// 就地刷新并保留当前选中。用于两类收尾：
    /// ① 非导航类操作（重命名 / 新建 / 移动 / 粘贴 / 排序 / 从树里删节点）——它们不改变所在目录；
    /// ② 后端数据变更事件驱动的刷新（<c>MainViewModel.OnBackendRefresh</c>，经 UiEventHub 防抖）。
    /// 选中的唯一事实来源是 <see cref="_selectedIds"/>，行/树均为投影，刷新并不抹掉集合，
    /// 故此处只需不带清空标志地刷新（这是保留选中的关键——无需任何"重新选中"步骤）。
    /// 只有"切换目录"才用 <see cref="LoadAsync"/>（它带清空标志）。
    /// </summary>
    public Task RefreshPreservingSelectionAsync()
        => RefreshAsync(clearSelection: false);

    private async Task DeleteNodeAsync(FolderNode? node)
    {
        if (node == null || node.FolderId == null) return; // 根节点「全部书签」不是文件夹
        // Windows 口径：删除 = 移入回收站，不再提示"子文件夹一并删除"
        if (!ConfirmDelete("删除文件夹", $"将文件夹「{node.Name}」移入回收站吗？"))
            return;
        try
        {
            await _client.FolderDeleteAsync(node.FolderId, "trash_links");
            StatusText = $"已删除文件夹「{node.Name}」";
            // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync），
            // 这里不再显式刷新 —— 显式 + 事件双重刷新就是"删完刷两次"的根因。
        }
        catch (Exception ex)
        {
            ShowError("删除失败", ex.Message);
        }
    }

    private void CopyUrl(BrowserRowViewModel? row)
    {
        if (row == null || row.IsFolder || string.IsNullOrEmpty(row.Url)) return;
        try
        {
            System.Windows.Clipboard.SetText(row.Url);
            StatusText = "已复制链接";
        }
        catch { /* 剪贴板被占用时静默 */ }
    }

    private async Task DeleteSelectedAsync()
    {
        var sel = SelectedRows.ToList();
        if (sel.Count == 0) return;
        if (!await ConfirmDeleteAsync(sel)) return;
        await DeleteItemsAsync(sel);
    }

    /// <summary>删除确认文案（Windows 口径：一切删除 = 移入回收站，不罗列子项后果）。实例方法：确认走对话框端口。</summary>
    private Task<bool> ConfirmDeleteAsync(IReadOnlyList<BrowserRowViewModel> items)
    {
        var folders = items.Count(r => r.IsFolder);
        var links = items.Count - folders;
        string msg;
        if (folders > 0 && links > 0)
            msg = $"将选中的 {folders} 个文件夹和 {links} 个链接移入回收站吗？";
        else if (folders > 0)
            msg = folders == 1
                ? $"将文件夹「{items[0].Name}」移入回收站吗？"
                : $"将选中的 {folders} 个文件夹移入回收站吗？";
        else
            msg = links == 1
                ? $"将链接「{items[0].Name}」移入回收站吗？"
                : $"将选中的 {links} 个链接移入回收站吗？";

        return Task.FromResult(ConfirmDelete("删除", msg));
    }

    private async Task DeleteItemsAsync(IReadOnlyList<BrowserRowViewModel> items)
    {
        // 单项失败不中断整批（与 Move/Paste 口径一致）；失败项留痕，成功数如实报
        var deleted = 0;
        var failed = 0;
        foreach (var item in items)
        {
            try
            {
                if (item.IsFolder) await _client.FolderDeleteAsync(item.Id, "trash_links");
                else await _client.LinkTrashAsync(item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                Services.Logger.Error($"删除「{item.Name}」失败（Retry 可跳过该项）", ex);   // 观测面铁律：失败必须暴露
            }
        }

        // 文案按实际结果分派 —— 全部成功 / 全部失败 / 部分成功（原 failed>=deleted 会掩盖"部分成功"）
        StatusText = failed == 0
            ? $"已删除 {deleted} 项"
            : deleted == 0
                ? "删除失败（详见日志）"
                : $"已删除 {deleted} 项，{failed} 项失败";
        if (deleted > 0) ClearSelection();
        // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）——
        // 显式 + 事件双重刷新就是"删完刷两次"的根因。失败项留在列表里，下次再删即可。
    }

    /// <summary>
    /// F2：对当前唯一选中项进入就地改名。编辑面 = 当前活跃栏（Windows 口径：哪一栏有焦点就在哪一栏改），
    /// 树里找不到对应节点时回落到主栏。选中集合是唯一事实来源，两栏共用同一个目标 ID。
    /// </summary>
    private void BeginRenameSelection()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row == null) return;
        if (ActivePane == BrowserPane.Tree)
        {
            var node = AllTreeNodes().FirstOrDefault(n => (n.IsLink ? n.Id : n.FolderId) == row.Id);
            if (node != null) { BeginRenameNode(node); return; }
        }
        BeginRenameRow(row);
    }

    private async Task OpenSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await OpenRowAsync(row);
    }

    private async Task DeleteRowAsync(BrowserRowViewModel? row)
    {
        if (row == null) return;
        // 右键命中的行已在多选集合内 → 批量删除；否则只删该行（Explorer 语义）
        var targets = row.IsSelected && SelectionCount > 1
            ? SelectedRows.ToList()
            : new List<BrowserRowViewModel> { row };
        if (!await ConfirmDeleteAsync(targets)) return;
        await DeleteItemsAsync(targets);
    }

    private IEnumerable<(string Id, string Name)> BuildBreadcrumbIds(string? folderId)
    {
        if (IsAtRoot()) yield break;

        var chain = new List<(string Id, string Name)>();
        var current = folderId;
        var visited = new HashSet<string>();   // 环保护：坏数据（父链成环）时终止而非死循环
        while (!string.IsNullOrEmpty(current) && visited.Add(current) && _folderMap.TryGetValue(current, out var info))
        {
            chain.Add((current, info.Name));
            current = info.ParentId;
        }
        chain.Reverse();
        foreach (var item in chain) yield return item;
    }

    // —— 面包屑内联路径编辑 ——

    /// <summary>
    /// Ctrl+Shift+E：把左栏树展开到当前所在位置（只展开、**不选中**——位置 ≠ 选中）。
    /// 展开链 = 当前目录的祖先链（含自身），让当前目录在树里可见；根目录无需展开（虚根恒展开）。
    /// </summary>
    public void ExpandTreeToCurrentLocation()
    {
        if (IsAtRoot()) return;
        var chain = new HashSet<string>(BuildBreadcrumbIds(Controller.CurrentFolderId).Select(c => c.Id),
            StringComparer.Ordinal);
        foreach (var node in AllTreeNodes())
            if (node.FolderId != null && chain.Contains(node.FolderId))
                node.IsExpanded = true;
    }

    /// <summary>
    /// Ctrl+Shift+C：复制当前目录路径（面包屑文本「全部书签 / A / B」，可被 Alt+D 地址栏解析）。
    /// 只写内部载荷会"复制了但别处粘不出来"，故此处走系统剪贴板（与右键「复制链接」同口径）。
    /// </summary>
    private void CopyCurrentPath()
    {
        var text = GetFolderPathDisplay(Controller.CurrentFolderId);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "已复制路径";
        }
        catch
        {
            StatusText = "复制路径失败（剪贴板被占用）";
        }
    }

    // —— 键盘导航（主栏 ↑/↓/End；左栏 ↑/↓/←/→）——

    /// <summary>
    /// 左栏键盘移动游标 = 上一次键盘落点的**节点对象**（记录"上一个落到哪"以便连续 ↓/↑ 前进）。
    /// 用对象引用而非 Id：虚根「全部书签」没有 Id（根 = null，零哨兵红线），只有引用能表示它。
    /// 与选中（<see cref="_selectedIds"/>）、位置（CurrentFolderId）正交；树重建后引用自然失效 →
    /// 自动回退到"选中实体 → 当前位置"，不会指向已废弃节点。
    /// </summary>
    private FolderNode? _treeNavNode;

    /// <summary>命令参数的方向字面量（↑=+1 语义以"下移"为正）。</summary>
    private static int ParseDirection(object? p)
    {
        var s = (p as string is string str ? str : p?.ToString()) ?? string.Empty;
        return s.Contains("up") ? -1 : s.Contains("down") ? 1 : 0;
    }

    /// <summary>主栏 ↑/↓：基于当前选中的末位行索引 ±delta，单选并滚入视口；无选中则从顶/底开始；到边界停住。</summary>
    private void MoveMainSelection(int delta)
    {
        if (Rows.Count == 0 || delta == 0) return;
        var current = SelectedRows.Select(r => Rows.IndexOf(r)).Where(i => i >= 0).OrderBy(i => i).LastOrDefault(-1);
        var next = current < 0
            ? (delta > 0 ? 0 : Rows.Count - 1)
            : Math.Clamp(current + delta, 0, Rows.Count - 1);
        var target = Rows[next];
        SelectRow(target);
        FocusRowRequested?.Invoke(this, target);
    }

    /// <summary>主栏 End：选中末项并滚入视口。</summary>
    private void SelectLastRow()
    {
        if (Rows.Count == 0) return;
        var target = Rows[Rows.Count - 1];
        SelectRow(target);
        FocusRowRequested?.Invoke(this, target);
    }

    /// <summary>主栏当前实现无多选语义下，ShowContextMenu 需要的"当前行" = 唯一选中行，先聚焦该行再弹菜单。</summary>
    private void ShowContextMenuForSelection()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) FocusRowRequested?.Invoke(this, row);
        ContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>左栏 ↑/↓：沿**可见视觉顺序**移动并落到该节点（语义完全复用 <see cref="SelectTreeNodeAsync"/>）。</summary>
    private void MoveTreeSelection(int delta)
    {
        if (delta == 0) return;
        var flat = VisibleTreeNodes().ToList();
        if (flat.Count == 0) return;

        var current = CurrentTreeIndex(flat);
        var next = current < 0
            ? (delta > 0 ? 0 : flat.Count - 1)
            : Math.Clamp(current + delta, 0, flat.Count - 1);
        var target = flat[next];
        _treeNavNode = target;   // 下一次移动从这次落点继续（游标独立于选中/位置）

        // 落点语义 = 与鼠标点击树行**完全同一条路径**（文件夹 = 选中 + 进入；链接叶子 = 定位；虚根 = 进根不选中）——
        // 键盘绝不另造一套"只移选中"的语义（那会让"点树"与"按树"行为分叉）。
        _ = SelectTreeNodeAsync(target);
    }

    /// <summary>
    /// 树节点的**可见**深度优先序列（= 屏幕上实际看到的行序）：
    /// 未展开的节点其子级不在视觉序列里（用户 2026-09-19 定稿：没展开就没看到子文件夹，不进入子级）；
    /// 虚根恒展开（RebuildFolderTree 里置位），故顶层始终可见。
    /// ⚠️ 不能直接用 <see cref="AllTreeNodes"/>：Children 里含全部子节点（展开只是视觉态），必须按 IsExpanded 过滤。
    /// </summary>
    private IEnumerable<FolderNode> VisibleTreeNodes()
    {
        foreach (var root in FolderTree)
            foreach (var node in Walk(root))
                yield return node;

        static IEnumerable<FolderNode> Walk(FolderNode node)
        {
            yield return node;
            if (!node.IsExpanded) yield break;
            foreach (var child in node.Children)
                foreach (var sub in Walk(child))
                    yield return sub;
        }
    }

    /// <summary>当前"聚焦树节点"索引：优先当前选中的树节点，其次当前所在目录节点（无则 -1 = 由调用方取顶/底）。</summary>
    private int CurrentTreeIndex(List<FolderNode> flat)
    {
        var focused = CurrentFocusedTreeNode();
        if (focused != null)
        {
            var idx = flat.IndexOf(focused);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    /// <summary>
    /// 当前聚焦的树节点，优先级：键盘移动游标（<see cref="_treeNavId"/>，连续 ↓/↑ 的落点）→
    /// 选中实体在树里的节点 → 当前所在目录节点（根目录 = 虚根）→ null（由调用方取顶/底）。
    /// ⚠️ 虚根必须参与：它的 Id 为空，只能按 <see cref="FolderNode.IsRoot"/> 匹配——
    /// 否则"在根目录按 ↓"每次都会重新从顶开始（落点永远停在虚根，走不动）。
    /// </summary>
    private FolderNode? CurrentFocusedTreeNode()
    {
        var all = AllTreeNodes().ToList();

        // ① 键盘游标（仍在当前树里才有效；树重建后旧引用自然落空 → 回退）
        if (_treeNavNode != null && all.Contains(_treeNavNode)) return _treeNavNode;

        // ② 选中实体（鼠标点树 / 上一次键盘落子写下的选中）
        foreach (var node in all)
        {
            if (node.IsLink && _selectedIds.Contains(node.Id)) return node;
            if (node.FolderId != null && _selectedIds.Contains(node.FolderId)) return node;
        }

        // ③ 当前所在目录（根目录 → 虚根行）
        var currentId = Controller.CurrentFolderId;
        foreach (var node in all)
            if (currentId == null ? node.IsRoot : node.FolderId == currentId) return node;
        return null;
    }

    // —— 撤销 / 重做（Ctrl+Z / Ctrl+Y）——
    // 可用性口径 = 引擎两个栈的**真实状态**（undo.list / undo.list_redo），不再靠本地猜测：
    // 新写操作会清空重做栈（标准 redo 语义），本地事实会在那时失真。

    private bool _canUndo;
    private bool _canRedo;
    /// <summary>可撤销（引擎撤销栈非空）。</summary>
    public bool CanUndo => _canUndo && !IsPathEditing;
    /// <summary>可重做（引擎重做栈非空）。</summary>
    public bool CanRedo => _canRedo && !IsPathEditing;

    private async Task UndoRedoAsync(bool redo)
    {
        try
        {
            var result = redo ? await _client.RedoAsync() : await _client.UndoAsync();
            // 撤销/重做成功后：回到受影响实体所在位置并选中它（Windows 资源管理器口径）——
            // 实体 ID 从变更集的 Touched 取（引擎已把逆向命令的受影响实体聚合上来），
            // 复用既有「跳转」语义（进目录 + 选中该行 + 滚入视口），不另造一套导航。
            await RefreshUndoStateAsync();
            await LocateAfterUndoAsync(result.Changes);
        }
        catch (Exception ex)
        {
            ShowError(redo ? "重做失败" : "撤销失败", ex.Message);
        }
    }

    /// <summary>
    /// 撤销/重做后定位到受影响实体：取变更集里第一个链接/文件夹，进其所在目录并选中该行。
    /// 无受影响实体（或引擎未回报）时什么都不做——绝不猜测位置。
    /// </summary>
    private async Task LocateAfterUndoAsync(LinkPocket.Contracts.ChangeSet? changes)
    {
        var touched = changes?.Touched;
        if (touched == null || touched.Count == 0) return;

        foreach (var entity in touched)
        {
            if (entity.Type == "link")
            {
                var link = await _client.LinkGetAsync(entity.Id);
                if (link == null) continue;   // 已被撤销掉（如撤销"新建链接"）→ 试下一个
                await NavigateAndSelectAsync(link.ListId, entity.Id);
                return;
            }
            if (entity.Type == "folder")
            {
                var tree = await _client.FolderTreeAsync();
                var folder = tree.FirstOrDefault(f => f.FolderId == entity.Id);
                if (folder == null) continue;   // 已进回收站（撤销"新建文件夹"）→ 试下一个
                await NavigateAndSelectAsync(folder.ParentId, entity.Id);
                return;
            }
        }
    }

    /// <summary>
    /// 同步撤销/重做可用性 = 引擎两个栈的**真实状态**（undo.list / undo.list_redo，
    /// 每次刷新链收尾与撤销/重做后各取一次）。查询失败时保持保守禁用（观测面纪律：不弹窗打断输入）。
    /// </summary>
    public async Task RefreshUndoStateAsync()
    {
        try
        {
            // ⚠️ 两个查询的返回都是**对象** `{ "entries": [...] }`（不是裸数组）——按数组解析会恒为空
            //（曾据此误判"无可撤销"，Ctrl+Z 永远灰着）。
            _canUndo = await HasEntriesAsync(await _client.UndoListAsync());
            _canRedo = await HasEntriesAsync(await _client.UndoListRedoAsync());
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            CommandManager.InvalidateRequerySuggested();
        }
        catch { /* 查询失败不阻断；CanExecute 保守禁用 */ }
    }

    private static Task<bool> HasEntriesAsync(System.Text.Json.JsonElement list)
        => Task.FromResult(list.ValueKind is System.Text.Json.JsonValueKind.Object
                           && list.TryGetProperty("entries", out var entries)
                           && entries.ValueKind is System.Text.Json.JsonValueKind.Array
                           && entries.GetArrayLength() > 0);

    /// <summary>视图请求：为当前选中行弹右键菜单（Shift+F10 / 菜单键；视图订阅后聚焦并 open 行 ContextMenu）。</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>左栏 ←/→：折叠 / 展开当前树节点（链接叶子 / 虚根无操作）。</summary>
    private void ToggleFocusedTreeExpand()
    {
        var node = CurrentFocusedTreeNode();
        if (node == null || node.IsLink || node.IsRoot) return;
        node.IsExpanded = !node.IsExpanded;
    }

    private void TogglePathEdit()
    {
        if (IsPathEditing) CancelPathEdit();
        else EnterPathEdit();
    }

    private void EnterPathEdit()
    {
        PathEditText = BuildPathText(Controller.CurrentFolderId);
        IsPathInvalid = false;
        IsPathEditing = true;
    }

    private void CancelPathEdit()
    {
        IsPathEditing = false;
        PathCandidates = new List<string>();
    }

    private string BuildPathText(string? folderId)
    {
        var parts = new List<string> { "全部书签" };
        foreach (var (id, name) in BuildBreadcrumbIds(folderId))
            parts.Add(EscapePathSegment(name));   // 名字里的 / 转义为 \/，编辑往返不丢
        return string.Join("/", parts);
    }

    // —— 路径编辑转义 ——
    // 分隔符 / 与文件夹名里的字面 / 冲突：名内 / 以 \/ 转义（\\ 转义 \）。解析侧按
    // "未转义的 /"切段并解码转义对，保证任何名字都能在地址栏无损往返。

    /// <summary>段名 → 地址栏文本（先 \\ 后 /，避免转义序列互相污染）。</summary>
    private static string EscapePathSegment(string name)
        => name.Replace("\\", "\\\\").Replace("/", "\\/");

    /// <summary>地址栏段 → 真实名字（先 \/ 后 \\）。</summary>
    private static string UnescapePathSegment(string seg)
        => seg.Replace("\\/", "/").Replace("\\\\", "\\");

    /// <summary>最后一次"未转义的 /"分隔符的位置（前面反斜杠数为偶）；无则 -1。</summary>
    private static int LastIndexOfPathSeparator(string text)
    {
        var slash = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '/') continue;
            var bs = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--) bs++;
            if (bs % 2 == 0) slash = i;
        }
        return slash;
    }

    /// <summary>按转义规则切分并解码路径段（'\/'= 名字里的字面斜杠，'\\'= 字面反斜杠），剔除空段并修饰空白。</summary>
    private static IEnumerable<string> SplitPathSegments(string text)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\\' || text[i + 1] == '/'))
            {
                current.Append(text[i + 1]);   // 转义对 → 字面字符
                i++;
                continue;
            }
            if (c == '/')
            {
                if (current.Length > 0) segments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) segments.Add(current.ToString().Trim());
        return segments;
    }

    /// <summary>Enter：逐级按名解析路径（同级重名取排序第一；不区分大小写）。失败 → 边框标红并提示。</summary>
    private void ConfirmPath()
    {
        if (TryResolvePath(PathEditText, out var folderId, out var invalidSegment))
        {
            IsPathEditing = false;
            PathCandidates = new List<string>();
            _ = LoadAsync(folderId);
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
        var text = PathEditText ?? string.Empty;
        var idx = LastIndexOfPathSeparator(text);
        var prefix = idx >= 0 ? text.Substring(0, idx + 1) : string.Empty;
        PathEditText = prefix + EscapePathSegment(name) + "/";   // 候选名含 / 时同样转义写入
        SelectedCandidateIndex = 0;
    }

    /// <summary>↑/↓ 移动候选高亮（由视图键盘事件调用）。</summary>
    public void MoveCandidate(int delta)
    {
        if (PathCandidates.Count == 0) return;
        var next = Math.Clamp(SelectedCandidateIndex + delta, 0, PathCandidates.Count - 1);
        SelectedCandidateIndex = next;
    }

    private void UpdatePathCandidates()
    {
        var text = _pathEditText ?? string.Empty;
        var idx = LastIndexOfPathSeparator(text);
        var headText = idx >= 0 ? text.Substring(0, idx + 1) : string.Empty;
        var typed = idx >= 0 ? text.Substring(idx + 1) : text;

        if (!TryResolvePath(headText, out var head, out _))
        {
            PathCandidates = new List<string>();
            return;
        }

        var typedPlain = UnescapePathSegment(typed.Trim());   // 用户输入的可能是转义名（如 "A\/B" 查找 A/B）

        PathCandidates = _folderMap
            .Where(kvp => ParentMatches(kvp.Value.ParentId, head))
            .Where(kvp => typedPlain.Length == 0 || kvp.Value.Name.StartsWith(typedPlain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kvp => kvp.Value.Name, StringComparer.CurrentCulture)
            .Select(kvp => kvp.Value.Name)
            .Take(8)
            .ToList();
        SelectedCandidateIndex = PathCandidates.Count > 0 ? 0 : -1;
    }

    private bool TryResolvePath(string text, out string? folderId, out string? invalidSegment)
    {
        folderId = null;
        invalidSegment = null;
        var segments = SplitPathSegments(text);   // 转义感知切分：'\/' 不是分隔符
        foreach (var seg in segments)
        {
            if (folderId == null && seg.Equals("全部书签", StringComparison.OrdinalIgnoreCase))
                continue;
            var current = folderId; // out 参数不能被 lambda 捕获，先复制
            var match = _folderMap
                .Where(kvp => ParentMatches(kvp.Value.ParentId, current)
                              && string.Equals(kvp.Value.Name, seg, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToList();
            if (match.Count == 0)
            {
                invalidSegment = seg;
                return false;
            }
            folderId = match[0].Key;
        }
        return true;
    }

    private static bool ParentMatches(string? parentId, string? current)
        => parentId == current;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

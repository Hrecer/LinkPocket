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

    /// <summary>补刷递归深度（2.10-44）：最后一次补刷的 finally 里自身再次触发最多 3 层，
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
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
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
    public ICommand ClearSelectionCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand RenameSelectionCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    public ICommand TogglePathEditCommand { get; }
    public ICommand ConfirmPathCommand { get; }
    public ICommand CancelPathEditCommand { get; }
    public ICommand CompletePathCommand { get; }

    /// <summary>文本输入委托：由视图层注入（InputDialog.Show），避免 VM 直接依赖控件。参数 (标题, 默认值)，返回输入或 null 取消。
    /// 实例属性（原静态版本在多窗口共享同一委托，且无头环境下是"全局静默"状态）——由视图在 DataContext 就绪后注入本页实例；
    /// <c>null</c> 时（无头/单测）调用方按取消处理，绝不因 VM 而崩溃。</summary>
    public Func<string, string, string?>? Prompt { get; set; }

    // —— 多选（Windows 资源管理器语义：锚点 + Ctrl/Shift 修饰键）——

    /// <summary>Shift 区间选择的起点行 ID。</summary>
    private string? _anchorId;

    public IEnumerable<BrowserRowViewModel> SelectedRows => Rows.Where(r => r.IsSelected);
    public int SelectionCount => Rows.Count(r => r.IsSelected);
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

    // —— 对话框端口（S7 分层债收口）——
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

    // —— S1 选中通知批量抑制 ——
    // 批量选择（Shift 区间 / 全选 / 清空）会逐行翻转 IsSelected；若不抑制，每行都触发一次
    // NotifySelectionChanged（4+ 属性通知 + Details.UpdateFrom + 命令重查询），10k 行下 = 上万次
    // 全量重评估。抑制期只发行自身 INPC，批末统一一次全量通知。

    /// <summary>抑制计数：>0 时行选中变更不即时通知（批内聚合）。</summary>
    private int _selectionNotifySuppress;

    /// <summary>行选中变更入口（行 VM 回调）：抑制期内静默，批末由 RunSelectionBatch 统一通知一次。</summary>
    internal void OnRowSelectionChanged()
    {
        if (_selectionNotifySuppress > 0) return;
        NotifySelectionChanged();
    }

    /// <summary>批量选中操作的护栏：body 内逐行变更静默，收尾无论成败都统一通知一次。</summary>
    private void RunSelectionBatch(Action body)
    {
        _selectionNotifySuppress++;
        try { body(); }
        finally { _selectionNotifySuppress--; }
        NotifySelectionChanged();
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
        GoUpCommand = new RelayCommand(() => _ = LoadAsync(GetParentId(Controller.CurrentFolderId)), () => !IsAtRoot() && !IsPathEditing);
        RowClickCommand = new RelayCommand<BrowserRowViewModel?>(SelectRow);
        RowOpenCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenRowAsync(row));
        CrumbClickCommand = new RelayCommand<BrowserCrumbViewModel?>(crumb => _ = LoadAsync(crumb?.FolderId));
        CopyUrlCommand = new RelayCommand<BrowserRowViewModel?>(CopyUrl);
        RenameRowCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = RenameRowAsync(row));
        DeleteRowCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = DeleteRowAsync(row));
        NewFolderCommand = new RelayCommand<object?>(param => _ = NewFolderAsync(param as string));
        NewLinkCommand = new RelayCommand(OpenEditorForCreate);
        OpenDetailCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenDetailPageAsync(row));
        DetailPage = new LinkDetailPageViewModel(client, this);
        RenameNodeCommand = new RelayCommand<FolderNode?>(node => _ = RenameNodeAsync(node));
        DeleteNodeCommand = new RelayCommand<FolderNode?>(node => _ = DeleteNodeAsync(node));
        CutCommand = new RelayCommand(CutSelection, () => HasSelection && !IsPathEditing);
        CopyCommand = new RelayCommand(CopySelection, () => HasSelection && !IsPathEditing);
        PasteCommand = new RelayCommand(() => _ = PasteAsync(), () => Clipboard.BrowserPayload is { IsEmpty: false } && !IsPathEditing);
        SelectAllCommand = new RelayCommand(SelectAllRows, () => !IsPathEditing);
        // Esc 在路径编辑态里归属「取消路径编辑」；此时清除选中必须让位，避免两者互抢按键
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !IsPathEditing);
        DeleteSelectionCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => HasSelection && !IsPathEditing);
        RenameSelectionCommand = new RelayCommand(() => _ = RenameSelectedAsync(), () => SelectionCount == 1 && !IsPathEditing);
        OpenSelectionCommand = new RelayCommand(() => _ = OpenSelectedAsync(), () => SelectionCount == 1 && !IsPathEditing);
        TogglePathEditCommand = new RelayCommand(TogglePathEdit);
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
    /// </summary>
    public void ApplySort(string? field, bool ascending)
    {
        if (string.IsNullOrEmpty(field)) return;
        SortBy = field;
        SortOrder = ascending ? "asc" : "desc";
        _ = RefreshPreservingSelectionAsync();
    }

    /// <summary>进入指定目录（null = 根）。首次显示页面时调用 LoadAsync(null)。</summary>
    public async Task LoadAsync(string? folderId)
    {
        Controller.NavigateTo(folderId);
        CurrentFolderId = Controller.CurrentFolderId;
        await RefreshAsync();
    }

    /// <summary>
    /// 重新加载当前目录（供事件推送订阅/操作收尾调用）。
    /// preserveSelectionId：原地刷新场景（如从链接详情页返回）传入原选中项 id，
    /// 刷新后恢复该选中，避免"刷新即清空右侧栏"。
    /// 重入守卫 = 「最后请求必被处理」：加载进行中又来新请求（导航切换 / 移动粘贴后的收尾刷新 /
    /// 防抖事件）只置挂起标志，当前加载收尾后自动补刷一次——绝不静默吞掉请求（曾导致：
    /// 导航后列表停在旧目录、移动粘贴后只剩事件链一条刷新路径）。
    /// </summary>
    public async Task RefreshAsync(string? preserveSelectionId = null)
    {
        if (IsLoading)
        {
            _refreshPending = true;
            return;
        }
        // 2.10-44：数据持续高频变动时，补刷递归不能无限延续（见 finally 内的深度计数）
        if (_refreshRecursionDepth >= MaxRefreshRecursion)
        {
            _refreshPending = false;   // 弃掉挂起：交还 300ms 事件防抖继续追平（不丢数据，只是晚一拍）
            return;
        }
        IsLoading = true;
        try
        {
            // 2.10-45 单快照：folders.overview 一次返回 目录页+全量树+根级计数，
            // 三个数据源在引擎同一读池 UoW 内（不再跨命令漂移；原三连查 FolderContents/Tree/Stats 已收敛为一条）。
            var contents = await _client.FoldersOverviewAsync(Controller.CurrentFolderId, sortBy: SortBy, sortOrder: SortOrder);

            // 文件夹映射：面包屑 + 返回上级需要父链；同时重建左侧文件夹树（与目录页同快照的树/计数）
            var tree = contents.Tree ?? new List<FolderDto>();
            _folderMap = tree.ToDictionary(f => f.FolderId, f => (f.ParentId, f.Name));
            RebuildFolderTree(tree, contents.RootLinkCount ?? 0);
            // 根级直挂链接注入「全部书签」节点下（文件夹之前；点击 = 主区定位选中该行）
            await LoadTreeRootLinkNodesAsync();
            // 树已重建：重发当前目录通知，让视图重新定位树的选中项
            OnPropertyChanged(nameof(CurrentFolderId));

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
            if (SortOrder == "desc")
            {
                foreach (var row in linkRows) Rows.Add(row);
                foreach (var row in folderRows) Rows.Add(row);
            }
            else
            {
                foreach (var row in folderRows) Rows.Add(row);
                foreach (var row in linkRows) Rows.Add(row);
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

            // 目录切换后旧选中与锚点失效；原地刷新（preserveSelectionId）时恢复原选中
            _anchorId = null;
            if (!string.IsNullOrEmpty(preserveSelectionId))
            {
                var keep = Rows.FirstOrDefault(r => r.Id == preserveSelectionId);
                if (keep != null)
                {
                    keep.IsSelected = true;
                    _anchorId = keep.Id;
                }
            }
            NotifySelectionChanged();

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

            // 加载期间有新的刷新请求（导航/操作收尾/防抖事件）→ 立即补刷一次，保证最后请求被处理
            if (_refreshPending)
            {
                _refreshPending = false;
                _refreshRecursionDepth++;
                try { await RefreshAsync(SelectedRows.FirstOrDefault()?.Id); }
                finally { _refreshRecursionDepth--; }
            }
        }
    }

    // —— 交互 ——

    private void SelectRow(BrowserRowViewModel? row)
    {
        if (row == null) return;
        RunSelectionBatch(() =>
        {
            foreach (var r in Rows)
                r.IsSelected = ReferenceEquals(r, row);
        });
        _anchorId = row.Id;
    }

    /// <summary>带修饰键的选择路由（由视图在鼠标抬起时调用，读 Keyboard.Modifiers）。</summary>
    public void SelectRowWithModifiers(BrowserRowViewModel? row, ModifierKeys mods)
    {
        if (row == null) return;

        if (mods.HasFlag(ModifierKeys.Control))
        {
            row.IsSelected = !row.IsSelected;   // 单行翻转：无需批量抑制
            _anchorId ??= row.Id;
        }
        else if (mods.HasFlag(ModifierKeys.Shift))
        {
            var anchor = Rows.FirstOrDefault(r => r.Id == _anchorId) ?? row;
            var i1 = Rows.IndexOf(anchor);
            var i2 = Rows.IndexOf(row);
            if (i1 > i2) (i1, i2) = (i2, i1);
            RunSelectionBatch(() =>
            {
                for (var i = 0; i < Rows.Count; i++)
                    Rows[i].IsSelected = i >= i1 && i <= i2;
            });
        }
        else
        {
            RunSelectionBatch(() =>
            {
                foreach (var r in Rows)
                    r.IsSelected = ReferenceEquals(r, row);
            });
            _anchorId = row.Id;
        }
    }

    public void SelectAllRows()
    {
        RunSelectionBatch(() =>
        {
            foreach (var r in Rows) r.IsSelected = true;
        });
        _anchorId ??= Rows.FirstOrDefault()?.Id;
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
    public async Task<bool> NavigateAndSelectAsync(string? folderId, string rowId)
    {
        if (string.IsNullOrEmpty(rowId)) return false;

        // 已在目标目录时不必重载（避免无谓的列表重建与闪烁）
        if (Controller.CurrentFolderId != folderId)
            await LoadAsync(folderId);

        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row == null) return false;

        SelectRowWithModifiers(row, ModifierKeys.None);
        FocusRowRequested?.Invoke(this, row);
        return true;
    }

    public void ClearSelection()
    {
        RunSelectionBatch(() =>
        {
            foreach (var r in Rows)
                r.IsSelected = false;
        });
        _anchorId = null;
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
    /// rootLinkCount = 同快照的根级直挂链接数（原另查 links.stats，现由 overview 一次交付）。
    /// 纯同步（2.7）：无 IO/等待，签名用 void 不误导调用方。
    /// 1.2：ParentId == FolderId 的自环坏数据排除（绝不把自己挂成自己的子节点）。</summary>
    private void RebuildFolderTree(List<FolderDto> tree, int rootLinkCount)
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
                && node.ParentId != node.FolderId   // 1.2：自环坏数据 → 按根级兜底，避免自引用节点
                && nodes.TryGetValue(node.ParentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                root.Children.Add(node);
            }
        }

        // 根节点计数 = 顶层文件夹递归计数之和 + 根级直挂链接数（内核递归计数）
        root.LinkCount = tree.Where(f => f.ParentId == null)
            .Sum(f => f.LinkCount) + rootLinkCount;

        FolderTree.Add(root);
    }

    /// <summary>
    /// 把根级直挂链接作为叶子节点加入树根「全部书签」的 Children，并与文件夹一起
    /// **统一按名称升序重排**（左侧目录树唯一排序口径 = 名称升序，链接与文件夹混排，绝无第二种顺序）。
    /// 链接叶子的点击语义 = 主区定位选中（BrowserView 处理）；此处只负责数据注入与排序。
    /// 上限 200：根级直挂链接海量时树不至于失控（正常使用远低于此量级）。
    /// </summary>
    private async Task LoadTreeRootLinkNodesAsync()
    {
        var root = FolderTree.FirstOrDefault();
        if (root == null) return;
        try
        {
            var roots = await _client.LinkRootsAsync(sortBy: "title", sortOrder: "asc", perPage: 200);
            foreach (var l in roots)
            {
                root.Children.Add(new FolderNode
                {
                    IsLink = true,
                    Id = l.LinkId,
                    Name = string.IsNullOrWhiteSpace(l.Title) ? (l.Url ?? "") : l.Title,
                    Host = this
                });
            }
            // 统一重排：即使本轮没有链接，也保证文件夹子节点本身按名称升序（口径唯一、确定性可见）
            var ordered = root.Children.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
            root.Children.Clear();
            foreach (var n in ordered) root.Children.Add(n);
        }
        catch (Exception ex)
        {
            // 树注入失败不阻断主列表（根级链接只影响树的展示；失败留痕）
            Services.Logger.Error("加载根级链接注入目录树失败", ex);
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

    /// <summary>批量拖拽 / 移动入口。targetFolderId 为 null 表示根。非法项（目标在自身子树内、已在目标目录）逐项跳过。</summary>
    public async Task MoveItemsAsync(IEnumerable<(string Id, bool IsFolder)> items, string? targetFolderId)
    {
        var target = targetFolderId;
        var moved = 0;
        var renamedNotes = new List<string>();
        IsLoading = true;
        try
        {
            foreach (var (id, isFolder) in items)
            {
                if (isFolder)
                {
                    if (id == target || IsSelfOrDescendant(id, target)) continue;
                    if (NormalizeParentId(_folderMap.TryGetValue(id, out var info) ? info.ParentId : null) == target)
                        continue; // 已在目标目录
                    var unique = await MoveFolderWithConflictRenameAsync(id, target, renamedNotes);
                    if (unique) moved++;
                }
                else
                {
                    if (await MoveLinkAsync(id, target)) moved++;
                }
            }
            StatusText = moved > 0 ? $"已移动 {moved} 项{FormatRenamedNotes(renamedNotes)}" : "没有需要移动的项目";
        }
        catch (Exception ex)
        {
            StatusText = "移动失败";
            ShowError("移动失败", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }

        // 收尾刷新必须放在 IsLoading=false 之后：在调用方自己撑起的 IsLoading 期间调刷新，
        // 会被重入守卫挂起且无人消费（历史上即因此只剩事件链一条刷新路径）——列表在此重建
        await RefreshPreservingSelectionAsync();
    }

    /// <summary>移动文件夹；目标目录存在同名时自动编号重命名（绝不覆盖）。返回是否执行了移动。
    /// 单项失败不中断整批（与 MoveLink/Copy* 一致）；改名失败只记备注，移动结果不受影响。</summary>
    private async Task<bool> MoveFolderWithConflictRenameAsync(string folderId, string? target, List<string> renamedNotes)
    {
        try
        {
            var name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : "文件夹";
            var unique = GenerateUniqueName(name, SiblingFolderNames(target));
            await _client.FolderMoveAsync(folderId, target);
            if (unique != name)
            {
                try
                {
                    await _client.FolderUpdateAsync(folderId, name: unique);
                    renamedNotes.Add($"「{name}」→「{unique}」");
                }
                catch
                {
                    // 改名失败不中断整批：文件夹已移动成功，只有撞名尚未消除（备注如实记录）
                    renamedNotes.Add($"「{name}」(改名未完成)");
                }
            }
            return true;
        }
        catch
        {
            return false;   // 单项移动失败 → 跳过该项继续批内其余项
        }
    }

    private async Task<bool> MoveLinkAsync(string linkId, string? target)
    {
        // 同目录粘贴/拖放 = 无操作
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取后 FirstOrDefault）
            if (link == null) return false; // 源已被删除，跳过
            if (NormalizeParentId(link.ListId) == target) return false;
            await _client.LinkUpdateAsync(linkId, listId: target);
            return true;
        }
        catch
        {
            return false; // 单项失败不中断整批
        }
    }

    /// <summary>把父目录 ID 归一化成可比较的值（null = 根）。</summary>
    private static string? NormalizeParentId(string? parentId) => parentId;

    /// <summary>目标目录下已存在的文件夹名集合。</summary>
    private HashSet<string> SiblingFolderNames(string? targetId)
    {
        var target = NormalizeParentId(targetId);
        return _folderMap
            .Where(kvp => NormalizeParentId(kvp.Value.ParentId) == target)
            .Select(kvp => kvp.Value.Name)
            .ToHashSet(StringComparer.CurrentCulture);
    }

    /// <summary>Windows 风格重名编号："abc" → "abc (2)" → "abc (3)"…；输入名本身已带 "(N)" 时剥掉再编号。</summary>
    private static string GenerateUniqueName(string original, HashSet<string> taken)
    {
        if (!taken.Contains(original)) return original;
        var baseName = System.Text.RegularExpressions.Regex.Replace(original, @"\s*\(\d+\)$", string.Empty);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "未命名";
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        // 编号耗尽（几乎不可达）：时间戳后缀。带毫秒避免同秒内两次调用撞名（与内核 WindowsNamingPolicy 同口径）
        return $"{baseName} ({DateTime.Now:HHmmssff})";
    }

    private static string FormatRenamedNotes(List<string> notes)
        => notes.Count > 0 ? $"（重命名：{string.Join("、", notes)}）" : string.Empty;

    // —— 剪切 / 复制 / 粘贴（Ctrl+X / C / V）——

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
            StatusText = "项目已在当前文件夹中";
            return;
        }

        IsLoading = true;
        var renamedNotes = new List<string>();
        var pasted = 0;
        try
        {
            foreach (var fid in payload.FolderIds)
            {
                if (fid == target || IsSelfOrDescendant(fid, target)) continue;
                if (payload.IsCut)
                {
                    if (NormalizeParentId(_folderMap.TryGetValue(fid, out var info) ? info.ParentId : null) == target) continue;
                    if (await MoveFolderWithConflictRenameAsync(fid, target, renamedNotes)) pasted++;
                }
                else if (await CopyFolderWithConflictRenameAsync(fid, target, renamedNotes)) pasted++;
            }

            foreach (var lid in payload.LinkIds)
            {
                if (payload.IsCut)
                {
                    if (await MoveLinkAsync(lid, target)) pasted++;
                }
                else if (await CopyLinkWithConflictRenameAsync(lid, target, renamedNotes)) pasted++;
            }

            if (payload.IsCut)
            {
                Clipboard.SetBrowserPayload(null); // 剪切语义：粘贴后清空
                foreach (var r in Rows) r.IsCut = false;
            }
            StatusText = pasted > 0 ? $"已粘贴 {pasted} 项{FormatRenamedNotes(renamedNotes)}" : "没有可粘贴的项目";
        }
        catch (Exception ex)
        {
            StatusText = "粘贴失败";
            ShowError("粘贴失败", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }

        // 收尾刷新同样放在 IsLoading=false 之后（与 MoveItemsAsync 同理，见其注释）
        await RefreshPreservingSelectionAsync();
    }

    /// <summary>深拷贝文件夹；同名自动编号。返回是否执行。</summary>
    private async Task<bool> CopyFolderWithConflictRenameAsync(string folderId, string? target, List<string> renamedNotes)
    {
        try
        {
            var name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : "文件夹";
            var unique = GenerateUniqueName(name, SiblingFolderNames(target));
            var copy = await _client.FolderCopyAsync(folderId, target);
            var newId = copy.Data?.NewFolderId;
            if (string.IsNullOrEmpty(newId)) return false;
            if (unique != name)
            {
                await _client.FolderUpdateAsync(newId, name: unique);
                renamedNotes.Add($"「{name}」→「{unique}」");   // 仅真正重命名才记备注（与移动/复制链接一致）
            }
            return true;
        }
        catch
        {
            return false; // 源已被删除等情况：单项跳过
        }
    }

    /// <summary>复制书签（全量字段）；同名自动编号。返回是否执行。</summary>
    private async Task<bool> CopyLinkWithConflictRenameAsync(string linkId, string? target, List<string> renamedNotes)
    {
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取）

            var targetNorm = target;
            // 目标目录的既有标题集合：分页接口 per_page=0 = 全量，替代第二次全库拉取
            var siblingTitles = (await _client.LinkListAsync(listId: targetNorm, perPage: 0))
                .Links
                .Select(l => l.Title)
                .ToHashSet(StringComparer.CurrentCulture);
            var unique = GenerateUniqueName(link.Title ?? string.Empty, siblingTitles);

            // 复制书签 = 全量字段（URL/标题/描述/收藏/图标；内核无标签系统，无其它字段可丢）
            await _client.LinkCreateAsync(link.Url,
                title: unique,
                description: string.IsNullOrEmpty(link.Description) ? null : link.Description,
                listId: targetNorm,
                isImportant: link.IsImportant,
                autoFetchMetadata: false,
                faviconUrl: string.IsNullOrEmpty(link.FaviconUrl) ? null : link.FaviconUrl);
            if (unique != link.Title) renamedNotes.Add($"「{link.Title}」→「{unique}」");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>刷新重建行后，按剪贴板载荷恢复剪切半透明视觉（仅剪切语义）。</summary>
    private bool IsCutInClipboard(string id, bool isFolder)
    {
        var p = Clipboard.BrowserPayload;
        return p is { IsCut: true } && (isFolder ? p.FolderIds.Contains(id) : p.LinkIds.Contains(id));
    }

    /// <summary>
    /// 新建文件夹（Windows 语义）：
    /// 参数为空 → 在当前目录新建；参数为目标文件夹 ID（行右键）→ 在该文件夹内新建。
    /// </summary>
    private async Task NewFolderAsync(string? parentId)
    {
        // 参数为空 → 当前目录；参数为真实文件夹 ID → 在该文件夹内新建
        var target = FolderIds.IsRoot(parentId) ? Controller.CurrentFolderId : parentId;
        var name = Prompt?.Invoke("新建文件夹", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _client.FolderCreateAsync(name, target);
            StatusText = $"已创建文件夹「{name}」";
            await RefreshPreservingSelectionAsync();
        }
        catch (Exception ex)
        {
            ShowError("新建文件夹失败", ex.Message);
        }
    }

    /// <summary>
    /// 就地刷新并保留当前选中。用于两类收尾：
    /// ① 非导航类操作（重命名 / 新建 / 移动 / 粘贴 / 排序 / 从树里删节点）——它们不改变所在目录；
    /// ② 后端数据变更事件驱动的刷新（<c>MainViewModel.OnBackendRefresh</c>，经 UiEventHub 防抖）——写操作自己
    ///    刚恢复的选中会被这条 300ms 防抖后的第二次刷新抹掉，所以它也必须保留选中。
    /// 只有"切换目录"才用裸 <see cref="RefreshAsync(string?)"/>（那种场景本来就该清空选中与锚点）。
    /// 若被保留的条目已不存在（例如刚被删掉），则刷新后自然为空选中。
    /// </summary>
    public Task RefreshPreservingSelectionAsync()
        => RefreshAsync(SelectedRows.FirstOrDefault()?.Id);

    private async Task RenameNodeAsync(FolderNode? node)
    {
        if (node == null || node.FolderId == null) return; // 根节点「全部书签」不是文件夹
        var name = Prompt?.Invoke("重命名文件夹", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;
        try
        {
            await _client.FolderUpdateAsync(node.FolderId, name: name);
            await RefreshPreservingSelectionAsync();
            StatusText = $"已重命名为「{name}」";
        }
        catch (Exception ex)
        {
            ShowError("重命名失败", ex.Message);
        }
    }

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

        // 1.6：文案按实际结果分派 —— 全部成功 / 全部失败 / 部分成功（原 failed>=deleted 会掩盖"部分成功"）
        StatusText = failed == 0
            ? $"已删除 {deleted} 项"
            : deleted == 0
                ? "删除失败（详见日志）"
                : $"已删除 {deleted} 项，{failed} 项失败";
        if (deleted > 0) ClearSelection();
        // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）——
        // 显式 + 事件双重刷新就是"删完刷两次"的根因。失败项留在列表里，下次再删即可。
    }

    private async Task RenameSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await RenameRowAsync(row);
    }

    private async Task OpenSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await OpenRowAsync(row);
    }

    private async Task RenameRowAsync(BrowserRowViewModel? row)
    {
        if (row == null) return;

        // 链接没有"重命名"语义（名称/URL/描述/图标都属于可编辑内容）→ 打开整页编辑器
        if (!row.IsFolder)
        {
            OpenEditorForEdit(row.Id);
            return;
        }

        var name = Prompt?.Invoke("重命名文件夹", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return;
        try
        {
            await _client.FolderUpdateAsync(row.Id, name: name);
            // 原地刷新并保留该行选中：重命名不该把选中态（以及右侧栏）清掉
            await RefreshAsync(row.Id);
            StatusText = $"已重命名为「{name}」";
        }
        catch (Exception ex)
        {
            ShowError("重命名失败", ex.Message);
        }
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
            parts.Add(EscapePathSegment(name));   // 名字里的 / 转义为 \/，编辑往返不丢（2.10-52/E1）
        return string.Join("/", parts);
    }

    // —— 路径编辑转义（2.10-52/E1）——
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
        var segments = SplitPathSegments(text);   // 转义感知切分：'\/' 不是分隔符（2.10-52/E1）
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

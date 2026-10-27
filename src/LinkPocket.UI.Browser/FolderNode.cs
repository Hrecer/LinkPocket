using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Material3.Wpf;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

public class FolderNode : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string? _folderId;
    private string _name = string.Empty;
    private string? _parentName;
    private int _linkCount;
    private string _iconKind = "folder-outline";
    private BulkObservableCollection<FolderNode> _children = new();

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    /// <summary>真实文件夹 ID；虚拟根节点「全部书签」为 <c>null</c>（它不是文件夹）。</summary>
    public string? FolderId
    {
        get => _folderId;
        set { _folderId = value; OnPropertyChanged(); }
    }

    /// <summary>是否为虚拟根节点「全部书签」：无 ID、不可重命名/删除，只作为树的根与移动目标。</summary>
    public bool IsRoot { get; set; }

    /// <summary>
    /// 是否为链接叶子。**浏览页左栏只显示文件夹，不再产生链接叶子**（Windows 导航窗格口径）；
    /// 该属性恒 false，仅为与回收站树共用同一套树面板模板而保留绑定面（模板按它决定叶子样式）。
    /// </summary>
    public bool IsLink { get; set; }

    /// <summary>回收站树模式：被删文件夹单元（灰化图标 + 无右键菜单）。浏览页恒为 false。</summary>
    public bool IsTrashed { get; set; }

    /// <summary>树行高亮（数据驱动，VM 唯一事实来源，容器重建不影响）：
    /// 链接叶子 = 该链接在选中集合中；文件夹行（含虚根「全部书签」）= **当前所在目录**（位置投影）。
    /// 两者语义不同：文件夹行回答"我在哪"，主栏点空白清选中不会让它熄灭。</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示就地改名编辑框（VM 重命名会话状态的投影，与 IsSelected 同处推送）。</summary>
    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否为拖拽悬停落点（数据驱动，VM 落点状态的投影，与 <see cref="IsSelected"/> 同构）：
    /// 拖拽过程中指针下的节点高亮，落点唯一事实来源在 VM（覆盖式更新），节点只被推送。
    /// </summary>
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示节点右键菜单（回收站树节点 = false）。ContextMenu 半离线，走 DataContext 绑定。</summary>
    public bool ShowNodeMenu => Host != null;

    public string? ParentId { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string? ParentName
    {
        get => _parentName;
        set { _parentName = value; OnPropertyChanged(); }
    }

    public int LinkCount
    {
        get => _linkCount;
        set { _linkCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalLinkCount)); OnPropertyChanged(nameof(DeleteMenuHeader)); OnPropertyChanged(nameof(ShowExpander)); }
    }

    /// <summary>
    /// 是否显示展开箭头：只看有没有子文件夹（Windows 导航窗格：没有下级文件夹就不画箭头）。
    /// 文件夹的链接数不决定箭头——链接不入树，点开也没有东西可看。
    /// </summary>
    public bool ShowExpander => Children.Count > 0;

    /// <summary>
    /// 右键菜单「删除」文案：与列表行一致 —— 文件夹内链接数（递归）> 0 才报数，
    /// 空文件夹只显示「删除」（不得出现"删除 (0 项)"，与 BrowserViewModel.DeleteMenuHeader 同口径）。
    /// </summary>
    public string DeleteMenuHeader => LinkCount > 0
        ? Loc.T("menu.deleteWithCount", LinkCount)
        : Loc.T("common.delete");

    /// <summary>环保护深度上限（坏数据成环时终止递归；合法深树极少超此值）。</summary>
    private const int MaxTreeDepth = 256;

    /// <summary>递归计数用「访问集合 + 深度」双保险——环数据立即停止（而非递归到深度上限），
    /// 合法深度只在极限（>256 层，实际不可达）时截断。与 BrowserViewModel.IsSelfOrDescendant 同策略。</summary>
    public int TotalLinkCount => TotalLinkCountCore(new HashSet<string?>(), 0);

    private int TotalLinkCountCore(HashSet<string?> visited, int depth)
    {
        var own = _linkCount;
        if (depth >= MaxTreeDepth) return own;
        if (!string.IsNullOrEmpty(FolderId) && !visited.Add(FolderId)) return own;   // 已在访问链上 = 环 → 停止本支
        foreach (var child in _children)
            own += child.TotalLinkCountCore(visited, depth + 1);
        return own;
    }

    public string IconKind
    {
        get => _iconKind;
        set { _iconKind = value; OnPropertyChanged(); }
    }

    private System.Windows.Media.Imaging.BitmapImage? _favicon;

    /// <summary>
    /// 站点图标。**浏览页左栏只显示文件夹，恒为 null**（文件夹画文件夹图标）；
    /// 保留该属性仅为与回收站树共用同一套模板的绑定面（模板按 <see cref="HasFavicon"/> 切换图标）。
    /// </summary>
    public System.Windows.Media.Imaging.BitmapImage? Favicon
    {
        get => _favicon;
        internal set
        {
            if (ReferenceEquals(_favicon, value)) return;
            _favicon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFavicon));
        }
    }

    /// <summary>是否有站点图标（模板据此在站点图标与通用链环图标之间切换；相等判据，可在 DataTrigger 里表达）。</summary>
    public bool HasFavicon => _favicon != null;

    /// <summary>图标地址（浏览页恒 null；仅为共用模板的绑定面保留，见 <see cref="Favicon"/>）。</summary>
    public string? FaviconUrl { get; internal set; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>所属 VM：树节点右键菜单经此绑定命令（ContextMenu 不在可视树）。</summary>
    public LinkPocket.ViewModels.BrowserViewModel? Host { get; set; }

    /// <summary>子节点集合（只有子文件夹）。类型仍是批量集合：与回收站树共用同一套模板/集合契约。</summary>
    public BulkObservableCollection<FolderNode> Children
    {
        get => _children;
        set
        {
            if (ReferenceEquals(_children, value)) return;
            _children = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalLinkCount));   // 整体换集合 ⇒ 递归计数可能变化，必须通知
            OnPropertyChanged(nameof(ShowExpander));     // 子文件夹有无同样决定箭头显隐
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

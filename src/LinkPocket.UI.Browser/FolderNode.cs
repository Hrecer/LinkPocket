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
    private ObservableCollection<FolderNode> _children = new();

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
    /// 是否为树中的根级链接叶子节点（「全部书签」节点下、文件夹之前的直挂链接）。
    /// 链接叶子：Id = 链接 ID、FolderId 恒 null、LinkCount 恒 0（右侧不显示计数）、
    /// 无子节点（chevron 自动隐藏）、无右键菜单、不可作为拖放目标（只能被定位/选中）。
    /// </summary>
    public bool IsLink { get; set; }

    /// <summary>回收站树模式：被删文件夹单元（灰化图标 + 无右键菜单）。浏览页恒为 false。</summary>
    public bool IsTrashed { get; set; }

    /// <summary>树选中态（数据驱动，VM 唯一事实来源）：与主栏 BrowserRowViewModel.IsSelected 同构，容器重建不影响高亮。</summary>
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
        set { _linkCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalLinkCount)); OnPropertyChanged(nameof(DeleteMenuHeader)); }
    }

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

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    /// <summary>所属 VM：树节点右键菜单经此绑定命令（ContextMenu 不在可视树）。</summary>
    public LinkPocket.ViewModels.BrowserViewModel? Host { get; set; }

    public ObservableCollection<FolderNode> Children
    {
        get => _children;
        set
        {
            if (ReferenceEquals(_children, value)) return;
            _children = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalLinkCount));   // 整体换集合 ⇒ 递归计数可能变化，必须通知
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

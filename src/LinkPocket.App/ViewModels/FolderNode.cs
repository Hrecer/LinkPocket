using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Material3.Wpf;

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

    /// <summary>回收站树模式：被删文件夹单元（灰化图标 + 无右键菜单）。浏览页恒为 false。</summary>
    public bool IsTrashed { get; set; }

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
    /// 右键菜单「删除」文案：与列表行一致 —— 删除文件夹时其内链接全部进回收站，
    /// 所以显示该文件夹内的链接数（递归），而不是无信息的「删除」。
    /// </summary>
    public string DeleteMenuHeader => $"删除 ({LinkCount} 项)";

    public int TotalLinkCount => _linkCount + _children.Sum(c => c.TotalLinkCount);

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
        set { _children = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

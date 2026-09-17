using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站树节点：被删文件夹单元（trash_folders 行的镜像）。
/// 绑定面与 FolderNode 一致（FolderTreePanel 模板复用），但：
/// IsTrashed=true（灰化图标）、ShowNodeMenu=false（无右键菜单）、Host=null（不可导航/不可打开）。
/// </summary>
public class TrashFolderNode : INotifyPropertyChanged
{
    /// <summary>trash_folders.trash_folder_id。</summary>
    public string TrashFolderId { get; set; } = string.Empty;

    /// <summary>回收站内的父单元 ID；NULL = 回收站根（删除操作的直接对象）。</summary>
    public string? ParentTrashFolderId { get; set; }

    /// <summary>删除时的原文件夹 ID（下一步「还原到原位置」的数据依据）。</summary>
    public string? OriginFolderId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>单元子树内的书签总数（含子孙单元）。</summary>
    public int LinkCount { get; set; }

    public bool IsRoot => false;
    public bool IsTrashed => true;
    public bool ShowNodeMenu => false;
    public object? Host => null;
    public string IconKind => "folder-outline";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public ObservableCollection<TrashFolderNode> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

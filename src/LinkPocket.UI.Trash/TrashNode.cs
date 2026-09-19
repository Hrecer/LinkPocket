using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站树节点（与浏览页 <see cref="FolderNode"/> 同构的绑定面 → 共用 FolderTreePanel）。
/// 三种形态：
/// <list type="bullet">
/// <item>虚根「回收站」（<see cref="IsRoot"/>）：不是实体、无 ID、永不进入选中、无右键菜单（与浏览页虚根同口径）；</item>
/// <item>被删单元（id = trash_folder_id）：可展开（子单元 + 直接链接叶子）、计数药丸 = 子树链接总数；</item>
/// <item>链接叶子（<see cref="IsLink"/>）：单元内/根级的书签快照——无 chevron、无计数药丸、无右键菜单（与浏览页链接叶子同口径）。</item>
/// </list>
/// 选中 / 落点都是 <see cref="TrashViewModel"/> 的**只读投影**（与浏览页同构）：节点对象随重建销毁，绝不持久状态。
/// </summary>
public class TrashNode : INotifyPropertyChanged
{
    /// <summary>单元 = trash_folder_id；链接叶子 = 原链接 ID；虚根 = 空串（无实体身份）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>回收站内的父单元（null = 回收站根）。</summary>
    public string? ParentId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsRoot { get; init; }
    public bool IsLink { get; init; }

    /// <summary>单元子树内书签总数（链接叶子恒 0）。</summary>
    public int LinkCount { get; init; }

    /// <summary>删除时的原位置路径快照（详情/提示用）。</summary>
    public string? OriginPath { get; init; }

    public DateTime? DeletedAt { get; init; }

    /// <summary>被删条目的图标灰化（与回收站语义一致）。</summary>
    public bool IsTrashed => true;

    /// <summary>虚根无右键菜单；单元有（打开 / 永久删除）；链接叶子由面板的 IsLink 触发器置空。</summary>
    public bool ShowNodeMenu => !IsRoot;

    /// <summary>节点右键菜单命令的绑定宿主（ContextMenu 不在可视树，无法 RelativeSource 向上找）。</summary>
    public TrashViewModel? Host { get; set; }

    public ObservableCollection<TrashNode> Children { get; } = new();

    /// <summary>展开态：树面板重建后由宿主按记忆的展开集合恢复。</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    /// <summary>选中 = 宿主选中集合的纯投影（与浏览页行/节点同构）。</summary>
    public bool IsSelected => Host != null && Host.IsSelectedId(Id);

    public void InvalidateIsSelected() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>拖拽悬停落点 = 宿主落点状态的纯投影（覆盖式更新，绝不留残留）。</summary>
    public bool IsDropTarget => Host != null && Host.IsDropTargetNode(Id);

    public void InvalidateIsDropTarget() => OnPropertyChanged(nameof(IsDropTarget));

    /// <summary>回收站树**没有改名**（只读）：为面板模板的改名编辑框绑定面提供恒 false（编辑器永不出现）。</summary>
    public bool IsRenaming => false;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

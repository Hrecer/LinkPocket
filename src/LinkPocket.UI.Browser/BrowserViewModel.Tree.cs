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

/// <summary>
/// BrowserViewModel · 分区：目录树重建（folders.overview 快照 → FolderTree）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>由 folders.overview 的树快照重建左侧树（ParentId == null 即根级）。保留既有展开状态。
    /// rootLinkCount = 同快照的根级直挂链接数（只参与根节点计数药丸）。
    /// 纯同步：无 IO/等待，签名用 void 不误导调用方。
    /// ParentId == FolderId 的自环坏数据排除（绝不把自己挂成自己的子节点）。
    /// <para>
    /// <b>左栏只显示文件夹</b>（Windows 资源管理器的导航窗格口径）：链接不入树——没必要在侧栏再列一遍
    /// 主栏里已有的书签，也免去"展开节点就按目录拉一次链接"的懒加载与叶子搬运成本。
    /// 树里只有两类行：虚根「全部书签」与文件夹。
    /// </para></summary>
    private void RebuildFolderTree(List<FolderDto> tree, int rootLinkCount)
    {
        var expandedIds = new HashSet<string?>();
        CollectExpandedIds(FolderTree, expandedIds);

        FolderTree.Clear();

        var root = new FolderNode { IsRoot = true, Name = BookmarkPath.RootToken, IconKind = "folder-outline", Host = this };
        var nodes = tree.ToDictionary(
            f => f.FolderId,
            f => new FolderNode
            {
                FolderId = f.FolderId,
                ParentId = f.ParentId,
                Name = f.Name,
                LinkCount = f.LinkCount,
                Host = this,
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

        // 根节点计数 = 顶层文件夹递归计数之和 + 根级直挂链接数（内核递归计数）
        root.LinkCount = tree.Where(f => f.ParentId == null)
            .Sum(f => f.LinkCount) + rootLinkCount;

        FolderTree.Add(root);

        // 最后一步恢复展开状态（虚根恒展开）
        root.IsExpanded = true;
        foreach (var (id, node) in nodes)
            if (expandedIds.Contains(id)) node.IsExpanded = true;
    }

    private static void CollectExpandedIds(IEnumerable<FolderNode> nodes, HashSet<string?> ids)
    {
        // 显式栈（深树不爆栈）
        var stack = new Stack<FolderNode>(nodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsExpanded && node.FolderId != null) ids.Add(node.FolderId);
            foreach (var child in node.Children) stack.Push(child);
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

}

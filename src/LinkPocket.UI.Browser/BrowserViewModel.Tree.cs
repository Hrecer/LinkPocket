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
    /// rootLinkCount = 同快照的根级直挂链接数；treeLinks = 同快照的全库活动链接（每文件夹直接链接叶子注入源）。
    /// 纯同步：无 IO/等待，签名用 void 不误导调用方。
    /// ParentId == FolderId 的自环坏数据排除（绝不把自己挂成自己的子节点）。</summary>
    private void RebuildFolderTree(List<FolderDto> tree, int rootLinkCount, List<LinkDto> treeLinks)
    {
        var expandedIds = new HashSet<string?>();
        CollectExpandedIds(FolderTree, expandedIds);

        FolderTree.Clear();

        var root = new FolderNode { IsRoot = true, Name = BookmarkPath.RootToken, IconKind = "folder-open-outline", IsExpanded = true, Host = this };
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

}
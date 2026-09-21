using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// TrashViewModel · 分区：回收站树重建（单元 + 链接叶子）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class TrashViewModel
{
    /// <summary>
    /// 重建树：虚根「回收站」（**恒展开**）+ 单元层级（环保护：坏数据兜底挂根）+ 各节点直接链接叶子。
    /// 排序与浏览页同口径：**单元组在前、链接组在后，各自名称升序**；展开态按用户记忆恢复。
    /// </summary>
    private void RebuildTree()
    {
        var expanded = CollectExpandedIds(Tree);
        Tree.Clear();

        var rootLinks = _linksByUnit.TryGetValue(string.Empty, out var rootLinkList) ? rootLinkList.Count : 0;
        var root = new TrashNode
        {
            IsRoot = true,
            Name = BookmarkPath.TrashToken,
            Host = this,
            IsExpanded = true,   // 虚根恒展开（与浏览页同口径）
            LinkCount = rootLinks,
        };
        var nodeById = new Dictionary<string, TrashNode>(StringComparer.Ordinal);
        foreach (var f in _unitList)
        {
            nodeById[f.TrashFolderId] = new TrashNode
            {
                Id = f.TrashFolderId,
                ParentId = f.ParentTrashFolderId,
                Name = f.Name,
                LinkCount = f.LinkCount,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
                IsExpanded = expanded.Contains(f.TrashFolderId),
                Host = this,
            };
        }

        foreach (var f in _unitList)
        {
            var node = nodeById[f.TrashFolderId];
            if (f.ParentTrashFolderId != null
                && nodeById.TryGetValue(f.ParentTrashFolderId, out var parent)
                && !IsSelfInLineage(f.ParentTrashFolderId, f.TrashFolderId))
            {
                parent.Children.Add(node);
            }
            else
            {
                root.Children.Add(node);   // 根级 / 父缺失 / 成环坏数据：兜底挂根（不死循环、不凭空消失）
            }
        }

        // 链接叶子：挂到归属单元（null → 根）；归属单元缺失（坏数据）同样兜底挂根
        foreach (var (key, links) in _linksByUnit)
        {
            var target = key.Length == 0 ? root
                : nodeById.TryGetValue(key, out var unitNode) ? unitNode : root;
            foreach (var l in links) target.Children.Add(ToLinkNode(l));
        }

        SortChildren(root);
        Tree.Add(root);
    }

    /// <summary>排序投影（单元在前、链接在后，各自名称升序）——递归应用。</summary>
    private static void SortChildren(TrashNode node)
    {
        var sorted = node.Children
            .OrderBy(n => n.IsLink ? 1 : 0)
            .ThenBy(n => n.Name, StringComparer.CurrentCulture)
            .ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }

    private TrashNode ToLinkNode(TrashEntryDto link) => new()
    {
        Id = link.Id,
        ParentId = link.TrashFolderId,
        Name = string.IsNullOrEmpty(link.Name) ? (link.Url ?? string.Empty) : link.Name,
        IsLink = true,
        Host = this,
    };

    /// <summary>沿 parent 链向上查找：self 是否出现在祖先链中（是 = 成环坏数据）。guard 防不死循环。</summary>
    private bool IsSelfInLineage(string? start, string self)
    {
        var cur = start;
        for (var guard = 0; cur != null && guard < 64; guard++)
        {
            if (cur == self) return true;
            if (!_unitById.TryGetValue(cur, out var p)) return false;
            cur = p.ParentTrashFolderId;
        }
        return false;
    }

    private HashSet<string> CollectExpandedIds(IEnumerable<TrashNode> nodes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Id.Length > 0) ids.Add(node.Id);
            ids.UnionWith(CollectExpandedIds(node.Children));
        }
        return ids;
    }

    /// <summary>树的所有节点（含虚根与链接叶子；深度优先）。</summary>
    internal IEnumerable<TrashNode> AllTreeNodes()
    {
        foreach (var node in Tree) foreach (var n in Walk(node)) yield return n;
        static IEnumerable<TrashNode> Walk(TrashNode node)
        {
            yield return node;
            foreach (var child in node.Children) foreach (var n in Walk(child)) yield return n;
        }
    }

}
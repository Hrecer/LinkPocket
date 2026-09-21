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
/// BrowserViewModel · 分区：面包屑/键盘导航/展开定位/右键菜单事件（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
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
    /// 与选中（<see cref="Selection"/>）、位置（CurrentFolderId）正交；树重建后引用自然失效 →
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
        _treeNavNode = target;   // 下一次移动从当前落点继续（游标独立于选中/位置）

        // 落点语义 = 与鼠标点击树行**完全同一条路径**（文件夹 = 选中 + 进入；链接叶子 = 定位；虚根 = 进根不选中）——
        // 键盘绝不另造一套"只移选中"的语义（那会让"点树"与"按树"行为分叉）。
        _ = SelectTreeNodeAsync(target);
    }

    /// <summary>
    /// 树节点的**可见**深度优先序列（= 屏幕上实际看到的行序）：
    /// 未展开的节点其子级不在视觉序列里（没展开就看不到子文件夹，不进入子级）；
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
            if (node.IsLink && Selection.Contains(node.Id)) return node;
            if (node.FolderId != null && Selection.Contains(node.FolderId)) return node;
        }

        // ③ 当前所在目录（根目录 → 虚根行）
        var currentId = Controller.CurrentFolderId;
        foreach (var node in all)
            if (currentId == null ? node.IsRoot : node.FolderId == currentId) return node;
        return null;
    }

    /// <summary>视图请求：为当前选中行弹右键菜单（Shift+F10 / 菜单键；视图订阅后聚焦并 open 行 ContextMenu）。</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>左栏 ←/→：折叠 / 展开当前树节点（链接叶子 / 虚根无操作）。</summary>
    private void ToggleFocusedTreeExpand()
    {
        var node = CurrentFocusedTreeNode();
        if (node == null || node.IsLink || node.IsRoot) return;
        node.IsExpanded = !node.IsExpanded;
    }

}
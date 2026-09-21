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
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.ViewModels;

/// <summary>
/// TrashViewModel · 分区：导航（单元进入/面包屑/打开与定位/键盘移动）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class TrashViewModel
{
    /// <summary>
    /// 导航到某单元（null = 回收站根）：本地过滤 + 历史栈；**点是当前位置 = 无条件重载一次**
    /// （Windows 直觉，与浏览页同口径）。
    /// </summary>
    public Task NavigateAsync(string? unitId)
    {
        if (unitId != null && !_unitById.ContainsKey(unitId))
        {
            StatusText = Loc.K("trash.unitGone");
            return Task.CompletedTask;
        }

        if (unitId == CurrentUnitId)
            return LoadAsync(navigating: true);   // 点是当前位置：重载刷新一次

        Controller.NavigateTo(unitId);
        RebuildRows();
        RebuildBreadcrumbs();
        SetStatusText();
        CommandRefresh.Request();
        RefreshCompleted?.Invoke(this, true);
        return Task.CompletedTask;
    }

    /// <summary>取某单元的父单元（根 = null）。</summary>
    public string? GetParentId(string? unitId)
        => unitId != null && _unitById.TryGetValue(unitId, out var f) ? f.ParentTrashFolderId : null;

    /// <summary>当前位置链（根之后逐级，用于地址栏文本）。</summary>
    private List<(string Id, string Name)> BuildPathChain()
    {
        var path = new List<(string, string)>();
        var cur = CurrentUnitId;
        for (var guard = 0; cur != null && guard < 64; guard++)
        {
            if (!_unitById.TryGetValue(cur, out var f)) break;
            path.Insert(0, (f.TrashFolderId, f.Name));
            cur = f.ParentTrashFolderId;
        }
        return path;
    }

    private void RebuildBreadcrumbs()
    {
        var chain = new List<TrashCrumbViewModel> { new(null, BookmarkPath.TrashToken) };
        foreach (var (id, name) in BuildPathChain()) chain.Add(new TrashCrumbViewModel(id, name));

        Breadcrumbs.Clear();
        for (var i = 0; i < chain.Count; i++)
            Breadcrumbs.Add(new TrashCrumbViewModel(chain[i].UnitId, chain[i].Name) { IsLast = i == chain.Count - 1 });

        OnPropertyChanged(nameof(IsInUnit));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CurrentUnitDisplayName));
    }

    // ================= 打开（双击 / Enter / 树节点单击） =================

    private async Task OpenRowAsync(TrashRowViewModel? row)
    {
        if (row == null) return;
        if (row.IsFolder)
        {
            SetSelection(new[] { row.Id });
            await NavigateAsync(row.Id);
            return;
        }
        ShowLinkDetailRequested?.Invoke(this, row);
    }

    /// <summary>打开只读详情覆盖层（共享详情页）：选中该行（与浏览页打开详情同口径）+ 填共享面 + 置开页标志。</summary>
    public void OpenLinkDetail(TrashRowViewModel row)
    {
        SetSelection(new[] { row.Id });
        _detailLinkId = row.Id;
        DetailPane.Show(row);
        IsDetailOverlayOpen = true;
    }

    /// <summary>详情覆盖层当前展示的条目 ID（刷新收尾据此判断"条目消失即关"）。</summary>
    private string? _detailLinkId;

    private void CloseDetailOverlay()
    {
        _detailLinkId = null;
        IsDetailOverlayOpen = false;
    }

    private void CopyDetailUrl()
    {
        try
        {
            if (!string.IsNullOrEmpty(DetailPane.Url)) System.Windows.Clipboard.SetText(DetailPane.Url);
        }
        catch { /* 剪贴板被占用时不阻断 */ }
    }

    private void CopyDetailId()
    {
        try
        {
            if (!string.IsNullOrEmpty(_detailLinkId)) System.Windows.Clipboard.SetText(_detailLinkId);
        }
        catch { /* 剪贴板被占用时不阻断 */ }
    }

    /// <summary>详情覆盖层的「打开网站」：即使是已废弃（在回收站里）的条目也应能打开；
    /// **不记访问**（回收站条目不在主表，没有访问统计口径）。</summary>
    private void OpenDetailWebsite()
    {
        var url = DetailPane.Url;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to open the site (returned quietly, page stays)", ex);   // 观测面：失败留痕
        }
    }

    /// <summary>树节点被点击（视图转发；与浏览页 SelectTreeNodeAsync 同口径）：单元 = 选中 + 进入；链接叶子 = 主栏定位选中；虚根 = 回根。</summary>
    public Task SelectTreeNodeAsync(TrashNode node) => OpenNodeAsync(node);

    /// <summary>树节点动作（与浏览页同一路径语义）：单元 = 选中 + 进入；链接叶子 = 主栏定位选中；虚根 = 回根。</summary>
    private async Task OpenNodeAsync(TrashNode? node)
    {
        if (node == null) return;
        if (node.IsRoot)
        {
            await NavigateAsync(null);   // 虚根：回根（不写选中——根不是实体）
            return;
        }
        if (node.IsLink)
        {
            await LocateLinkAsync(node);
            return;
        }
        SetSelection(new[] { node.Id });
        await NavigateAsync(node.Id);
    }

    /// <summary>定位链接：进其所属单元 + 主栏选中该行（与浏览页"跳转"语义同口径；已在则不重载）。</summary>
    private Task LocateLinkAsync(TrashNode link)
    {
        var parent = link.ParentId;   // null = 根
        if (parent != CurrentUnitId)
        {
            Controller.NavigateTo(parent);
            RebuildRows();
            RebuildBreadcrumbs();
            SetStatusText();
        }
        SetSelection(new[] { link.Id });
        var row = Rows.FirstOrDefault(r => r.Id == link.Id);
        if (row != null) FocusRowRequested?.Invoke(this, row);
        return Task.CompletedTask;
    }

    private async Task OpenSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await OpenRowAsync(row);
    }

    // ================= 键盘（主栏 / 左栏） =================

    private static int ParseDirection(object? parameter)
        => parameter as string == "up" ? -1 : 1;

    /// <summary>主栏 ↑/↓：移动选中（单选）+ 滚入视口；到边界**停住**；无选中时从顶/底开始。</summary>
    private void MoveMainSelection(int delta)
    {
        if (Rows.Count == 0 || delta == 0) return;
        var target = Selection.Move(delta, Rows.Select(r => r.Id).ToList());
        if (target == null) return;
        var row = Rows.FirstOrDefault(r => r.Id == target);
        if (row != null) FocusRowRequested?.Invoke(this, row);
    }

    private void SelectLastRow()
    {
        if (Rows.Count == 0) return;
        var target = Selection.SelectLast(Rows.Select(r => r.Id).ToList());
        if (target == null) return;
        var row = Rows.FirstOrDefault(r => r.Id == target);
        if (row != null) FocusRowRequested?.Invoke(this, row);
    }

    /// <summary>左栏 ↑/↓：按**可见视觉顺序**移动；落点语义与鼠标点树完全同一条路径（与浏览页同口径）。</summary>
    private void MoveTreeSelection(int delta)
    {
        if (delta == 0) return;
        var flat = VisibleTreeNodes().ToList();
        if (flat.Count == 0) return;

        var current = CurrentFocusedTreeNode() is { } focused ? flat.IndexOf(focused) : -1;
        var next = current < 0
            ? (delta > 0 ? 0 : flat.Count - 1)
            : Math.Clamp(current + delta, 0, flat.Count - 1);
        var target = flat[next];
        _treeNavNode = target;   // 下一次移动从本次落点继续（游标独立于选中/位置）
        _ = OpenNodeAsync(target);
    }

    private void ToggleFocusedTreeExpand()
    {
        var node = CurrentFocusedTreeNode();
        if (node == null || node.IsLink || node.IsRoot) return;
        node.IsExpanded = !node.IsExpanded;
    }

    /// <summary>键盘游标（节点对象引用；虚根无 ID、只有引用能表示它——与浏览页同法；树重建后旧引用自然失效）。</summary>
    private TrashNode? _treeNavNode;

    /// <summary>当前聚焦的树节点（与浏览页同优先级：键盘游标 → 选中实体 → 当前所在单元/虚根）。</summary>
    private TrashNode? CurrentFocusedTreeNode()
    {
        var all = AllTreeNodes().ToList();
        if (_treeNavNode != null && all.Contains(_treeNavNode)) return _treeNavNode;

        foreach (var node in all)
        {
            if (node.IsRoot) continue;
            if (Selection.Contains(node.Id)) return node;
        }

        var currentId = CurrentUnitId;
        foreach (var node in all)
            if (currentId == null ? node.IsRoot : node.Id == currentId) return node;
        return null;
    }

    /// <summary>树节点的**可见**深度优先序列（未展开节点的子级不在序列内——没看到就不进入）。</summary>
    internal IEnumerable<TrashNode> VisibleTreeNodes()
    {
        foreach (var root in Tree)
            foreach (var node in Walk(root))
                yield return node;

        static IEnumerable<TrashNode> Walk(TrashNode node)
        {
            yield return node;
            if (!node.IsExpanded) yield break;
            foreach (var child in node.Children)
                foreach (var n in Walk(child))
                    yield return n;
        }
    }

    // ================= 地址栏（面包屑内联路径编辑；与浏览页同口径，解析器共享） =================

}
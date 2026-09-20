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
/// TrashViewModel · 分区：选中与投影（共享 ListSelection 核心）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class TrashViewModel
{
    // ================= 选中（唯一事实来源 + 投影） =================
    // 核心 = 全站共享的 ListSelection（与浏览页/搜索/智能列表/去重明细同一实现）

    /// <summary>选中集合（**共享 ListSelection 核心**：唯一选中集合 + 锚点 + 修饰键语义）。</summary>
    public ListSelection Selection { get; } = new();

    /// <summary>本行/节点是否在选中集合（行与树都是它的只读投影）。</summary>
    public bool IsSelectedId(string id) => Selection.Contains(id);

    public int SelectionCount => Selection.Count;
    public bool HasSelection => Selection.HasAny;

    /// <summary>选中提示文案（状态栏药丸）：单选显示名称、多选显示项数。</summary>
    public string SelectionInfoText => SelectionCount == 1
        ? (Rows.FirstOrDefault(r => Selection.Contains(r.Id))?.Name ?? "已选中 1 项")
        : $"已选中 {SelectionCount} 项";

    /// <summary>当前选中行（主栏视角；顺序 = 行序）。</summary>
    public IEnumerable<TrashRowViewModel> SelectedRows => Rows.Where(r => Selection.Contains(r.Id));

    /// <summary>选中投影到行 + 树 + 右栏详情栏（唯一写入入口之后的唯一投影点）。</summary>
    private void ApplySelectionToView()
    {
        foreach (var r in Rows) r.InvalidateIsSelected();
        foreach (var node in AllTreeNodes()) node.InvalidateIsSelected();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionInfoText));
        ProjectDetails();
        CommandRefresh.Request();
    }

    /// <summary>选中 → 右栏详情栏投影：空占位 / 单选详情 / 多选计数（同一投影点，视图不另设刷新入口）。</summary>
    private void ProjectDetails()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0) Details.Clear();
        else if (selected.Count == 1) Details.Show(selected[0]);
        else Details.ShowMulti(selected);
    }

    /// <summary>写入选中集合的**唯一入口**（覆盖式，经共享 <see cref="ListSelection"/>）：空集合 = 清空。</summary>
    public void SetSelection(IEnumerable<string> ids) => Selection.Set(ids);

    public void ClearSelection() => Selection.Clear();

    /// <summary>Esc（分层，用户令 2026-09-19）：只读详情覆盖层打开 → 先退出覆盖层；否则清空选中。</summary>
    private void Escape()
    {
        if (IsDetailOverlayOpen)
        {
            CloseDetailOverlay();
            return;
        }
        ClearSelection();
    }

    /// <summary>行点击选择（共享 <see cref="ListSelection.Click"/>：Ctrl 翻转 / Shift 以锚点画区间 /
    /// 无修饰 = 单选——与浏览页同一实现、同一语义）。</summary>
    public void SelectRowWithModifiers(TrashRowViewModel row, ModifierKeys modifiers)
    {
        Selection.Click(row.Id, modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Shift),
            Rows.Select(r => r.Id).ToList());
    }

    public void SelectAllRows() => Selection.SelectAll(Rows.Select(r => r.Id).ToList());

    /// <summary>刷新后清理已不存在的选中 ID（被永久删除的条目不得残留选中态）。</summary>
    private void RemoveMissingFromSelection(IReadOnlyList<string> previous)
    {
        if (previous.Count == 0) return;
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in _unitList) alive.Add(f.TrashFolderId);
        foreach (var l in _allLinks) alive.Add(l.Id);
        SetSelection(previous.Where(alive.Contains));
    }

}
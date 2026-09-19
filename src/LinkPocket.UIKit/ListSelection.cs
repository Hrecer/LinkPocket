using System;
using System.Collections.Generic;

namespace LinkPocket.ViewModels;

/// <summary>
/// **列表选中的共享核心（全站唯一实现）**：唯一选中集合 + 锚点 + 修饰键语义
/// （Ctrl 翻转 / Shift 区间 / 无修饰 = 单选）+ 移动 / 末项 / 全选 / 清空。
///
/// 使用方（浏览页 / 回收站 / 搜索页 / 智能列表结果页 / 去重明细）只做两件事：
/// ① 提供"当前视觉顺序"（<c>order</c>，取自各自表格控件的当前排序）；
/// ② 订阅 <see cref="Changed"/>，把集合投影到行/树/右栏/命令（唯一投影点）。
///
/// 纪律（CONVENTIONS §4-8）：集合是唯一事实来源，任何写入只经本类；
/// 页面绝不旁路在行对象或节点上写选中，也不引入第二个"恢复选中"的路径。
/// </summary>
public sealed class ListSelection
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private string? _anchorId;

    /// <summary>选中 / 锚点发生任何变化后触发（页面在此完成全部投影）。</summary>
    public event Action? Changed;

    /// <summary>当前选中集合（只读使用；改动一律走本类方法）。</summary>
    public IEnumerable<string> Ids => _ids;

    public int Count => _ids.Count;
    public bool HasAny => _ids.Count > 0;

    /// <summary>Shift 区间选择的锚点（最近一次"落点"行 ID）。</summary>
    public string? AnchorId => _anchorId;

    public bool Contains(string? id) => id != null && _ids.Contains(id);

    /// <summary>覆盖式写入（目标集合 = <paramref name="ids"/>）；<paramref name="anchor"/> 为空 = 保持现值。</summary>
    public void Set(IEnumerable<string> ids, string? anchor = null)
    {
        _ids.Clear();
        foreach (var id in ids) _ids.Add(id);
        if (!string.IsNullOrEmpty(anchor)) _anchorId = anchor;
        Changed?.Invoke();
    }

    /// <summary>在当前集合上原地增删（Ctrl 翻转等）——起点恒为当前集合，绝不从空集开始。</summary>
    public void Mutate(Action<HashSet<string>> mutate, string? anchor = null)
    {
        var next = new HashSet<string>(_ids, StringComparer.Ordinal);
        mutate(next);
        _ids.Clear();
        foreach (var id in next) _ids.Add(id);
        if (!string.IsNullOrEmpty(anchor)) _anchorId = anchor;
        Changed?.Invoke();
    }

    /// <summary>单选（落点 = 锚点）。</summary>
    public void SelectSingle(string id) => Set(new[] { id }, id);

    /// <summary>
    /// 行点击的修饰键路由：Ctrl = 翻转（锚点保持）；Shift = 以锚点画区间；
    /// 无修饰 = 单选。order = 当前视觉顺序（由共享表格的当前排序给出）。
    /// </summary>
    public void Click(string id, bool ctrl, bool shift, IReadOnlyList<string> order)
    {
        if (ctrl)
        {
            Mutate(s => { if (!s.Remove(id)) s.Add(id); }, anchor: _anchorId ?? id);
            return;
        }
        if (shift && _anchorId != null)
        {
            var from = IndexOf(order, _anchorId);
            var to = IndexOf(order, id);
            if (from >= 0 && to >= 0)
            {
                var lo = Math.Min(from, to);
                var hi = Math.Max(from, to);
                var range = new List<string>(hi - lo + 1);
                for (var i = lo; i <= hi; i++) range.Add(order[i]);
                Set(range, _anchorId);
                return;
            }
        }
        SelectSingle(id);
    }

    public void Clear() => Set(Array.Empty<string>());

    /// <summary>把指定 ID 纳入选中集合（不清空其他）——"从详情页返回还原选中"语义。</summary>
    public void Restore(string id)
    {
        if (string.IsNullOrEmpty(id) || !_ids.Add(id)) return;
        _anchorId ??= id;
        Changed?.Invoke();
    }

    /// <summary>
    /// ↑/↓：按视觉顺序把选中收敛为"当前选中末位 ± delta"（单选），返回落点 ID（供调用方滚入视口）；
    /// 无选中时从首/末开始；到边界停住返回原落点。
    /// </summary>
    public string? Move(int delta, IReadOnlyList<string> order)
    {
        if (order.Count == 0 || delta == 0) return null;
        var current = -1;
        for (var i = 0; i < order.Count; i++)
            if (_ids.Contains(order[i])) current = Math.Max(current, i);
        var next = current < 0
            ? (delta > 0 ? 0 : order.Count - 1)
            : Math.Clamp(current + delta, 0, order.Count - 1);
        var target = order[next];
        SelectSingle(target);
        return target;
    }

    /// <summary>End：选中末项（返回该 ID 供滚入视口）。</summary>
    public string? SelectLast(IReadOnlyList<string> order)
    {
        if (order.Count == 0) return null;
        var target = order[^1];
        SelectSingle(target);
        return target;
    }

    /// <summary>Ctrl+A：全选（锚点保持；无锚点时落到首项）。</summary>
    public void SelectAll(IReadOnlyList<string> order)
        => Set(order, _anchorId ?? (order.Count > 0 ? order[0] : null));

    /// <summary>剔除已不存在的 ID（刷新后清理）；返回是否有变化。</summary>
    public bool RemoveMissing(Func<string, bool> exists)
    {
        var removed = false;
        foreach (var id in new List<string>(_ids))
            if (!exists(id)) { _ids.Remove(id); removed = true; }
        if (removed) Changed?.Invoke();
        return removed;
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }
}

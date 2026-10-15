using System;
using System.Collections.Generic;
using LinkPocket.Contracts;

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：主栏行集合的增量同步（2026-10-15 C 路线）。
///
/// <para><b>为什么不再整表替换</b>：写操作（删除 / 新建 / 改名 / 移动）之后的目标行序与旧集合
/// 通常只差一两行——旧实现一律 Reset 整表重建，10K 目录实测把"写后刷新"拖到 118–231ms。
/// 增量同步把「目标行序 vs 现有行集合」折算成最小编辑脚本（按位置 Remove / Insert / Move），
/// 只对真动了的行发集合通知；行对象原地复用（展示字段写回，见
/// <see cref="BrowserRowViewModel.ApplyFolder"/> / <see cref="BrowserRowViewModel.ApplyLink"/>），
/// 容器保留、选中 / 改名 / 落点投影（全是宿主状态投影）跨编辑存活。</para>
///
/// <para><b>回退口径</b>：编辑脚本超过 <see cref="MaxIncrementalEdits"/>（换目录、整库级批量变更、
/// 排序列整体重排等），逐条通知比一次 Reset 更贵——回退整表替换，与原行为一致。</para>
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>目标行序中的一项（轻量三元组：Id / 类型 / 来源 DTO）——先算序，再决定"复用旧行还是建新行"。</summary>
    private readonly record struct RowPlan(string Id, bool IsFolder, FolderDto? Folder, LinkDto? Link);

    /// <summary>增量编辑脚本上限：单次写操作的影响面远小于此；超过即整表替换更便宜。</summary>
    private const int MaxIncrementalEdits = 256;

    /// <summary>新建一行（唯一构造口：字段映射在 <see cref="BrowserRowViewModel.FromFolder"/> / <see cref="BrowserRowViewModel.FromLink"/>）。</summary>
    private BrowserRowViewModel CreateRow(RowPlan plan)
        => plan.IsFolder
            ? BrowserRowViewModel.FromFolder(plan.Folder!, this, _clipboardCtl.IsCutInClipboard(plan.Id, true))
            : BrowserRowViewModel.FromLink(plan.Link!, this, _clipboardCtl.IsCutInClipboard(plan.Id, false));

    /// <summary>把目标值写回既有行（只写变了的字段，每处一次属性通知）。</summary>
    private void ApplyPlan(BrowserRowViewModel row, RowPlan plan)
    {
        if (plan.IsFolder) row.ApplyFolder(plan.Folder!, _clipboardCtl.IsCutInClipboard(plan.Id, true));
        else row.ApplyLink(plan.Link!, _clipboardCtl.IsCutInClipboard(plan.Id, false));
    }

    /// <summary>两个行序是否同一批行（条数、逐项 Id 与类型一致）= 只需原地写回字段。</summary>
    private static bool SameRowIdentity(IReadOnlyList<BrowserRowViewModel> rows, IReadOnlyList<RowPlan> plans)
    {
        if (rows.Count != plans.Count) return false;
        for (var i = 0; i < plans.Count; i++)
            if (!string.Equals(rows[i].Id, plans[i].Id, StringComparison.Ordinal)
                || rows[i].IsFolder != plans[i].IsFolder) return false;
        return true;
    }

    /// <summary>
    /// 增量集合编辑：把 <see cref="Rows"/> 收敛到目标行序。返回 false = 放弃（调用方走整表替换）。
    ///
    /// <para>算法（先在模拟序列上把脚本算完、超编即放弃——失败路径一次都不碰真集合）：
    /// ① 移除：旧有新无的 Id 按旧索引从后往前 RemoveAt（索引不前移）；
    /// ② 对齐：目标序列从左到右逐位收敛——该位已就位则跳过；否则目标 Id 仍在（前缀之后必有）则
    /// 把那一行挪来（RemoveAt + InsertAt，行对象复用）；已不存在于旧集合则插入新行。
    /// 位置 j 之前的区间恒已就位，目标项存在时必在 p &gt; j，无向左挪的分支；</para>
    /// <para>收尾逐行写回目标值（保留行原地 Apply、新行即建即含值）。</para>
    /// </summary>
    private bool TryApplyIncrementalEdit(IReadOnlyList<RowPlan> next)
    {
        if (Rows.Count == 0 || next.Count == 0) return false;   // 空进空出交给整表替换（一次 Reset 本就便宜）

        var oldById = new Dictionary<string, BrowserRowViewModel>(Rows.Count, StringComparer.Ordinal);
        foreach (var row in Rows)
            if (!oldById.TryAdd(row.Id, row)) return false;   // 重复 Id = 不该存在的坏集合，保守回退
        var newIdSet = new HashSet<string>(next.Count, StringComparer.Ordinal);
        foreach (var plan in next)
            if (!newIdSet.Add(plan.Id)) return false;

        // —— 模拟序列上算脚本（keepIds = 旧序列去掉"新序没有的"，随后按目标序收敛）——
        var keepIds = new List<string>(Rows.Count);
        var removedIndexes = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            if (newIdSet.Contains(Rows[i].Id)) keepIds.Add(Rows[i].Id);
            else removedIndexes.Add(i);
        }

        var edits = removedIndexes.Count;
        if (edits > MaxIncrementalEdits) return false;

        var posById = new Dictionary<string, int>(keepIds.Count, StringComparer.Ordinal);
        for (var i = 0; i < keepIds.Count; i++) posById[keepIds[i]] = i;

        var moves = new List<(int At, int From)>();   // From < 0 = 新插入
        for (var j = 0; j < next.Count; j++)
        {
            var want = next[j].Id;
            if (j < keepIds.Count && string.Equals(keepIds[j], want, StringComparison.Ordinal)) continue;
            if (posById.TryGetValue(want, out var p))
            {
                moves.Add((j, p));
                keepIds.RemoveAt(p);
                keepIds.Insert(j, want);
                for (var k = j; k <= p; k++) posById[keepIds[k]] = k;
            }
            else
            {
                moves.Add((j, -1));
                keepIds.Insert(j, want);
                for (var k = j; k < keepIds.Count; k++) posById[keepIds[k]] = k;
            }
            if (++edits > MaxIncrementalEdits) return false;
        }

        // —— 真集合上执行（模拟与真集合逐位镜像）——
        for (var i = removedIndexes.Count - 1; i >= 0; i--) Rows.RemoveAt(removedIndexes[i]);
        foreach (var (at, from) in moves)
        {
            if (from < 0)
            {
                Rows.Insert(at, CreateRow(next[at]));
            }
            else
            {
                var row = Rows[from];
                Rows.RemoveAt(from);
                Rows.Insert(at, row);
            }
        }
        for (var i = 0; i < next.Count; i++) ApplyPlan(Rows[i], next[i]);

        // 右键命中行被这次编辑删掉 → 引用作废（删除文案随之复位）；保留的行对象存活，引用继续有效
        if (_contextRow is not null && !newIdSet.Contains(_contextRow.Id)) SetContextRow(null);
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站的**还原**（唯一入口）：把选中条目（混合：链接 + 单元）经一条 <c>trash.restore_batch</c> 还原——
/// 缺省回删除前位置（origin），或显式回根目录；结果如实播报（回落根 / 自动编号 / 同 URL 重复）。
/// 还原**不需要**确认（非破坏动作）；刷新归引擎事件（300ms 防抖），写操作不显式刷新（与全页同口径）。
/// </summary>
public partial class TrashViewModel
{
    private Task RestoreSelectionAsync(string to)
    {
        var rows = SelectedRows.ToList();
        if (rows.Count == 0) return Task.CompletedTask;
        return ExecuteRestoreAsync(
            rows.Where(r => !r.IsFolder).Select(r => r.Id).ToList(),
            rows.Where(r => r.IsFolder).Select(r => r.Id).ToList(),
            to);
    }

    /// <summary>树单元节点的还原（节点作用域——与节点右键菜单逐项对应；虚根/链接叶子不可还原）。</summary>
    private Task RestoreNodeAsync(TrashNode? node, string to)
        => node == null || node.IsRoot || node.IsLink
            ? Task.CompletedTask
            : ExecuteRestoreAsync([], [node.Id], to);

    /// <summary>只读详情覆盖层的还原：作用对象 = **覆盖层正在展示的那一份书签快照**
    /// （与选中集合无关、也不受覆盖层门禁影响——动作就发生在眼前这一项上）。</summary>
    private Task RestoreDetailAsync(string to)
    {
        var id = _detailLinkId;
        return id == null ? Task.CompletedTask : ExecuteRestoreAsync(new[] { id }, Array.Empty<string>(), to);
    }

    private async Task ExecuteRestoreAsync(IReadOnlyList<string> linkIds, IReadOnlyList<string> folderIds, string to)
    {
        try
        {
            var result = await _client.TrashRestoreBatchAsync(linkIds, folderIds, to);
            SetSelection(Array.Empty<string>());   // 条目已离开回收站：绝不残留选中（详情覆盖层由"条目消失即关"收尾）
            StatusText = BuildRestoreSummary(result.Data, to, linkIds.Count + folderIds.Count);
        }
        catch (Exception ex)
        {
            LpLog.Error($"还原失败（链接 {linkIds.Count} / 单元 {folderIds.Count}；to={to}）", ex);
            ShowError("还原失败", ex.Message);
            await LoadAsync();   // 请求可能已在服务端生效（超时等）→ 重拉，避免 UI 残留已还原条目
        }
    }

    /// <summary>结果播报（D4/D7 定稿）：计数 + 回落项前列名称 + 自动编号计数 + 同 URL 重复计数（不阻断、不合并）。</summary>
    private static string BuildRestoreSummary(TrashRestoreBatchResult? r, string to, int selectedCount)
    {
        if (r == null) return "已还原";
        var head = $"已还原 {selectedCount} 项{(to == "root" ? "到根目录" : "到原位置")}";
        var notes = new List<string>();
        if (r.FellBackToRoot.Count > 0)
        {
            var preview = string.Join("、", r.FellBackToRoot.Take(3).Select(n => $"「{n}」"));
            var tail = r.FellBackToRoot.Count > 3 ? " 等" : string.Empty;
            notes.Add($"{r.FellBackToRoot.Count} 项原位置已不存在落根（{preview}{tail}）");
        }
        if (r.Renamed.Count > 0) notes.Add($"{r.Renamed.Count} 项自动编号");
        if (r.DuplicateUrls > 0) notes.Add($"{r.DuplicateUrls} 项与目标位置已有链接同 URL（未合并）");
        return notes.Count == 0 ? head : $"{head}（{string.Join("；", notes)}）";
    }
}

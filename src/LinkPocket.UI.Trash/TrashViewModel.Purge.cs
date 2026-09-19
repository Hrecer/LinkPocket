using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站的**永久删除**（单条 / 批量；两阶段确认令牌；不可恢复）——回收站唯一的破坏性写操作。
/// 回收站条目只能被**打开查看**与**处置**（还原 / 永久删除），站内没有任何改变归属/层级的能力。
/// </summary>
public partial class TrashViewModel
{
    /// <summary>节点右键「永久删除」（单元 = 整子树）。</summary>
    private async Task PurgeNodeAsync(TrashNode? node)
    {
        if (node == null || node.IsRoot || node.IsLink) return;
        await PurgeIdsAsync(new[] { node.Id }, isFolder: true, name: node.Name);
    }

    /// <summary>只读详情覆盖层的「永久删除」：作用对象 = **覆盖层正在展示的那一份书签快照**
    /// （与选中集合无关、也不受覆盖层门禁影响）。删完条目消失 → 覆盖层由"条目消失即关"收尾。</summary>
    private async Task PurgeDetailAsync()
    {
        var id = _detailLinkId;
        if (id == null) return;
        await PurgeIdsAsync(new[] { id }, isFolder: false, name: DetailPane.Title);
    }

    /// <summary>Delete 键 / 工具栏「永久删除」：删除当前选中项（可多选，批量命令 purge_batch）。</summary>
    private async Task PurgeSelectionGuardedAsync()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count == 0) return;

        var name = rows.Count == 1 ? rows[0].Name : $"这 {rows.Count} 项";
        var folderIds = rows.Where(r => r.IsFolder).Select(r => r.Id).ToList();
        var linkIds = rows.Where(r => !r.IsFolder).Select(r => r.Id).ToList();

        if (!ConfirmPurge(name, rows.Count, rows.Any(r => r.IsFolder))) return;

        try
        {
            await EngineConfirm.RunAsync(token => _client.TrashPurgeBatchAsync(linkIds, folderIds,
                new CallOptions { ConfirmToken = token }));
            SetSelection(Array.Empty<string>());   // 条目已消失：绝不残留指向已删条目的选中态
        }
        catch (Exception ex)
        {
            Logger.Error($"永久删除失败（已强制刷新回收站）：{name}", ex);
            ShowError("永久删除失败", ex.Message);
            await LoadAsync();   // 请求可能已在服务端生效（超时等）→ 重拉，避免 UI 残留已删条目
        }
    }

    private async Task PurgeIdsAsync(IReadOnlyList<string> ids, bool isFolder, string name)
    {
        if (!ConfirmPurge(name, 1, isFolder)) return;
        try
        {
            await EngineConfirm.RunAsync(token => _client.TrashPurgeAsync(ids[0], isFolder,
                new CallOptions { ConfirmToken = token }));
            SetSelection(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Logger.Error($"永久删除失败（已强制刷新回收站）：{name}", ex);
            ShowError("永久删除失败", ex.Message);
            await LoadAsync();
        }
    }

    /// <summary>删除确认（规范弹窗；文案与既有回收站口径一致：明说不可恢复）。</summary>
    private bool ConfirmPurge(string name, int count, bool containsFolder)
    {
        if (Dialogs == null)
        {
            Logger.Error("对话框端口未登记：永久删除确认被跳过（无 UI 环境）", null);   // 功能不可用 ≠ 静默取消
            return false;
        }
        var message = containsFolder
            ? $"确定要永久删除{Target(name, count)}吗？\n文件夹内的全部内容将一并删除，不可恢复。"
            : $"确定要永久删除{Target(name, count)}吗？\n此操作不可恢复。";
        return Dialogs.Confirm("永久删除", message, "永久删除", "delete-forever");
    }

    private static string Target(string name, int count)
        => count == 1 ? $"「{name}」" : name;   // 多选时 name 已是「这 N 项」

    private void ShowError(string title, string message)
        => Dialogs?.Alert(title, message);
}

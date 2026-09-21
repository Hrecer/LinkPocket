using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.I18n;

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
        await PurgeIdsAsync(new[] { node.Id }, isFolder: true, node.Name, LocValue.Empty);
    }

    /// <summary>只读详情覆盖层的「永久删除」：作用对象 = **覆盖层正在展示的那一份书签快照**
    /// （与选中集合无关、也不受覆盖层门禁影响）。删完条目消失 → 覆盖层由"条目消失即关"收尾。</summary>
    private async Task PurgeDetailAsync()
    {
        var id = _detailLinkId;
        if (id == null) return;
        await PurgeIdsAsync(new[] { id }, isFolder: false, DetailPane.TitleData, DetailPane.TitleCopy);
    }

    /// <summary>Delete 键 / 工具栏「永久删除」：删除当前选中项（可多选，批量命令 purge_batch）。</summary>
    private async Task PurgeSelectionGuardedAsync()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count == 0) return;

        var name = rows.Count == 1 ? Loc.K("trash.thisOne", rows[0].Name) : Loc.K("trash.thisMany", rows.Count);
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
            LpLog.Error($"permanent delete failed (trash force-refreshed)", ex);
            ShowError(Loc.T("trash.purgeFailed"), Loc.T("err.unexpected"));
            await LoadAsync();   // 请求可能已在服务端生效（超时等）→ 重拉，避免 UI 残留已删条目
        }
    }

    /// <param name="nameData">条目的用户数据名（标题 / 单元名）。</param>
    /// <param name="nameCopy">条目的文案名（数据缺失时的兜底，如「无名称」）；空 = 用数据名。</param>
    private async Task PurgeIdsAsync(IReadOnlyList<string> ids, bool isFolder, string nameData, LocValue nameCopy)
    {
        if (!ConfirmPurge(nameCopy.IsEmpty ? Loc.K("trash.thisOne", nameData) : nameCopy, 1, isFolder)) return;
        try
        {
            await EngineConfirm.RunAsync(token => _client.TrashPurgeAsync(ids[0], isFolder,
                new CallOptions { ConfirmToken = token }));
            SetSelection(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            LpLog.Error("permanent delete failed (trash force-refreshed)", ex);
            ShowError(Loc.T("trash.purgeFailed"), Loc.T("err.unexpected"));
            await LoadAsync();
        }
    }

    /// <summary>删除确认（规范弹窗；文案与既有回收站口径一致：明说不可恢复）。</summary>
    private bool ConfirmPurge(LocValue name, int count, bool containsFolder)
    {
        if (Dialogs == null)
        {
            LpLog.Error("dialog port not registered: the permanent-delete confirmation was skipped (no UI environment)", null);   // 功能不可用 ≠ 静默取消
            return false;
        }
        // 单选时把名字括起来；多选时 name 已是「这 N 项」整句——两种情况都在渲染边界拼
        var target = count == 1 ? Loc.K("trash.oneName", name) : name;
        var title = Loc.T("trash.menu.purge");
        var message = containsFolder
            ? Loc.T("trash.purge.confirmFolders", target.Resolve())
            : Loc.T("trash.purge.confirmPlain", target.Resolve());
        return Dialogs.Confirm(title, message, title, "delete-forever");
    }

    private void ShowError(string title, string message)
        => Dialogs?.Alert(title, message);
}

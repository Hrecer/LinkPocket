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
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：文件操作（移动/复制/新建/删除/打开/复制地址）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>
    /// 单项移动/复制结果（**三态**，结果文案必须如实分派）：
    /// <see cref="Done"/> = 已执行；<see cref="Skipped"/> = 无操作跳过（同目录，不是错误）；
    /// <see cref="Failed"/> = 执行失败（源已消失、引擎拒绝等，必须留痕）。
    /// </summary>
    private enum OpOutcome { Done, Skipped, Failed }

    /// <summary>移动文件夹（目标层同层唯一编号由引擎负责，见 Kernel IFolderNaming）。
    /// 单项失败不中断整批（与 MoveLink/Copy* 一致），失败必须留痕（观测面铁律）。</summary>
    private async Task<OpOutcome> MoveFolderAsync(string folderId, string? target, List<LocValue> renamedNotes,
        LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            object name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : Loc.K("ui.noun.folder");
            var moved = await _client.FolderMoveAsync(folderId, target, o);
            // 编号由引擎统一负责（单一实现）；UI 只按返回名生成提示，绝不自己再补发一条改名命令
            //（那正是"移动 + 改名两次写、且批量内各算各的"造成 6 个同名文件夹的根因）
            var resolved = moved.Data?.Name;
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, name as string, StringComparison.Ordinal))
                renamedNotes.Add(Loc.K("browser.renamedNote", name, resolved));
            return OpOutcome.Done;
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to move folder '{folderId}'", ex);
            return OpOutcome.Failed;
        }
    }

    /// <summary>移动链接：同目录 = <see cref="OpOutcome.Skipped"/>（无操作，非错误）；源已消失/引擎拒绝 = 失败（留痕）。</summary>
    private async Task<OpOutcome> MoveLinkAsync(string linkId, string? target, LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取后 FirstOrDefault）
            if (link == null)
            {
                LpLog.Error($"failed to move link '{linkId}': the source no longer exists");
                return OpOutcome.Failed;
            }
            if (NormalizeParentId(link.ListId) == target) return OpOutcome.Skipped;
            await _client.LinkUpdateAsync(linkId, listId: target, o: o);
            return OpOutcome.Done;
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to move link '{linkId}'", ex);
            return OpOutcome.Failed;
        }
    }

    /// <summary>把父目录 ID 归一化成可比较的值（null = 根）。</summary>
    private static string? NormalizeParentId(string? parentId) => parentId;

    private static LocValue FormatRenamedNotes(List<LocValue> notes)
        => notes.Count == 0 ? LocValue.Empty : Loc.K("browser.status.renamedSuffix", Join(notes, LocValue.Empty));

    // —— 剪切 / 复制 / 粘贴（Ctrl+X / C / V）——

    // —— 刚置入项临时置尾（Windows 资源管理器语义）——
    // 粘贴（复制/剪切）完成后，新项**临时排在列表末尾**（不参与排序、不按名称归位），并被选中、滚入视口——
    // 文件多、滚到中部的场景下也能立刻看到刚粘贴的东西（微软官方口径：避免文件多时找不到）。
    // **只有真刷新才归位**：重新进入目录（含点当前目录的树行/虚根）、点列头排序、F5（显式发起的导航刷新）；
    // 后台事件刷新（写操作后的 300ms 防抖）**绝不归位**——否则粘贴后的那次刷新就把置尾效果抹掉了。

    /// <summary>深拷贝文件夹（目标层同层唯一编号由引擎负责）。失败必须留痕（观测面铁律）。</summary>
    private async Task<(OpOutcome Outcome, string? NewId)> CopyFolderAsync(string folderId, string? target,
        List<LocValue> renamedNotes, LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            object name = _folderMap.TryGetValue(folderId, out var info) ? info.Name : Loc.K("ui.noun.folder");
            var copy = await _client.FolderCopyAsync(folderId, target, o);
            var newId = copy.Data?.NewFolderId;
            if (string.IsNullOrEmpty(newId))
            {
                LpLog.Error($"failed to copy folder '{name}': the engine returned no new ID");
                return (OpOutcome.Failed, null);
            }
            // 副本名由引擎编号决定（folders.copy 返回最终名）；UI 只按差异生成提示
            var resolved = copy.Data?.Name;
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, name as string, StringComparison.Ordinal))
                renamedNotes.Add(Loc.K("browser.renamedNote", name, resolved));
            return (OpOutcome.Done, newId);
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to copy folder '{folderId}'", ex);
            return (OpOutcome.Failed, null);
        }
    }

    /// <summary>复制书签（全量字段）。失败必须留痕（观测面铁律）。
    /// 链接标题**不做唯一化**：链接身份 = URL，标题只是标签。</summary>
    private async Task<(OpOutcome Outcome, string? NewId)> CopyLinkAsync(string linkId, string? target,
        LinkPocket.Contracts.CallOptions? o = null)
    {
        try
        {
            var link = await _client.LinkGetAsync(linkId);   // 单点取源（替代全量拉取）
            if (link == null)
            {
                LpLog.Error($"failed to copy link '{linkId}': the source no longer exists");
                return (OpOutcome.Failed, null);
            }

            // 复制书签 = 全量字段（URL/标题/描述/收藏/图标；内核无标签系统，无其它字段可丢）
            var created = await _client.LinkCreateAsync(link.Url,
                title: link.Title,
                description: string.IsNullOrEmpty(link.Description) ? null : link.Description,
                listId: target,
                isImportant: link.IsImportant,
                autoFetchMetadata: false,
                faviconUrl: string.IsNullOrEmpty(link.FaviconUrl) ? null : link.FaviconUrl,
                o: o);
            var newId = created.Data?.LinkId;
            return string.IsNullOrEmpty(newId) ? (OpOutcome.Failed, null) : (OpOutcome.Done, newId);
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to copy link '{linkId}'", ex);
            return (OpOutcome.Failed, null);
        }
    }

    /// <summary>新建文件夹的默认名（Windows 口径；同层撞名由引擎自动编号「新建文件夹 (2)」）。</summary>
    /// <summary>新建文件夹的默认名：<b>建夹那一刻</b>取出成品名（落库的是用户数据，不是待翻译的键）。</summary>
    private static string DefaultFolderName()
    {
        return Loc.T("common.newFolder");
    }

    /// <summary>
    /// 新建文件夹（Windows 口径，**不再弹输入框**）：
    /// 直接以默认名创建（同层撞名由引擎自动编号），随后**立刻进入就地改名**——
    /// 新项临时置尾 + 选中 + 滚入视口 + 聚焦编辑框；行要等事件刷新（300ms 防抖）重建后才出现，
    /// 故改名会话与定位请求先待命，重建时由投影/消费落地（**不做显式刷新**：显式 + 事件双重刷新是"刷两遍"的根因）。
    /// 参数为空 → 在当前目录新建；参数为目标文件夹 ID（行右键）→ 在该文件夹内新建
    /// （那种情况新文件夹不在当前视图里，只如实报告，不进入不可见的改名态）。
    /// </summary>
    private async Task NewFolderAsync(string? parentId)
    {
        // 参数为空 → 当前目录；参数为真实文件夹 ID → 在该文件夹内新建
        var target = FolderIds.IsRoot(parentId) ? Controller.CurrentFolderId : parentId;
        try
        {
            var created = await _client.FolderCreateAsync(DefaultFolderName(), parentId: target);
            var newId = created.Data?.FolderId;
            if (string.IsNullOrEmpty(newId)) return;
            var name = created.Data?.Name ?? DefaultFolderName();
            StatusText = Loc.K("browser.status.folderCreated", name);

            // 新项不在当前视图（在别的文件夹内新建）→ 无可见行可改名，只报告
            if (NormalizeParentId(target) != NormalizeParentId(Controller.CurrentFolderId)) return;

            // Windows 口径：新项临时置尾（不参与排序）+ 选中 + 滚入视口，并直接进入就地改名
            MarkRecentlyPinned(new[] { newId });
            SetSelection(new[] { newId }, newId);
            _pendingFocusId = newId;
            BeginRename(newId, isFolder: true, name, BrowserPane.Main);
            // 刷新交给后端事件（300ms 防抖）——写操作后不做显式刷新（WARNINGS #18）
        }
        catch (Exception)
        {
            ShowError(Loc.T("status.newFolderFailed"), Loc.T("err.unexpected"));
        }
    }

    private async Task DeleteNodeAsync(FolderNode? node)
    {
        if (node == null || node.FolderId == null) return; // 根节点「全部书签」不是文件夹
        // Windows 口径：删除 = 移入回收站，不再提示"子文件夹一并删除"
        if (!ConfirmDelete(Loc.T("common.title.deleteFolder"), Loc.T("browser.confirm.folderToTrash", node.Name)))
            return;
        try
        {
            await _client.FolderDeleteAsync(node.FolderId, "trash_links");
            StatusText = Loc.K("browser.status.folderTrashed", node.Name);
            // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync），
            // 这里不再显式刷新 —— 显式 + 事件双重刷新就是"删完刷两次"的根因。
        }
        catch (Exception)
        {
            ShowError(Loc.T("status.deleteFailed"), Loc.T("err.unexpected"));
        }
    }

    private void CopyUrl(BrowserRowViewModel? row)
    {
        if (row == null || row.IsFolder || string.IsNullOrEmpty(row.Url)) return;
        try
        {
            System.Windows.Clipboard.SetText(row.Url);
            StatusText = Loc.K("status.linkCopied");
        }
        catch { /* 剪贴板被占用时静默 */ }
    }

    private async Task DeleteSelectedAsync()
    {
        var sel = SelectedRows.ToList();
        if (sel.Count == 0) return;
        if (!await ConfirmDeleteAsync(sel)) return;
        await DeleteItemsAsync(sel);
    }

    /// <summary>删除确认文案（Windows 口径：一切删除 = 移入回收站，不罗列子项后果）。实例方法：确认走对话框端口。</summary>
    private Task<bool> ConfirmDeleteAsync(IReadOnlyList<BrowserRowViewModel> items)
    {
        var folders = items.Count(r => r.IsFolder);
        var links = items.Count - folders;
        LocValue msg;
        if (folders > 0 && links > 0)
            msg = Loc.K("browser.confirm.mixedToTrash", folders, links);
        else if (folders > 0)
            msg = folders == 1
                ? Loc.K("browser.confirm.folderToTrash", items[0].Name)
                : Loc.K("browser.confirm.foldersToTrash", folders);
        else
            msg = links == 1
                ? Loc.K("browser.confirm.linkToTrash", items[0].Name)
                : Loc.K("browser.confirm.linksToTrash", links);

        return Task.FromResult(ConfirmDelete(Loc.T("common.delete"), msg.Resolve()));
    }

    private async Task DeleteItemsAsync(IReadOnlyList<BrowserRowViewModel> items)
    {
        // 单项失败不中断整批（与 Move/Paste 口径一致）；失败项留痕，成功数如实报
        var deleted = 0;
        var failed = 0;
        foreach (var item in items)
        {
            try
            {
                if (item.IsFolder) await _client.FolderDeleteAsync(item.Id, "trash_links");
                else await _client.LinkTrashAsync(item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                LpLog.Error($"failed to delete '{item.Name}' (Retry skips this item)", ex);   // 观测面铁律：失败必须暴露
            }
        }

        // 文案按实际结果分派 —— 全部成功 / 全部失败 / 部分成功（原 failed>=deleted 会掩盖"部分成功"）
        StatusText = failed == 0
            ? Loc.K("browser.status.deletedCount", deleted)
            : deleted == 0
                ? Loc.K("browser.status.deleteFailed")
                : Loc.K("browser.status.deletedWithFailures", deleted, failed);
        if (deleted > 0) ClearSelection();
        // 刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）——
        // 显式 + 事件双重刷新就是"删完刷两次"的根因。失败项留在列表里，下次再删即可。
    }

    /// <summary>
    /// F2：对当前唯一选中项进入就地改名。编辑面 = 当前活跃栏（Windows 口径：哪一栏有焦点就在哪一栏改），
    /// 树里找不到对应节点时回落到主栏。选中集合是唯一事实来源，两栏共用同一个目标 ID。
    /// </summary>
    private void BeginRenameSelection()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row == null) return;
        if (ActivePane == BrowserPane.Tree)
        {
            var node = AllTreeNodes().FirstOrDefault(n => (n.IsLink ? n.Id : n.FolderId) == row.Id);
            if (node != null) { BeginRenameNode(node); return; }
        }
        BeginRenameRow(row);
    }

    private async Task OpenSelectedAsync()
    {
        var row = SelectedRows.FirstOrDefault();
        if (row != null) await OpenRowAsync(row);
    }

    private async Task DeleteRowAsync(BrowserRowViewModel? row)
    {
        if (row == null) return;
        // 右键命中的行已在多选集合内 → 批量删除；否则只删该行（Explorer 语义）
        var targets = row.IsSelected && SelectionCount > 1
            ? SelectedRows.ToList()
            : new List<BrowserRowViewModel> { row };
        if (!await ConfirmDeleteAsync(targets)) return;
        await DeleteItemsAsync(targets);
    }

}

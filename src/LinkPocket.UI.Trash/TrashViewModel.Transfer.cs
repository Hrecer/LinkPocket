using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.ViewModels;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站的**站内搬移流水线** + 永久删除（与浏览页传输流水线同构但语义更窄）：
/// <list type="bullet">
/// <item>载荷 = 共享的 <see cref="DragItem"/>（与浏览页同一种形状，拖动集合唯一出口在 <see cref="BuildDragItems"/>）；</item>
/// <item>**只允许移动**（站内搬移不产生副本——ID 唯一）：无 Ctrl 复制、无剪贴板入口、无撤销分组；</item>
/// <item>三态如实分派（已移动 / 已在目标位置 / 失败）+ 成环（单元移入自身或自身子树）统一弹窗；</item>
/// <item>永久删除 = 单条 / 批量（<c>trash.purge_batch</c>），两阶段确认令牌，不可恢复。</item>
/// </list>
/// <para>入口只有两个：拖拽落点（左键 / 右键拖拽菜单）与树/行右键菜单——全部只构造请求，
/// 逐项循环、成环收集、结果文案只在本文件。</para>
/// </summary>
public partial class TrashViewModel
{
    // ———————— 拖拽载荷（拖动集合唯一出口） ————————

    /// <summary>
    /// 构造本次拖动集合：拖未选中项 → 先单选该项；拖已选中项 → 拖动整个选中集合。
    /// <paramref name="grabbedId"/> = 用户抓住的那一项（置于**首位**，保证首项 = 抓住项）。
    /// </summary>
    public IReadOnlyList<DragItem> PrepareDragFromRow(TrashRowViewModel row)
    {
        if (!IsSelectedId(row.Id)) SetSelection(new[] { row.Id });
        return BuildDragItems(row.Id);
    }

    /// <summary>树节点拖拽载荷：虚根不可拖（无实体身份）；链接叶子可拖；单元可拖。</summary>
    public IReadOnlyList<DragItem> PrepareDragFromNode(TrashNode node)
    {
        if (node.IsRoot) return Array.Empty<DragItem>();
        if (!IsSelectedId(node.Id)) SetSelection(new[] { node.Id });
        return BuildDragItems(node.Id);
    }

    /// <summary>拖动集合唯一出口：按选中集合投影成 DragItem，抓住项置首（HashSet 无序 → 必须显式排序）。</summary>
    private IReadOnlyList<DragItem> BuildDragItems(string grabbedId)
    {
        var items = new List<DragItem>();
        foreach (var row in Rows.Where(r => _selectedIds.Contains(r.Id)))
            items.Add(new DragItem(row.Id, row.IsFolder, row.Name));
        foreach (var node in AllTreeNodes().Where(n => !n.IsRoot && _selectedIds.Contains(n.Id)))
        {
            if (items.Any(i => i.Id == node.Id)) continue;   // 主栏已覆盖（同一实体只搬一次）
            items.Add(new DragItem(node.Id, !node.IsLink, node.Name));
        }

        var index = items.FindIndex(i => i.Id == grabbedId);
        if (index > 0)
        {
            var grabbed = items[index];
            items.RemoveAt(index);
            items.Insert(0, grabbed);
        }
        return items;
    }

    /// <summary>落点执行：把拖动集合搬到 <paramref name="targetUnitId"/>（null = 回收站根）。执行在 OLE 循环退出后。</summary>
    public Task DropItemsAsync(IReadOnlyList<DragItem> items, string? targetUnitId)
        => TransferAsync(items, targetUnitId);

    /// <summary>
    /// 站内搬移流水线（唯一实现）：逐项 <c>trash.move</c>（单项失败不中断整批、必须留痕）→
    /// 成环收集（执行层判定）→ 结果如实分派 → 成环规范弹窗。
    /// 刷新交给引擎事件（trash.changed → 300ms 防抖），写操作不显式刷新（与浏览页同口径）。
    /// </summary>
    private async Task TransferAsync(IReadOnlyList<DragItem> items, string? targetUnitId)
    {
        if (items.Count == 0) return;

        var blocked = new List<string>();   // 成环（单元移入自身 / 自身子树）→ 执行层统一拒绝并弹窗，绝不静默
        var done = 0;
        var skipped = 0;
        var failed = 0;

        try
        {
            foreach (var item in items)
            {
                if (item.IsFolder && (item.Id == targetUnitId || IsUnitInSubtree(item.Id, targetUnitId)))
                {
                    blocked.Add(DisplayName(item));
                    continue;
                }

                if (IsAlreadyInTarget(item, targetUnitId))
                {
                    skipped++;   // 已在目标位置 = 无操作（不是错误）
                    continue;
                }

                try
                {
                    await _client.TrashMoveAsync(item.Id, item.IsFolder, targetUnitId);
                    done++;
                    SyncSnapshotAfterMove(item, targetUnitId);   // 快照内同步：连续搬移的"已在目标位置"判定不依赖防抖刷新
                }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Error($"回收站站内搬移失败：{item.Id} → {targetUnitId ?? RootDisplayName}", ex);   // 观测面：失败必须留痕
                }
            }

            var parts = new List<string>();
            if (done > 0) parts.Add($"已移动 {done} 项");
            if (skipped > 0) parts.Add($"{skipped} 项已在目标位置");
            if (failed > 0) parts.Add($"{failed} 项失败（详见日志）");
            StatusText = parts.Count > 0 ? string.Join("，", parts) : "没有需要移动的项目";

            if (blocked.Count > 0) ShowError("无法移动", BlockedMessage(blocked));
        }
        catch (Exception ex)
        {
            StatusText = "移动失败";
            ShowError("移动失败", ex.Message);
        }
    }

    /// <summary>搬移成功后同步内存快照（只服务于后续判定：连续搬移的"已在目标位置"、地址栏路径解析；
    /// 视图仍由引擎事件防抖刷新重建——绝不在这里手工重建树/行）。</summary>
    private void SyncSnapshotAfterMove(DragItem item, string? targetUnitId)
    {
        if (item.IsFolder)
        {
            if (_unitById.TryGetValue(item.Id, out var unit)) unit.ParentTrashFolderId = targetUnitId;
        }
        else
        {
            var link = _allLinks.FirstOrDefault(l => l.Id == item.Id);
            if (link != null) link.TrashFolderId = targetUnitId;
        }
    }

    /// <summary>该项是否已在目标位置（单元 = 父单元；链接 = 归属单元）——用于"无操作"如实计数。</summary>
    private bool IsAlreadyInTarget(DragItem item, string? targetUnitId)
    {
        if (item.IsFolder)
            return GetParentId(item.Id) == targetUnitId;
        return _allLinks.FirstOrDefault(l => l.Id == item.Id)?.TrashFolderId == targetUnitId;
    }

    /// <summary>成环判据（执行层）：目标是否落在该单元的子树内（含自身）——快照内判定，与引擎同口径。</summary>
    private bool IsUnitInSubtree(string unitId, string? targetUnitId)
    {
        if (targetUnitId == null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = targetUnitId;
        while (cur != null && seen.Add(cur))
        {
            if (cur == unitId) return true;
            cur = GetParentId(cur);
        }
        return false;
    }

    private static string DisplayName(DragItem item)
        => !string.IsNullOrEmpty(item.Name) ? item.Name : item.IsFolder ? "文件夹" : "链接";

    private static string BlockedMessage(IReadOnlyList<string> names)
        => names.Count == 1
            ? $"无法将文件夹「{names[0]}」移到它自己或它的子文件夹里。"
            : "以下文件夹无法移到它们自己或它们的子文件夹里：\n" + string.Join("、", names.Select(n => $"「{n}」"));

    private void ShowError(string title, string message)
        => Dialogs?.Alert(title, message);

    // ———————— 永久删除（单条 / 批量；两阶段确认） ————————

    /// <summary>节点右键「永久删除」（单元 = 整子树）。</summary>
    private async Task PurgeNodeAsync(TrashNode? node)
    {
        if (node == null || node.IsRoot || node.IsLink) return;
        await PurgeIdsAsync(new[] { node.Id }, isFolder: true, name: node.Name);
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
}

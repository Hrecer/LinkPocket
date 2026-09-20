using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页的**传输流水线**（移动 / 复制）——三态如实分派、成环拒绝、置尾选中、撤销分组，
/// 全部只有这一份实现。
///
/// <para>为什么必须是一条流水线（而不是每个入口各写一遍）：拖拽落点、右键拖拽菜单、剪贴板粘贴
/// 在语义上**只差两个参数**——「搬哪些项」与「移动还是复制」。曾经拖拽与粘贴各有一份逐项循环，
/// 于是成环收集、置尾/选中、状态文案、失败处理都被写了两次（改一处忘一处 = 长期风险）。
/// 现在三者共用 <see cref="TransferAsync"/>：新入口只允许**构造请求**，不允许另写循环。</para>
///
/// <para>事实来源：本次搬运的模式由 VM 落点状态（<see cref="BrowserDropTarget.Mode"/>）决定，
/// 提示文案与光标都是它的投影；执行动作也读它，绝不第二次判定 Ctrl。</para>
/// </summary>
public partial class BrowserViewModel
{
    // —— 入口一：拖拽落点（左键拖拽 / 右键拖拽菜单都走这里）——

    /// <summary>
    /// 落点执行：把拖动集合搬到 <paramref name="targetFolderId"/>（null = 根）。
    /// <paramref name="mode"/> 由视图从落点状态取出（= 松手那一刻提示条说的那个动作）。
    /// </summary>
    public Task DropItemsAsync(IReadOnlyList<DragItem> items, string? targetFolderId, TransferMode mode)
        => TransferAsync(items, targetFolderId, mode, TransferOrigin.Drag);

    // —— 入口二：剪贴板粘贴（Ctrl+V / 右键菜单）——

    /// <summary>
    /// 粘贴：把剪贴板载荷投影成**与拖拽同一种**载荷项（<see cref="DragItem"/>），再走同一条流水线。
    /// 剪贴板自己存的是 ID 清单（存储格式），不构成第二份传输实现。
    /// </summary>
    private async Task PasteAsync()
    {
        var payload = Clipboard.BrowserPayload;
        if (payload == null || payload.IsEmpty) return;

        var target = Controller.CurrentFolderId;
        if (payload.IsCut && payload.SourceFolderId == target)
        {
            // 剪切到源目录 = 无操作（Windows 同口径）；但必须明确提示——
            // 含糊的"没反应"曾让用户以为"剪切后粘贴不了 = 数据不一致"（实为同目录粘贴被静默早退）。
            // 载荷**保留**（剪切态不消费）：导航到目标文件夹后仍可粘贴。
            StatusText = "剪切的项目已在当前文件夹中（先进入目标文件夹再粘贴）";
            return;
        }

        var items = payload.FolderIds
            .Select(id => new DragItem(id, true, FolderDisplayName(id)))
            .Concat(payload.LinkIds.Select(id => new DragItem(id, false, LinkDisplayName(id))))
            .ToList();

        await TransferAsync(items, target, payload.IsCut ? TransferMode.Move : TransferMode.Copy,
            TransferOrigin.Clipboard);
    }

    // 拖拽收尾（成环弹窗）**不再有视图侧入口**（2026-09-19 用户要求统一口径）：
    // 过去视图在松手后自己判一次成环并弹窗，而粘贴路径在流水线里弹——两套口径（且视图那次是在
    // OLE 拖拽循环里弹的窗，浮层还挂在屏幕上）。现在统一为：
    //   「落点是不是自己/自己的子文件夹」只在**执行层**判定（本文件 `TransferAsync` 的 `blocked`），
    //   **拖拽（左键落点 / 右键拖拽菜单）与剪切粘贴共用同一个弹窗**；
    //   视图只负责在拖拽循环退出之后把 Drop 记下的意图交给 `DropItemsAsync`。
    // Esc 取消 = OLE 不派发 Drop → 待执行单为空 → 什么都不会发生（结构性保证，无需判据）。

    // —— 唯一实现 ——

    /// <summary>
    /// 传输核心：逐项分派（文件夹 / 链接 × 移动 / 复制）→ 汇总三态 → 收尾（落位、剪切载荷消费、
    /// 结果如实分派、成环弹窗）。所有入口都必须经此，不允许在别处再写一遍搬运。
    ///
    /// <para>单项失败**不中断整批**（与既有口径一致）：失败项计数 + <c>LpLog.Error</c> 留痕（观测面铁律），
    /// 状态栏如实报"N 项失败"，绝不把部分失败含混成"已完成"。</para>
    ///
    /// <para>刷新统一交给后端事件（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）：
    /// 显式刷新 + 事件刷新 = 主栏刷两遍；写操作也不得占用 IsLoading（它是刷新的重入标志）。</para>
    /// </summary>
    private async Task TransferAsync(IReadOnlyList<DragItem> items, string? target, TransferMode mode,
        TransferOrigin origin)
    {
        if (items.Count == 0) return;

        var renamedNotes = new List<string>();
        var blocked = new List<string>();   // 成环（自身 / 自己的子文件夹）→ 明确反馈，绝不静默（Explorer 同样拒绝并弹窗）
        var placed = new List<string>();    // 真正落到目标目录的实体 ID（复制 = 新 ID；移动 = 原 ID）
        var done = 0;
        var skipped = 0;
        var failed = 0;
        // 撤销分组：一次拖拽/一次粘贴 = 一个用户动作 → 引擎把这几步合并为**一条**撤销记录（一次 Ctrl+Z 撤销整批）
        var callOptions = new CallOptions(UndoGroupId: Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var item in items)
            {
                var id = item.Id;
                if (item.IsFolder)
                {
                    if (id == target || IsSelfOrDescendant(id, target))
                    {
                        blocked.Add(DisplayName(item));   // 放进自己 / 自己的子文件夹：成环，拒绝（Explorer 口径）
                        continue;
                    }

                    if (mode == TransferMode.Move)
                    {
                        if (NormalizeParentId(_folderMap.TryGetValue(id, out var info) ? info.ParentId : null) == target)
                        {
                            skipped++;   // 已在目标目录 = 无操作（不是错误）
                            continue;
                        }
                        if (await MoveFolderAsync(id, target, renamedNotes, callOptions) == OpOutcome.Done)
                        {
                            done++;
                            placed.Add(id);
                        }
                        else failed++;
                    }
                    else
                    {
                        var (outcome, newId) = await CopyFolderAsync(id, target, renamedNotes, callOptions);
                        if (outcome == OpOutcome.Done && newId != null) { done++; placed.Add(newId); }
                        else failed++;
                    }
                }
                else
                {
                    if (mode == TransferMode.Move)
                    {
                        var outcome = await MoveLinkAsync(id, target, callOptions);
                        if (outcome == OpOutcome.Done) { done++; placed.Add(id); }
                        else if (outcome == OpOutcome.Failed) failed++;
                        else skipped++;                          // Skipped（已在目标目录）= 无操作
                    }
                    else
                    {
                        var (outcome, newId) = await CopyLinkAsync(id, target, callOptions);
                        if (outcome == OpOutcome.Done && newId != null) { done++; placed.Add(newId); }
                        else failed++;
                    }
                }
            }

            // —— 收尾 ①：新项落位（临时置尾 + 选中 + 滚入视口，Windows 口径：新出现的东西要立刻被看见）——
            // 只在「目标目录就是当前所在目录」且「本次确实产生了应被看见的项」时做：
            //  · 剪贴板粘贴（复制/剪切）→ 一贯如此；
            //  · 拖拽复制 → 副本是新实体，Windows 复制完同样选中副本；
            //  · 拖拽移动 → 不置尾不选中（项只是换了目录，保持原行为）。
            var landsHere = NormalizeParentId(target) == NormalizeParentId(Controller.CurrentFolderId);
            var producesVisibleNewItems = origin == TransferOrigin.Clipboard || mode == TransferMode.Copy;
            if (done > 0 && landsHere && producesVisibleNewItems)
            {
                MarkRecentlyPinned(placed);
                SetSelection(placed, placed[0]);
                _pendingFocusId = placed[0];       // 行要等事件刷新重建后才出现 → 定位请求先待命（ConsumePendingFocus）
            }

            // —— 收尾 ②：剪切载荷消费：**只有真的有项被粘贴**才遗忘（Windows 同口径）——
            // 全部被拒/全部失败时必须保留——否则"粘贴进自己的子文件夹被拒"之后，
            // 用户导航到合法位置就再也粘贴不了了（那才是真正的"剪切不见了"）。
            if (origin == TransferOrigin.Clipboard && mode == TransferMode.Move && done > 0)
            {
                Clipboard.SetBrowserPayload(null);
                foreach (var r in Rows) r.IsCut = false;
            }

            // —— 收尾 ③：结果**如实分派**（成功 / 无操作 / 失败 分开说）——
            StatusText = origin == TransferOrigin.Clipboard
                ? ClipboardResultText(done, failed, renamedNotes)
                : DropResultText(mode, done, skipped, failed, renamedNotes);

            // —— 收尾 ④：成环：明确弹窗说明——用户明确操作后"毫无反应"会被读成数据损坏 ——
            if (blocked.Count > 0) ShowError(BlockedTitle(mode), BlockedMessage(mode, blocked));
        }
        catch (Exception ex)
        {
            StatusText = origin == TransferOrigin.Clipboard
                ? "粘贴失败"
                : mode == TransferMode.Move ? "移动失败" : "复制失败";
            ShowError(StatusText, ex.Message);
        }
    }

    /// <summary>剪贴板粘贴的结果文案（与既有口径逐字一致）。</summary>
    private static string ClipboardResultText(int done, int failed, List<string> renamedNotes)
    {
        var parts = new List<string>();
        if (done > 0) parts.Add($"已粘贴 {done} 项{FormatRenamedNotes(renamedNotes)}");
        if (failed > 0) parts.Add($"{failed} 项失败（详见日志）");
        return parts.Count > 0 ? string.Join("，", parts) : "没有可粘贴的项目";
    }

    /// <summary>拖拽落点的结果文案：成功 / 已在目标位置 / 失败分开说（**失败绝不静默**）。</summary>
    private static string DropResultText(TransferMode mode, int done, int skipped, int failed, List<string> renamedNotes)
    {
        var action = mode == TransferMode.Move ? "移动" : "复制";
        var parts = new List<string>();
        if (done > 0) parts.Add($"已{action} {done} 项{FormatRenamedNotes(renamedNotes)}");
        if (skipped > 0) parts.Add($"{skipped} 项已在目标位置");
        if (failed > 0) parts.Add($"{failed} 项失败（详见日志）");
        return parts.Count > 0 ? string.Join("，", parts) : $"没有需要{action}的项目";
    }

    /// <summary>非法目标（成环）的弹窗标题——按**动作**区分（移动 / 复制）。</summary>
    private static string BlockedTitle(TransferMode mode) => mode == TransferMode.Move ? "无法移动" : "无法复制";

    /// <summary>
    /// 非法目标的说明文案（Explorer 口径：明确说清为何不能，而不是"操作后毫无反应"）。
    /// 成环 = 目标是自己或自己的子文件夹（文件夹不能成为自己的后代）；同目录**不在此列**（那只是无操作）。
    /// </summary>
    private static string BlockedMessage(TransferMode mode, IReadOnlyList<string> names)
    {
        var action = mode == TransferMode.Move ? "移动" : "复制";
        if (names.Count == 1)
            return $"无法将文件夹「{names[0]}」{action}到它自己或它的子文件夹里。";
        return $"以下文件夹无法{action}到它们自己或它们的子文件夹里：\n" +
               string.Join("、", names.Select(n => $"「{n}」"));
    }

    /// <summary>提示文案里的项名（缺失时给中性名——文案绝不猜身份）。</summary>
    private static string DisplayName(DragItem item)
        => !string.IsNullOrEmpty(item.Name) ? item.Name : item.IsFolder ? "文件夹" : "链接";

    /// <summary>文件夹显示名（`_folderMap` 是权威；缺失时给中性名）。</summary>
    private string FolderDisplayName(string folderId)
        => _folderMap.TryGetValue(folderId, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : "文件夹";

    /// <summary>链接显示名（当前视图行优先；不可见时给中性名——只用于提示文案，不参与任何判定）。</summary>
    private string LinkDisplayName(string linkId)
        => Rows.FirstOrDefault(r => r.Id == linkId)?.Name ?? "链接";

    // —— 拖拽载荷构造（拖动集合的唯一出口；从主文件迁入）——

    /// <summary>
    /// 拖拽载荷构造（拖动集合的**唯一出口**）：把唯一选中集合（<see cref="Selection"/> 共享核心）投影成载荷项。
    /// 解析顺序：当前主栏行 → 目录树（树选中但不在当前视图的文件夹 / 链接叶子）；
    /// <paramref name="grabbedId"/>（用户**抓住的那一项**）排在首位——浮层显示的是它、执行顺序也从它开始
    /// （集合是无序的，不定首项会让"抓住的那项"与浮层显示不符，同批同名项谁拿编号也随之漂移）。
    /// 解析不到的 ID（实体已被外部事件改掉等）不进载荷，但**如实提示**，绝不静默少搬几项。
    /// </summary>
    private IReadOnlyList<DragItem> BuildDragItems(string? grabbedId = null)
    {
        var items = new List<DragItem>();
        if (!string.IsNullOrEmpty(grabbedId) && ResolveDragItem(grabbedId) is { } head) items.Add(head);
        foreach (var id in Selection.Ids)
        {
            if (string.Equals(id, grabbedId, StringComparison.Ordinal)) continue;   // 已作为首项
            if (ResolveDragItem(id) is { } item) items.Add(item);
        }

        var missing = Selection.Count - items.Count;
        if (missing > 0) StatusText = $"{missing} 项已不在当前视图，未参与本次操作";
        return items;
    }

    /// <summary>把一个实体 ID 解析成载荷项（主栏行优先，其次目录树）；解析不到 = null（绝不猜类型）。</summary>
    private DragItem? ResolveDragItem(string id)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row != null) return new DragItem(row.Id, row.IsFolder, row.Name);

        var node = AllTreeNodes().FirstOrDefault(n => n.IsLink ? n.Id == id : n.FolderId == id);
        return node != null ? new DragItem(id, !node.IsLink, node.Name) : null;
    }

    /// <summary>
    /// 主栏行拖拽起点：未选中 → 先单选该行（Explorer 口径：拖未选中项先选中）；已选中 → 拖动整个选中集合。
    /// 返回本次拖动的载荷快照（视图据此调 DoDragDrop），抓住的行在首位。
    /// </summary>
    public IReadOnlyList<DragItem> PrepareDragFromRow(BrowserRowViewModel? row)
    {
        if (row == null) return [];
        if (!row.IsSelected) SelectRowWithModifiers(row, ModifierKeys.None);
        return BuildDragItems(row.Id);
    }

    /// <summary>
    /// 树节点拖拽起点：语义与主栏**完全一致**（未选中 → 先单选该节点；已选中 → 拖动整个选中集合）。
    /// 实体 ID：文件夹 = <c>FolderId</c>、链接叶子 = <c>Id</c>；「全部书签」虚根不是实体 → 空载荷（不可拖）。
    /// 选中仍只经 <see cref="SetSelection"/>（唯一写入入口）落盘，不在此旁路写节点状态。
    /// </summary>
    public IReadOnlyList<DragItem> PrepareDragFromNode(FolderNode? node)
    {
        if (node == null) return [];
        var id = node.IsLink ? node.Id : node.FolderId;
        if (string.IsNullOrEmpty(id)) return [];
        if (!Selection.Contains(id)) Selection.SelectSingle(id);
        return BuildDragItems(id);
    }

    // 拖拽落点入口见 `BrowserViewModel.Transfer.cs`：`DropItemsAsync(items, target, mode)`。
    // 拖拽与剪贴板粘贴共用同一条传输流水线（`TransferAsync`），此处不再有第二份逐项循环。

}

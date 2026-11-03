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

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：撤销 / 重做（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    // —— 撤销 / 重做（Ctrl+Z / Ctrl+Y）——
    // 可用性口径 = 引擎两个栈的**真实状态**（undo.list / undo.list_redo），不再靠本地猜测：
    // 新写操作会清空重做栈（标准 redo 语义），本地事实会在那时失真。

    private bool _canUndo;
    private bool _canRedo;
    /// <summary>可撤销（引擎撤销栈非空）。</summary>
    public bool CanUndo => _canUndo && !IsPathEditing;
    /// <summary>可重做（引擎重做栈非空）。</summary>
    public bool CanRedo => _canRedo && !IsPathEditing;

    private async Task UndoRedoAsync(bool redo)
    {
        try
        {
            var result = redo ? await _client.RedoAsync() : await _client.UndoAsync();
            // 撤销/重做成功后：回到受影响实体所在位置并选中它（Windows 资源管理器口径）——
            // 实体 ID 从变更集的 Touched 取（引擎已把逆向命令的受影响实体聚合上来），
            // 复用既有「跳转」语义（进目录 + 选中该行 + 滚入视口），不另造一套导航。
            await RefreshUndoStateAsync();
            var needsRefresh = await LocateAfterUndoAsync(result.Changes);
            // 立即把结果反映到列表：跨目录定位时上面已经加载过一次；同目录（或目标已不存在）时
            // 事件防抖要等 300ms，这里补一次原地刷新，Ctrl+Z 才"按下去就见效"。
            if (needsRefresh) await RefreshPreservingSelectionAsync();
        }
        catch (Exception)
        {
            ShowError(redo ? Loc.T("status.redoFailed") : Loc.T("status.undoFailed"), Loc.T("err.unexpected"));
        }
    }

    /// <summary>
    /// 撤销/重做后定位到受影响实体：取变更集里第一个链接/文件夹，进其所在目录并选中该行。
    /// 无受影响实体（或引擎未回报）时什么都不做——绝不猜测位置。
    /// 返回是否需要调用方补一次原地刷新（目标目录已加载过 = 不需要；同目录未加载 / 目标已不存在 = 需要）。
    /// </summary>
    private async Task<bool> LocateAfterUndoAsync(LinkPocket.Contracts.ChangeSet? changes)
    {
        var touched = changes?.Touched;
        if (touched == null || touched.Count == 0) return true;

        foreach (var entity in touched)
        {
            if (entity.Type == "link")
            {
                LinkDto link;
                try
                {
                    link = await _client.LinkGetAsync(entity.Id);
                }
                catch (EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
                {
                    continue;   // 已被撤销掉（如撤销"新建链接"）→ 试下一个
                }
                var navigates = Controller.CurrentFolderId != link.ListId;
                await NavigateAndSelectAsync(link.ListId, entity.Id);
                return !navigates;   // 跨目录时 Navigate 已加载；同目录只需原地刷新
            }
            if (entity.Type == "folder")
            {
                FolderDto folder;
                try
                {
                    folder = await _client.FolderGetAsync(entity.Id);   // 单行查询，不拉整棵树
                }
                catch (EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
                {
                    continue;   // 已进回收站（撤销"新建文件夹"）→ 试下一个
                }
                var navigates = Controller.CurrentFolderId != folder.ParentId;
                await NavigateAndSelectAsync(folder.ParentId, entity.Id);
                return !navigates;
            }
        }
        return true;   // 没有可定位的实体：仍需让撤销结果（行消失/字段变化）立刻可见
    }

    /// <summary>
    /// 同步撤销/重做可用性 = 引擎两个栈的**真实状态**（undo.list / undo.list_redo，
    /// 每次刷新链收尾与撤销/重做后各取一次）。查询失败时保持保守禁用（观测面纪律：不弹窗打断输入）。
    /// </summary>
    public async Task RefreshUndoStateAsync()
    {
        // 观测定痕（cat=app.nav）：刷新链收尾处的**两条引擎读**（列表 + 重做列表）也落在 UI 线程续体上，
        // 是"导航后那块阻塞"的并列嫌疑（见 favicon:backfill 的同源说明；只记耗时，不改行为）。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ⚠️ 两个查询的返回都是**对象** `{ "entries": [...] }`（不是裸数组）——按数组解析会恒为空
            //（曾据此误判"无可撤销"，Ctrl+Z 永远灰着）。
            _canUndo = await HasEntriesAsync(await _client.UndoListAsync());
            _canRedo = await HasEntriesAsync(await _client.UndoListRedoAsync());
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            var cmdWatch = System.Diagnostics.Stopwatch.StartNew();
            CommandRefresh.Request();
            LinkPocket.Contracts.LpLog.Write(LinkPocket.Contracts.LogLevel.Info, NavLogCategory,
                "cmdrefresh:requery(undo-state)", elapsedMs: cmdWatch.ElapsedMilliseconds);
        }
        catch { /* 查询失败不阻断；CanExecute 保守禁用 */ }
        finally
        {
            LinkPocket.Contracts.LpLog.Write(LinkPocket.Contracts.LogLevel.Info, NavLogCategory,
                $"undo-state:queried canUndo={_canUndo} canRedo={_canRedo}", elapsedMs: watch.ElapsedMilliseconds);
        }
    }

    private static Task<bool> HasEntriesAsync(System.Text.Json.JsonElement list)
        => Task.FromResult(list.ValueKind is System.Text.Json.JsonValueKind.Object
                           && list.TryGetProperty("entries", out var entries)
                           && entries.ValueKind is System.Text.Json.JsonValueKind.Array
                           && entries.GetArrayLength() > 0);

}

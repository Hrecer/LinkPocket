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
            await LocateAfterUndoAsync(result.Changes);
        }
        catch (Exception ex)
        {
            ShowError(redo ? Loc.T("status.redoFailed") : Loc.T("status.undoFailed"), ex.Message);
        }
    }

    /// <summary>
    /// 撤销/重做后定位到受影响实体：取变更集里第一个链接/文件夹，进其所在目录并选中该行。
    /// 无受影响实体（或引擎未回报）时什么都不做——绝不猜测位置。
    /// </summary>
    private async Task LocateAfterUndoAsync(LinkPocket.Contracts.ChangeSet? changes)
    {
        var touched = changes?.Touched;
        if (touched == null || touched.Count == 0) return;

        foreach (var entity in touched)
        {
            if (entity.Type == "link")
            {
                var link = await _client.LinkGetAsync(entity.Id);
                if (link == null) continue;   // 已被撤销掉（如撤销"新建链接"）→ 试下一个
                await NavigateAndSelectAsync(link.ListId, entity.Id);
                return;
            }
            if (entity.Type == "folder")
            {
                var tree = await _client.FolderTreeAsync();
                var folder = tree.FirstOrDefault(f => f.FolderId == entity.Id);
                if (folder == null) continue;   // 已进回收站（撤销"新建文件夹"）→ 试下一个
                await NavigateAndSelectAsync(folder.ParentId, entity.Id);
                return;
            }
        }
    }

    /// <summary>
    /// 同步撤销/重做可用性 = 引擎两个栈的**真实状态**（undo.list / undo.list_redo，
    /// 每次刷新链收尾与撤销/重做后各取一次）。查询失败时保持保守禁用（观测面纪律：不弹窗打断输入）。
    /// </summary>
    public async Task RefreshUndoStateAsync()
    {
        try
        {
            // ⚠️ 两个查询的返回都是**对象** `{ "entries": [...] }`（不是裸数组）——按数组解析会恒为空
            //（曾据此误判"无可撤销"，Ctrl+Z 永远灰着）。
            _canUndo = await HasEntriesAsync(await _client.UndoListAsync());
            _canRedo = await HasEntriesAsync(await _client.UndoListRedoAsync());
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            CommandRefresh.Request();
        }
        catch { /* 查询失败不阻断；CanExecute 保守禁用 */ }
    }

    private static Task<bool> HasEntriesAsync(System.Text.Json.JsonElement list)
        => Task.FromResult(list.ValueKind is System.Text.Json.JsonValueKind.Object
                           && list.TryGetProperty("entries", out var entries)
                           && entries.ValueKind is System.Text.Json.JsonValueKind.Array
                           && entries.GetArrayLength() > 0);

}
using System;
using System.Collections.Generic;
using System.Linq;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页**剪贴板语义（控制器）**：从 BrowserViewModel 抽出的剪切 / 复制 / 取消剪切状态机。
/// 载荷的**存储**在 <see cref="Managers.ClipboardManager"/>（应用级）；传输（粘贴）走共享流水线
/// `BrowserViewModel.TransferAsync`——本控制器只负责"构造载荷 / 剪切态视觉 / 取消 / 查询"，
/// 不碰逐项搬运（单一传输实现，铁律 10）。
///
/// 遗忘时机（Windows 口径）：Esc 取消剪切 / 被新的复制剪切覆盖 / **真有项被粘贴**（由流水线消费）
/// / 关闭应用；换目录与刷新都不遗忘。
/// </summary>
public sealed class BrowserClipboardController
{
    private readonly Managers.ClipboardManager _clipboard;
    private readonly Func<IReadOnlyList<DragItem>> _buildItems;
    private readonly Func<string?> _sourceFolderId;
    private readonly Action<IReadOnlyList<DragItem>?> _applyCutVisual;
    private readonly Action<string> _setStatus;

    /// <param name="clipboard">应用级剪贴板（载荷存储）。</param>
    /// <param name="buildItems">拖动集合的唯一出口（与拖拽共用，树里选中而主栏不可见的项同样可复制/剪切）。</param>
    /// <param name="sourceFolderId">来源目录 ID（写入载荷，供"粘回源目录 = 无操作"判定）。</param>
    /// <param name="applyCutVisual">剪切半透明视觉投影（传 null = 全清）。</param>
    /// <param name="setStatus">状态栏反馈。</param>
    public BrowserClipboardController(Managers.ClipboardManager clipboard,
        Func<IReadOnlyList<DragItem>> buildItems,
        Func<string?> sourceFolderId,
        Action<IReadOnlyList<DragItem>?> applyCutVisual,
        Action<string> setStatus)
    {
        _clipboard = clipboard;
        _buildItems = buildItems;
        _sourceFolderId = sourceFolderId;
        _applyCutVisual = applyCutVisual;
        _setStatus = setStatus;
    }

    /// <summary>是否有待粘贴的剪切载荷（Esc 分层与"取消剪切"可用性的唯一判据）。</summary>
    public bool HasCutPayload => _clipboard.BrowserPayload is { IsCut: true, IsEmpty: false };

    /// <summary>刷新重建行后，按剪贴板载荷恢复剪切半透明视觉（仅剪切语义）。</summary>
    public bool IsCutInClipboard(string id, bool isFolder)
    {
        var p = _clipboard.BrowserPayload;
        return p is { IsCut: true } && (isFolder ? p.FolderIds.Contains(id) : p.LinkIds.Contains(id));
    }

    public void Cut()
    {
        var items = _buildItems();
        if (items.Count == 0) return;
        _clipboard.SetBrowserPayload(BuildPayload(items, isCut: true));
        _applyCutVisual(items);
        _setStatus($"已剪切 {items.Count} 项（Ctrl+V 粘贴到目标文件夹）");
    }

    public void Copy()
    {
        var items = _buildItems();
        if (items.Count == 0) return;
        _clipboard.SetBrowserPayload(BuildPayload(items, isCut: false));
        _applyCutVisual(null);   // 复制覆盖剪切，清除半透明视觉
        _setStatus($"已复制 {items.Count} 项");
    }

    /// <summary>取消剪切：清空剪贴板载荷（复制载荷不受影响——Windows 里 Esc 只取消剪切），复位行半透明视觉。</summary>
    public void Cancel()
    {
        _clipboard.SetBrowserPayload(null);
        _applyCutVisual(null);
        _setStatus("已取消剪切");
    }

    private Managers.BrowserClipboardPayload BuildPayload(IReadOnlyList<DragItem> items, bool isCut) => new()
    {
        FolderIds = items.Where(i => i.IsFolder).Select(i => i.Id).ToList(),
        LinkIds = items.Where(i => !i.IsFolder).Select(i => i.Id).ToList(),
        SourceFolderId = _sourceFolderId(),
        IsCut = isCut
    };
}

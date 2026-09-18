using System;
using System.Collections.Generic;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站详情栏模型（<see cref="Views.DetailSidebar"/> 的数据源）：
/// 只读 —— 展示类型 / 原位置 / 删除时间 / ID（链接附网址卡与 favicon），IsReadOnly = true
/// 隐藏「快捷操作」卡（不提供打开 / 编辑 / 删除入口；永久删除仍由页面工具栏负责）。
/// 无还原功能，亦不在详情栏加入任何还原入口。
/// </summary>
public class TrashSidebarModel : DetailSidebarModel
{
    private RelayCommand? _copyCommand;

    /// <summary>按选中的回收站条目填充详情（平铺条目；folder = 被删单元根）。</summary>
    public void Show(TrashEntryDto entry)
    {
        var isFolder = entry.EntryType == LinkPocket.Contracts.TrashEntryType.Folder;

        HasSelection = true;
        IsMulti = false;
        IsFolder = isFolder;
        IsReadOnly = true;
        DisplayName = string.IsNullOrEmpty(entry.Name) ? (entry.Url ?? "（无名称）") : entry.Name;
        IdText = entry.Id;
        UrlText = entry.Url ?? string.Empty;
        DescriptionText = string.Empty;
        Favicon = isFolder ? null : FaviconService.LoadFromCache(entry.FaviconUrl);

        // 复制命令：复用公共 RelayCommand（CanExecuteChanged 走 CommandManager）；
        // 空 URL 时禁点（2.3-17：绝不 Clipboard.SetText("") 覆盖用户剪贴板）
        _copyCommand ??= new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(UrlText); } catch { /* 剪贴板被占用时不阻断 */ }
        }, () => !string.IsNullOrEmpty(UrlText));

        // 行序：类型 / 原位置 / [网址(仅链接带网址)] / 删除时间 / ID（2.3-18：按序追加，不再用 Insert 魔法位）
        var rows = new List<DetailSidebarRow>
        {
            new()
            {
                IconKind = isFolder ? LinkPocket.Contracts.TrashEntryType.Folder : "link-variant",
                Label = "类型",
                Value = isFolder ? "文件夹单元（含子树）" : "书签",
                IsAccent = true
            },
            new()
            {
                IconKind = "folder-outline",
                Label = "原位置",
                Value = string.IsNullOrWhiteSpace(entry.OriginPath) ? "全部书签" : entry.OriginPath
            },
        };
        if (!isFolder && !string.IsNullOrWhiteSpace(UrlText))
        {
            rows.Add(new DetailSidebarRow
            {
                IconKind = "link-variant",
                Label = "网址",
                Value = UrlText,
                CopyCommand = _copyCommand,
                CopyToolTip = "复制网址"
            });
        }
        rows.Add(new DetailSidebarRow
        {
            IconKind = "history",
            Label = "删除时间",
            Value = entry.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        });
        rows.Add(new DetailSidebarRow
        {
            IconKind = "fingerprint",
            Label = "ID",
            Value = entry.Id,
            IsMono = true,
            CopyCommand = _copyCommand,
            CopyToolTip = "复制 ID"
        });

        SetRows(rows);
        RaiseAll();
    }
}

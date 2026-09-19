using System;
using System.Collections.Generic;
using System.Windows.Input;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站详情栏模型（<see cref="Views.DetailSidebar"/> 的数据源）：
/// 只读 —— 展示 类型 / 原位置 / 删除时间 / ID（链接附网址卡与 favicon），IsReadOnly = true
/// 隐藏「快捷操作」卡（不提供打开 / 编辑 / 删除入口；永久删除仍由页面工具栏与右键菜单负责）。
/// 无还原功能，亦不在详情栏加入任何还原入口（用户定稿）。
/// </summary>
public class TrashSidebarModel : DetailSidebarModel
{
    private RelayCommand? _copyCommand;

    /// <summary>按选中的回收站行填充详情（行 = 单元或书签快照）。</summary>
    public void Show(TrashRowViewModel row)
    {
        HasSelection = true;
        IsMulti = false;
        IsFolder = row.IsFolder;
        IsReadOnly = true;
        DisplayName = string.IsNullOrEmpty(row.Name) ? "（无名称）" : row.Name;
        IdText = row.Id;
        UrlText = row.Url ?? string.Empty;
        DescriptionText = string.Empty;
        Favicon = row.IsFolder ? null : FaviconService.LoadFromCache(row.FaviconUrl);

        // 复制命令：复用公共 RelayCommand（CanExecuteChanged 走 CommandManager）；
        // 空 URL 时禁点（绝不 Clipboard.SetText("") 覆盖用户剪贴板）
        _copyCommand ??= new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(UrlText); } catch { /* 剪贴板被占用时不阻断 */ }
        }, () => !string.IsNullOrEmpty(UrlText));

        var rows = new List<DetailSidebarRow>
        {
            new()
            {
                IconKind = row.IsFolder ? "folder" : "link-variant",
                Label = "类型",
                Value = row.IsFolder ? "文件夹单元（含子树）" : "书签",
                IsAccent = true
            },
            new()
            {
                IconKind = "folder-outline",
                Label = "原位置",
                Value = row.OriginText
            },
        };
        if (!row.IsFolder && !string.IsNullOrWhiteSpace(UrlText))
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
            Value = row.DeletedText
        });
        rows.Add(new DetailSidebarRow
        {
            IconKind = "fingerprint",
            Label = "ID",
            Value = row.Id,
            IsMono = true,
            CopyCommand = _copyCommand,
            CopyToolTip = "复制 ID"
        });

        SetRows(rows);
        RaiseAll();
    }

    /// <summary>多选态：只报项数（批量可用动作 = 永久删除，由工具栏/Delete 键承载）。</summary>
    public void ShowMulti(int count)
    {
        HasSelection = true;
        IsMulti = true;
        IsFolder = false;
        IsReadOnly = true;
        DisplayName = $"已选中 {count} 项";
        IdText = string.Empty;
        UrlText = string.Empty;
        DescriptionText = string.Empty;
        Favicon = null;
        SetRows(Array.Empty<DetailSidebarRow>());
        RaiseAll();
    }
}

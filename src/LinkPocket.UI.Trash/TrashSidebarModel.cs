using System;
using System.Collections.Generic;
using System.Windows.Input;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站详情栏模型（<see cref="Views.DetailSidebar"/> 的数据源；由 <see cref="TrashViewModel.Details"/> 持有，
/// 在选中投影点按选中项数重建 —— 视图只绑定，不另持一份状态）：
/// 只读 —— 展示 类型 / 原位置 / 描述 / 删除时间 / ID（链接附网址卡与 favicon），IsReadOnly = true。
/// 「快捷操作」卡**在共享框架内定制动作面**（不是另写一套界面）：只开 打开/详情 + 永久删除，
/// 关 编辑/重命名 与 打开网站；命令复用本页既有能力（<c>OpenSelectionCommand</c> / <c>PurgeSelectionCommand</c>）。
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
        // 动作面：打开/详情 + 永久删除（无 编辑/重命名、无 打开网站）
        ShowOpenAction = true;
        ShowOpenWebsite = false;
        ShowEditAction = false;
        ShowDeleteAction = true;
        DeleteActionLabel = "永久删除";
        ConfigureSidebarActionLabels(row.IsFolder);   // 主按钮文案：单元=打开（进入）/ 链接=详情（只读覆盖层）
        DisplayName = string.IsNullOrEmpty(row.Name) ? "（无名称）" : row.Name;
        IdText = row.Id;
        UrlText = row.Url ?? string.Empty;
        DescriptionText = row.Description ?? string.Empty;   // 描述快照（链接与单元通用）
        Favicon = row.IsFolder ? null : FaviconService.LoadFromCache(row.FaviconUrl);

        // 复制命令：复用公共 RelayCommand（CanExecuteChanged 走 CommandManager）；
        // 空 URL 时禁点（绝不 Clipboard.SetText("") 覆盖用户剪贴板）
        _copyCommand ??= new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(UrlText); } catch { /* 剪贴板被占用时不阻断 */ }
        }, () => !string.IsNullOrEmpty(UrlText));
        CopyUrlCommand = _copyCommand;   // 网址卡的复制按钮（与行内「复制网址」同一条命令）

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

    /// <summary>多选态：只报项数与类型分布（批量可用动作 = 永久删除，由工具栏/Delete 键承载）。</summary>
    public void ShowMulti(IReadOnlyList<TrashRowViewModel> rows)
    {
        HasSelection = true;
        IsMulti = true;
        IsFolder = false;
        IsReadOnly = true;
        // 多选：只提供永久删除（与浏览页同款按钮/样式，文案按页定制）
        ShowOpenAction = false;
        ShowOpenWebsite = false;
        ShowEditAction = false;
        ShowDeleteAction = true;
        DeleteSelectionLabel = "永久删除所选";
        DisplayName = $"已选中 {rows.Count} 项";
        IdText = string.Empty;
        UrlText = string.Empty;
        DescriptionText = string.Empty;
        Favicon = null;
        SelectedTotal = rows.Count;
        SelectedFolders = rows.Count(r => r.IsFolder);
        SelectedLinks = SelectedTotal - SelectedFolders;
        SetRows(Array.Empty<DetailSidebarRow>());
        RaiseAll();
    }
}

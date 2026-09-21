using System;
using System.Collections.Generic;
using System.Windows.Input;
using LinkPocket.Services;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站详情栏模型（<see cref="Views.DetailSidebar"/> 的数据源；由 <see cref="TrashViewModel.Details"/> 持有，
/// 在选中投影点按选中项数重建 —— 视图只绑定，不另持一份状态）：
/// 只读 —— 展示 类型 / 原位置 / 描述 / 删除时间 / ID（链接附网址卡与 favicon），IsReadOnly = true。
/// 「快捷操作」卡**在共享框架内定制动作面**（不是另写一套界面）：两枚药丸 = 详情 / 打开（打开网站），
/// 右侧三枚图标钮 = 还原 / 还原到根目录 / 永久删除（沿用现有，顺序与工具栏一致）；
/// 命令复用本页既有能力（<c>OpenSelectionCommand</c> / <c>RestoreSelectionCommand</c> /
/// <c>RestoreSelectionToRootCommand</c> / <c>PurgeSelectionCommand</c>）。
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
        UseTrashedIconTone = true;   // 被删快照：右栏大图标与主栏/左栏同口径灰化（缺此设置则右栏图标不灰化）
        // 动作面：详情/打开 + 打开网站 + 还原/还原到根目录/永久删除（无编辑/重命名）
        ShowOpenAction = true;
        ShowOpenWebsite = true;                       // 「打开」药丸（打开网站；即使是废弃条目也能打开）
        ShowEditAction = false;
        ShowDeleteAction = true;
        DeleteActionLabel = Loc.K("trash.menu.purge");
        ShowRestoreAction = true;
        RestoreTone = PillTone.Primary;               // 还原 = 深紫（与工具栏主按钮同一色系）
        ShowRestoreToRootAction = true;
        RestoreToRootTone = PillTone.Tonal;           // 还原到根目录 = 浅紫
        StackedActions = true;                        // 两行排布：药丸一行 / 三枚图标钮一行（286 宽同排会裁字）
        // 「跳转」= **本页内定位到选中行**（把视角移回它）：回收站条目不在主表，`locate.resolve` 查不到它，
        // 所以本页的跳转就是"滚回那一行"（与浏览页跳转的落点效果一致，复用同一个视图原语）。
        // 一页上百项时，选中的行滑出视口后可一键回到它。仅单选（多选无"某一项"）。
        ShowJumpAction = true;
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
                LabelKey = "ui.noun.type",
                Value = row.IsFolder ? "文件夹单元（含子树）" : "书签",
                IsAccent = true
            },
            new()
            {
                IconKind = "folder-outline",
                LabelKey = "ui.noun.origin",
                Value = row.OriginText
            },
        };
        if (!row.IsFolder && !string.IsNullOrWhiteSpace(UrlText))
        {
            rows.Add(new DetailSidebarRow
            {
                IconKind = "link-variant",
                LabelKey = "ui.noun.url",
                Value = UrlText,
                CopyCommand = _copyCommand,
                CopyToolTip = "复制网址"
            });
        }
        rows.Add(new DetailSidebarRow
        {
            IconKind = "history",
            LabelKey = "ui.noun.deletedAt",
            Value = row.DeletedText
        });
        rows.Add(new DetailSidebarRow
        {
            IconKind = "fingerprint",
            LabelKey = "ui.noun.id",
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
        UseTrashedIconTone = true;   // 与单选同口径：回收站右栏一律灰化
        // 多选：只提供永久删除（与浏览页同款按钮/样式，文案按页定制）
        ShowOpenAction = false;
        ShowOpenWebsite = false;
        ShowEditAction = false;
        ShowDeleteAction = true;
        ShowRestoreAction = false;
        ShowRestoreToRootAction = false;
        ShowJumpAction = false;   // 多选没有"某一项"可定位（与其它页同一口径）
        DeleteSelectionLabel = Loc.K("trash.purgeSelection");
        DisplayName = Loc.T("count.selectedItems", rows.Count);
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

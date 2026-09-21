using System.Windows.Input;
using LinkPocket.Services;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站只读详情页模型（共享 <see cref="LinkDetailPaneModel"/> 的定制）：
/// 与浏览页详情页**同一份界面**（<c>Views.LinkDetailPane</c>）；本页只声明动作面 = 三枚**等大药丸**
/// （打开网站 / 还原到原位置 / 还原到根目录）+ 永久删除图标钮，行内容 = 回收站快照
/// （原位置 / 删除时间 / ID），描述 = 删除时的描述快照。
/// 命令由 <see cref="TrashViewModel"/> 注入，且**作用对象 = 本覆盖层正在展示的那一项**
/// （不依赖"有没有被选中"，也不受覆盖层门禁影响——见 TrashViewModel 的 Detail* 命令）。
/// </summary>
public class TrashDetailPaneModel : LinkDetailPaneModel
{
    /// <summary>ID 行复制（快照 ID；由宿主注入）。</summary>
    public ICommand? CopyIdCommand { get; set; }

    /// <summary>按选中的回收站行填充（只读：数据全部来自快照，不查主表）。</summary>
    public void Show(TrashRowViewModel row)
    {
        // 动作面：三枚等大药丸（打开网站 / 还原 / 还原到根目录）+ 永久删除（无编辑 / 无重命名）
        ShowOpenAction = true;
        OpenLabel = Loc.K("common.open");
        OpenToolTip = Loc.K("detail.openInBrowser");
        OpenIconKind = "open-in-new";
        OpenTone = PillTone.Tonal;                 // 主处置是「还原」，打开降为次要色
        ShowRestoreAction = true;
        RestoreTone = PillTone.Primary;
        ShowRestoreToRootAction = true;
        RestoreToRootTone = PillTone.Tonal;
        ShowEditAction = false;
        ShowDeleteAction = true;
        DeleteActionLabel = Loc.K("trash.menu.purge");

        SetContent(
            row.Name ?? "",
            string.IsNullOrEmpty(row.Name) ? Loc.K("trash.unnamed") : LocValue.Empty,
            row.Url ?? string.Empty,
            row.IsFolder ? null : FaviconService.LoadFromCache(row.FaviconUrl),
            row.Description ?? string.Empty,
            new[]
            {
                new DetailSidebarRow { IconKind = "folder-outline", LabelKey = "ui.noun.origin", ValueCopy = row.OriginText },
                new DetailSidebarRow { IconKind = "history", LabelKey = "ui.noun.deletedAt", ValueData = row.DeletedText.Resolve() },
                new DetailSidebarRow
                {
                    IconKind = "fingerprint", LabelKey = "ui.noun.id", ValueData = row.Id, IsMono = true,
                    CopyCommand = CopyIdCommand, CopyToolTip = Loc.K("common.copyId")
                },
            });
    }
}

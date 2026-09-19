using System.Windows.Input;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站只读详情页模型（共享 <see cref="LinkDetailPaneModel"/> 的定制）：
/// 与浏览页详情页**同一份界面**（<c>Views.LinkDetailPane</c>）；本页只声明动作面 = 「还原」+「永久删除」，
/// 行内容 = 回收站快照（原位置 / 删除时间 / ID），描述 = 删除时的描述快照。
/// 命令由 <see cref="TrashViewModel"/> 注入（复用本页既有能力，绝不另写逻辑）。
/// </summary>
public class TrashDetailPaneModel : LinkDetailPaneModel
{
    /// <summary>ID 行复制（快照 ID；由宿主注入）。</summary>
    public ICommand? CopyIdCommand { get; set; }

    /// <summary>按选中的回收站行填充（只读：数据全部来自快照，不查主表）。</summary>
    public void Show(TrashRowViewModel row)
    {
        // 动作面：还原（主按钮）+ 永久删除（无编辑 / 无打开网站）
        ShowOpenAction = true;
        ShowEditAction = false;
        ShowDeleteAction = true;
        OpenLabel = "还原";
        OpenToolTip = "还原到删除前所在位置";
        OpenIconKind = "restore";
        DeleteActionLabel = "永久删除";

        SetContent(
            string.IsNullOrEmpty(row.Name) ? "（无名称）" : row.Name,
            row.Url ?? string.Empty,
            row.IsFolder ? null : FaviconService.LoadFromCache(row.FaviconUrl),
            row.Description ?? string.Empty,
            new[]
            {
                new DetailSidebarRow { IconKind = "folder-outline", Label = "原位置", Value = row.OriginText },
                new DetailSidebarRow { IconKind = "history", Label = "删除时间", Value = row.DeletedText },
                new DetailSidebarRow
                {
                    IconKind = "fingerprint", Label = "ID", Value = row.Id, IsMono = true,
                    CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID"
                },
            });
    }
}

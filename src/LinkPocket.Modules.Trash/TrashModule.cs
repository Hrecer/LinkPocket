using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// 回收站模块入口：平铺/树/单元内容/页面快照/还原/永久删除。
/// 口径铁律：平铺 = 单独删除书签 + 单元根（按删除时间倒序）；还原缺省回删除前位置（origin）；
/// **站内没有任何改变归属/层级的能力**（搬移系统已整体移除——条目只能被打开查看与两路处置）。
/// </summary>
public static class TrashModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new TrashListHandler(),
        new TrashTreeHandler(),
        new TrashUnitContentsHandler(),
        new TrashOverviewHandler(),
        new TrashRestoreHandler(),
        new TrashRestoreUnitHandler(),
        new TrashRestoreBatchHandler(),
        new TrashPurgeHandler(),
        new TrashPurgeBatchHandler(),
    ];
}

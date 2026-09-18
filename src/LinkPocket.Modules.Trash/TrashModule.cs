using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// 回收站模块入口：平铺/树/单元内容/还原/永久删除。
/// 口径铁律：平铺 = 单独删除书签 + 单元根（按删除时间倒序）；还原固定落根（行为等价项）。
/// </summary>
public static class TrashModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new TrashListHandler(),
        new TrashTreeHandler(),
        new TrashUnitContentsHandler(),
        new TrashRestoreHandler(),
        new TrashRestoreBatchHandler(),
        new TrashPurgeHandler(),
        new TrashPurgeBatchHandler(),
    ];
}

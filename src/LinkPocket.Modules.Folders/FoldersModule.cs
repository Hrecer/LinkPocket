using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// 文件夹域模块入口（方案 4.2）：对外唯一动作 = 注册命令处理器。
/// 模块内部类型一律 internal（黑盒封装，方案 2.2）；跨模块只经嵌套命令或事件通信。
/// </summary>
public static class FoldersModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new FolderContentsHandler(),
        new FolderTreeHandler(),
        new FolderGetHandler(),
        new FolderBreadcrumbHandler(),
        new FolderCycleCheckHandler(),
        new FolderFindHandler(),
        new FolderCreateHandler(),
        new FolderUpdateHandler(),
        new FolderDeleteHandler(),
        new FolderMoveHandler(),
        new FolderMoveBatchHandler(),
        new FolderCopyHandler(),
        new FolderSortHandler(),
    ];
}

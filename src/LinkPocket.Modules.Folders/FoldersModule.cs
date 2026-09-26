using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// 文件夹域模块入口：对外唯一动作 = 注册命令处理器。
/// 模块内部类型一律 internal（黑盒封装）；跨模块只经嵌套命令或事件通信。
/// </summary>
public static class FoldersModule
{
    /// <param name="limits">引擎资源上限（组合根可配置；缺省 = <see cref="EngineLimits.Default"/>）。</param>
    public static IReadOnlyList<ICommandHandler> CreateHandlers(EngineLimits? limits = null) =>
    [ 
        new FolderContentsHandler(limits ?? EngineLimits.Default),
        new FolderOverviewHandler(limits ?? EngineLimits.Default),
        new FolderTreeHandler(),
        new FolderTreeLinksHandler(limits ?? EngineLimits.Default),
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

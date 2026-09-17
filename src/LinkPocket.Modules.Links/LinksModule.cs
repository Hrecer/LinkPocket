using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 链接域模块入口（方案 4.2）：对外唯一动作 = 注册命令处理器。
/// 模块内部类型一律 internal（黑盒封装）；跨模块只经嵌套命令或事件通信。
/// </summary>
public static class LinksModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new LinkListHandler(),
        new LinkGetHandler(),
        new LinkRootsHandler(),
        new LinkStatsHandler(),
        new LinkSmartListHandler(),
        new LinkMetadataFetchHandler(),
        new LinkQueryHandler(),
        new LinkFindByUrlHandler(),
        new LinkCreateHandler(),
        new LinkUpdateHandler(),
        new LinkTrashHandler(),
        new LinkVisitRecordHandler(),
        new LinkMoveBatchHandler(),
        new LinkCopyBatchHandler(),
        new LinkVisitBatchHandler(),
        new LinkExportHandler(),
    ];
}

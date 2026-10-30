using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 链接域模块入口：对外唯一动作 = 注册命令处理器。
/// 模块内部类型一律 internal（黑盒封装）；跨模块只经嵌套命令或事件通信。
/// </summary>
public static class LinksModule
{
    /// <summary>
    /// 注册命令处理器；另在装配时把元数据抓取的同款**浏览器指纹客户端**接到 favicon 下载兜底
    /// （反爬墙站点连图标文件也按客户端指纹拦——浏览器能看图、工具得靠指纹；见 WARNINGS 166）。
    /// </summary>
    public static IReadOnlyList<ICommandHandler> CreateHandlers()
    {
        FaviconCache.ImpersonatedDownloader = MetadataTransport.TryGetBytesImpersonatedAsync;
        return
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
}

using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Favicon;

/// <summary>图标缓存模块入口：磁盘缓存统计 + 后台预取队列（并发 4、去重）。</summary>
public static class FaviconModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new FaviconCacheStatsHandler(),
        new FaviconPrefetchHandler(),
    ];
}

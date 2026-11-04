using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>搜索模块入口：四范围组合搜索 + 命中字段解释。</summary>
public static class SearchModule
{
    /// <param name="limits">引擎资源上限（分页单页上限；缺省 = <see cref="EngineLimits.Default"/>）。</param>
    /// <param name="snapshotUowFactory">快照会话的 UoW 工厂；null = 不注册会话命令（不触库宿主）。</param>
    public static IReadOnlyList<ICommandHandler> CreateHandlers(
        EngineLimits? limits = null, Func<IUnitOfWork>? snapshotUowFactory = null)
    {
        var effectiveLimits = limits ?? EngineLimits.Default;
        var handlers = new List<ICommandHandler>
        {
            new SearchLinksHandler(),
            new SearchCountHandler(),
            new SearchExplainHandler(),
        };
        if (snapshotUowFactory is not null)
        {
            var store = new SearchSessionStore(snapshotUowFactory);
            handlers.Add(new SearchSessionOpenHandler(effectiveLimits, store));
            handlers.Add(new SearchSessionPageHandler(effectiveLimits, store));
            handlers.Add(new SearchSessionCloseHandler(store));
        }
        return handlers;
    }
}

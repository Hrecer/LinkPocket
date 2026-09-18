using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>搜索模块入口：四范围组合搜索 + 命中字段解释。</summary>
public static class SearchModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new SearchLinksHandler(),
        new SearchExplainHandler(),
    ];
}

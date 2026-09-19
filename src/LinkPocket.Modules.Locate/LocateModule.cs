using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Locate;

/// <summary>定位模块入口：ID → 位置解析（查询）。</summary>
public static class LocateModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new LocateResolveHandler(),
    ];
}

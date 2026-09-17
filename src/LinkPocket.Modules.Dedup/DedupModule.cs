using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>
/// 查重模块入口（方案 4.2/第五章）：复杂命令样板——
/// scan（分组扫描）→ plan（策略计划，纯干跑可审计）→ apply（嵌套派发 links.trash 执行，单审计条目）。
/// </summary>
public static class DedupModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new DedupScanHandler(),
        new DedupPlanHandler(),
        new DedupApplyHandler(),
    ];
}

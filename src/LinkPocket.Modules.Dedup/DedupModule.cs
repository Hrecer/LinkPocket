using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>
/// 查重模块入口（方案 4.2/第五章）：复杂命令样板——
/// scan（分组扫描）→ plan（策略计划，纯函数零副作用，查询流免审计）→ apply（嵌套派发 links.trash 执行，
/// 唯一写入口：单父审计条目 + 子记录，支持 DryRun 预演即执行但不提交、零事件）。
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
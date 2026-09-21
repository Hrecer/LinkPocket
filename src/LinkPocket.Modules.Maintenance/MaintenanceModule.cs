using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>维护模块入口：schema 版本 / 诊断收集 / 审计读侧与保留 / **日志读侧与运行期调级** / 整库重置（两阶段确认）。</summary>
public static class MaintenanceModule
{
    /// <param name="runtimeStats">
    /// 引擎运行时可观测读数提供方（查询缓存命中/失效计数 + 事件存储游标）。
    /// 由组合根接线（EngineRuntimeStats 在 Contracts——模块不引 LinkPocket.Engine 实现程序集）；<b>不接线时 diagnostics 的 runtime 段为 null</b>
    /// ——宁可暴露"未接线"，也不填假值（观测面纪律）。
    /// </param>
    public static IReadOnlyList<ICommandHandler> CreateHandlers(Func<EngineRuntimeStats>? runtimeStats = null) =>
    [
        new MaintenanceSchemaVersionHandler(),
        new DiagnosticsCollectHandler(runtimeStats),
        new AuditQueryHandler(),
        new AuditPruneHandler(),
        // 日志读侧：读侧入口由 LpLog 门面给出（日志不在库里，无需组合根接线）；
        // 未装配日志管道时两个命令一律报 LP.STATE.005，绝不返回空结果假装"没有日志"
        new LogsQueryHandler(),
        new LogsLevelHandler(),
        new MaintenanceReinitHandler(),
    ];
}

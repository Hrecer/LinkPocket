using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>维护模块入口（方案 4.2）：schema 版本 / 诊断收集 / 整库重置（两阶段确认）。</summary>
public static class MaintenanceModule
{
    /// <param name="runtimeStats">
    /// 引擎运行时可观测读数提供方（阶段 12：查询缓存命中/失效计数 + 事件存储游标）。
    /// 由组合根接线（它是引擎侧状态，模块不引引擎程序集）；<b>不接线时 diagnostics 的 runtime 段为 null</b>
    /// ——宁可暴露"未接线"，也不填假值（观测面纪律）。
    /// </param>
    public static IReadOnlyList<ICommandHandler> CreateHandlers(Func<EngineRuntimeStats>? runtimeStats = null) =>
    [
        new MaintenanceSchemaVersionHandler(),
        new DiagnosticsCollectHandler(runtimeStats),
        new MaintenanceReinitHandler(),
    ];
}

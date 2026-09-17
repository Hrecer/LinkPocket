using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>维护模块入口（方案 4.2）：schema 版本 / 诊断收集 / 整库重置（两阶段确认）。</summary>
public static class MaintenanceModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new MaintenanceSchemaVersionHandler(),
        new DiagnosticsCollectHandler(),
        new MaintenanceReinitHandler(),
    ];
}

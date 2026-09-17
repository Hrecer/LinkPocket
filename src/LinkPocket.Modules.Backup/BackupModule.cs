using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>备份模块入口（方案 4.2）：.lpbackup v2 导出/导入/只读检查。</summary>
public static class BackupModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new BackupExportHandler(),
        new BackupImportHandler(),
        new BackupInspectHandler(),
    ];
}

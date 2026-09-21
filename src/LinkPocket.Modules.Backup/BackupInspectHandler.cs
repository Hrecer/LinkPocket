using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>backup.inspect（★ 引擎能力）：只读检查备份包——manifest 信息 + 完整性校验，不写任何库数据。</summary>
internal sealed class BackupInspectHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "backup.inspect",
        Category: "backup",
        Description: "Read-only inspection of a .lpbackup: version / statistics / integrity result (pre-import assessment, zero side effects)",
        Parameters: [ParamSpec.Req<string>("file_path", "Backup file path")],
        Caps: CommandCaps.Query | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var file = await BackupIO.ReadAsync(filePath, ctx.Ct);

        return CommandResult.Ok(JsonSerializer.SerializeToElement(new
        {
            valid = file.Valid,
            errors = file.Errors,
            version = file.Manifest.Version,
            app_version = file.Manifest.AppVersion,
            created_at = file.Manifest.CreatedAt,
            data_sha256 = file.Manifest.DataSha256,
            total_folders = file.Manifest.Statistics.TotalFolders,
            total_links = file.Manifest.Statistics.TotalLinks,
            total_favicons = file.Manifest.Statistics.TotalFavicons,
        }));
    }
}

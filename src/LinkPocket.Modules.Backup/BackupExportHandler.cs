using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>backup.export（Mutation · FileIo）：全库导出为 .lpbackup（回收站不进备份——行为等价项）。</summary>
/// <remarks>
/// <b>不预删目标文件</b>：整包由 <see cref="BackupIO.PackAsync"/>
/// 写到**同目录临时文件**再原子替换目标——旧的"先 File.Delete 再打包"在打包失败/取消时会让用户
/// **同时丢掉旧备份与新备份**，是真实的数据丢失路径，已删除。
/// </remarks>
internal sealed class BackupExportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "backup.export",
        Category: "backup",
        Description: $"导出备份为 {BackupIO.FileExtension}（格式版本 {BackupIO.FormatVersion}；"
                     + "SHA-256 manifest + 临时 key 身份模型；同目录临时文件 + 原子替换；回收站内容不会被备份）",
        Parameters: [ParamSpec.Req<string>("output_path", "备份文件完整路径")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var outputPath = CommandArgs.RequireString(args, "output_path");
        var ct = ctx.Ct;

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"导出目录不存在：{directory}", correlationId: ctx.CorrelationId));

        var folders = await ctx.Uow.Folders.ListAllAsync(ct);
        var links = await ctx.Uow.Links.ListAsync(new LinkQuerySpec(), ct);

        try
        {
            await BackupIO.PackAsync(folders, links, fullPath, ct);
        }
        catch (EngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.FileIoError, $"备份文件打包失败：{ex.Message}", retryable: true));
        }

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                file_path = fullPath,
                format_version = BackupIO.FormatVersion,
                total_folders = folders.Count,
                total_links = links.Count,
                file_bytes = new FileInfo(fullPath).Length,
            }),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"已导出备份（{folders.Count} 个文件夹、{links.Count} 个书签）"));
    }
}

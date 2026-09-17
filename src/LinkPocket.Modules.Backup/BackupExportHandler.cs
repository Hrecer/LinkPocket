using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>backup.export（Mutation · FileIo）：全库导出为 .lpbackup（回收站不进备份——行为等价项）。</summary>
internal sealed class BackupExportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "backup.export",
        Category: "backup",
        Description: "导出备份为 .lpbackup v2（SHA-256 manifest + 临时 key 身份模型；回收站内容不会被备份）",
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

        // 撞已存在文件会抛 → 与既有口径一致：先删旧文件（回收站不进备份同属该流程的既定语义）
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.FileIoError, $"无法覆盖已存在的备份文件：{ex.Message}", retryable: true));
        }

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
                total_folders = folders.Count,
                total_links = links.Count,
                file_bytes = new FileInfo(fullPath).Length,
            }),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                "links.changed",
                $"已导出备份（{folders.Count} 个文件夹、{links.Count} 个书签）"));
    }
}

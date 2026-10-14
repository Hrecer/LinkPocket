using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.export（Mutation · FileIo）：回收站两表导出为 .lptrash（**保留实体原 ID**；图标缓存不随包携带）。
/// 更新流程的"临时搬运"通道之一（见 <c>UPDATE.md</c> §4.2 / §5.3）；用户也可手动用于归档。
/// </summary>
/// <remarks>
/// <b>不预删目标文件</b>：整包由 <see cref="TrashIO.PackAsync"/> 写到同目录临时文件再原子替换——
/// 打包失败/取消时用户的旧包不会丢。
/// </remarks>
internal sealed class TrashExportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.export",
        Category: "trash",
        Description: $"Export the recycle bin as {TrashIO.FileExtension} (format {TrashIO.FormatVersion};"
                     + "original ids and origin references are preserved; SHA-256 manifest; temp file in the same"
                     + " directory + atomic replace; favicon cache files are not included)",
        Parameters: [ParamSpec.Req<string>("output_path", "Export file absolute path")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation,
        Client: new("TrashExportAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var outputPath = CommandArgs.RequireString(args, "output_path");
        var ct = ctx.Ct;

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"export directory does not exist: {directory}", correlationId: ctx.CorrelationId));

        var units = await ctx.Uow.Trash.ListFoldersAsync(ct);
        var links = await ctx.Uow.Trash.ListAllLinksAsync(ct);

        // 干跑零副作用：不打包、不落盘（临时文件也算落盘）；如实预告影响面（计数 + 目标路径）
        if (ctx.DryRun)
            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    dry_run = true,
                    file_path = fullPath,
                    format_version = TrashIO.FormatVersion,
                    total_units = units.Count,
                    total_links = links.Count,
                }),
                ChangeSet.Of(
                    new EntityRef("file", fullPath),
                    LinkPocket.Contracts.DomainEventNames.TrashChanged,
                    $"Would export the recycle bin ({units.Count} units, {links.Count} bookmarks)"));

        try
        {
            await TrashIO.PackAsync(units, links, fullPath, ct);
        }
        catch (EngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.FileIoError, $"trash packaging failed: {ex.Message}", retryable: true));
        }

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                file_path = fullPath,
                format_version = TrashIO.FormatVersion,
                total_units = units.Count,
                total_links = links.Count,
                file_bytes = new FileInfo(fullPath).Length,
            }),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                LinkPocket.Contracts.DomainEventNames.TrashChanged,
                $"Recycle bin exported ({units.Count} units, {links.Count} bookmarks)"));
    }
}

using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>bookmarks.export（Mutation · FileIo）：全库导出为 Netscape 书签文件（UTF-8 无 BOM、CRLF）。</summary>
internal sealed class BookmarksExportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "bookmarks.export",
        Category: "bookmarks",
        Description: "Export all bookmarks as a Netscape bookmark file (file_path is the target absolute path; unowned bookmarks land at root level so no data is lost)",
        Parameters: [ParamSpec.Req<string>("file_path", "Export target absolute path")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var ct = ctx.Ct;

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"export directory does not exist: {directory}", correlationId: ctx.CorrelationId));

        var folders = await ctx.Uow.Folders.ListAllAsync(ct);
        var links = await ctx.Uow.Links.ListAsync(new LinkQuerySpec(), ct);

        var stats = new NetscapeWriter.WriteStats();
        var html = await NetscapeWriter.BuildHtmlAsync(folders, links, stats, ct);

        try
        {
            // UTF-8 无 BOM（与 Chrome 一致）
            await File.WriteAllTextAsync(fullPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.FileIoError, $"export file write failed: {ex.Message}", retryable: true));
        }

        var bytes = new FileInfo(fullPath).Length;
        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                file_path = fullPath,
                folders = stats.FoldersExported,
                links = stats.LinksExported + stats.RootLinksExported,
                root_links = stats.RootLinksExported,
                orphans = stats.OrphanLinksExported,
                file_bytes = bytes,
            }),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"Exported {stats.FoldersExported} folders and {stats.LinksExported + stats.RootLinksExported} bookmarks"));
    }
}

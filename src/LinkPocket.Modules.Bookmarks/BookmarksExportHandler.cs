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

        // **同目录临时文件 + 原子替换**（与备份导出同一口径）：直接写目标文件时，取消 / 磁盘满 /
        // 进程被杀会把用户已有的那份导出**截断成半截文件** —— 旧内容与新内容一起丢。
        // 绝不预删目标：临时文件与目标同卷，`File.Move(overwrite)` 才是原子的。
        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // UTF-8 无 BOM（与 Chrome 一致）
            await File.WriteAllTextAsync(tempPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemp(tempPath);
            throw;   // 取消原样上抛（引擎包成 LP.ENG.003），不许被说成"文件写失败"
        }
        catch (Exception ex)
        {
            TryDeleteTemp(tempPath);
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

    /// <summary>临时文件清理：尽力而为，绝不顶替原始异常（清理失败只留一个半截临时文件，不影响数据）。</summary>
    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 观测面纪律：清理失败不改变"导出失败"这个事实，异常已在上抛路径上 */ }
    }
}

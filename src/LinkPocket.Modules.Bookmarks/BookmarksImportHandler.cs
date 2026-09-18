using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>
/// bookmarks.import（Mutation · LongRunning）：从 Netscape 书签文件导入（追加，顶层条目落根级）。
/// 文件夹 ID 内存生成 → 一次性注册 → 引擎单事务提交；导入后全部文件夹 UpdatedAt 视为变动（既有口径）。
/// </summary>
internal sealed class BookmarksImportHandler : ICommandHandler
{
    // 数据库列长上限（schema 约束，超出截断——与既有导入器一致）
    private const int MaxFolderNameLength = 255;
    private const int MaxLinkTitleLength = 255;
    private const int MaxLinkUrlLength = 2048;
    private const int MaxLinkFaviconLength = 512;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "bookmarks.import",
        Category: "bookmarks",
        Description: "从 Netscape 书签文件导入全部书签与文件夹（追加到现有数据；顶层条目落根级）",
        Parameters: [ParamSpec.Req<string>("file_path", "书签 HTML 文件路径")],
        Caps: CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var ct = ctx.Ct;

        if (!File.Exists(filePath))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"文件不存在：{filePath}", correlationId: ctx.CorrelationId));

        var doc = await NetscapeReader.ParseFileAsync(filePath, ct);
        if (!doc.IsValid)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath,
                string.IsNullOrEmpty(doc.Error) ? "不是有效的书签文件" : doc.Error,
                correlationId: ctx.CorrelationId));

        var now = DateTime.UtcNow;

        // 文件夹 ID 由实体构造即生成 → 内存直接建立父子关系，无需逐条提交
        var folderIds = new string[doc.Items.Count];
        var foldersToAdd = new List<Folder>(doc.FolderCount);
        var linksToAdd = new List<Link>(doc.LinkCount);

        for (var i = 0; i < doc.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = doc.Items[i];
            var parentFolderId = item.ParentIndex >= 0 ? folderIds[item.ParentIndex] : null;

            if (item.IsFolder)
            {
                var folder = new Folder
                {
                    Name = NetscapeReader.Truncate(item.Title, MaxFolderNameLength) ?? "未命名文件夹",
                    ParentId = parentFolderId,
                    LinkCount = 0,
                    CreatedAt = item.AddDate ?? now,
                    UpdatedAt = item.LastModified ?? item.AddDate ?? now,
                };
                folderIds[i] = folder.FolderId;
                foldersToAdd.Add(folder);
            }
            else
            {
                var link = new Link
                {
                    Url = NetscapeReader.Truncate(item.Url, MaxLinkUrlLength) ?? string.Empty,
                    Title = NetscapeReader.Truncate(item.Title, MaxLinkTitleLength),
                    Description = item.Description,
                    FaviconUrl = NetscapeReader.Truncate(item.IconUrl, MaxLinkFaviconLength),
                    ListId = parentFolderId,
                    VisitCount = 0,
                    IsImportant = false,
                    CreatedAt = item.AddDate ?? now,
                    UpdatedAt = item.LastModified ?? item.AddDate ?? now,
                };
                linksToAdd.Add(link);
            }
        }

        // 写入循环同样保持可取消（Add 只登记不落盘，但批量循环可能很长）
        foreach (var folder in foldersToAdd)
        {
            ct.ThrowIfCancellationRequested();
            _ = await ctx.Uow.Folders.AddAsync(folder, ct);
        }
        foreach (var link in linksToAdd)
        {
            ct.ThrowIfCancellationRequested();
            _ = await ctx.Uow.Links.AddAsync(link, ct);
        }

        // 批量写入 → 所有现有文件夹内容均视为变动（既有 TouchAllModified 口径；新导入的同样整体刷新）。
        // ListAllAsync 是 AsNoTracking 快照：直接改快照 + UpdateAsync 挂跟踪（替代「逐条 FindAsync」的 N+1），
        // 全部 UPDATE 在引擎提交时一次落库。
        foreach (var folder in await ctx.Uow.Folders.ListAllAsync(ct))
        {
            folder.UpdatedAt = now;
            await ctx.Uow.Folders.UpdateAsync(folder, ct);
        }
        foreach (var folder in foldersToAdd)
            folder.UpdatedAt = now;

        var summary = $"已导入 {foldersToAdd.Count} 个文件夹、{linksToAdd.Count} 个书签"
                      + (doc.SkippedCount > 0 ? $"（跳过 {doc.SkippedCount} 个无效条目）" : "");
        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                folders_created = foldersToAdd.Count,
                links_created = linksToAdd.Count,
                skipped = doc.SkippedCount,
                warnings = doc.Warnings,
            }),
            new ChangeSet(
                Touched: foldersToAdd.Select(f => new EntityRef("folder", f.FolderId))
                    .Concat(linksToAdd.Select(l => new EntityRef("link", l.LinkId)))
                    .ToList(),
                Events: ["links.changed", "folders.changed"],
                HumanSummary: summary,
                // 容错告警同时走 ChangeSet.Warnings（观测面契约：绝不静默吞掉；data.warnings 供 UI 展示保留）
                Warnings: doc.Warnings.Count > 0 ? doc.Warnings : null));
    }
}

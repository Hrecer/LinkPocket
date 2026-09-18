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

        var doc = await NetscapeReader.ParseFileAsync(filePath);
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
        var directLinkCounts = new Dictionary<string, int>(StringComparer.Ordinal);

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

                if (parentFolderId != null)
                    directLinkCounts[parentFolderId] = directLinkCounts.TryGetValue(parentFolderId, out var c) ? c + 1 : 1;
            }
        }

        // 文件夹「链接数」缓存字段按直接子链接数回填（与库内其它写入路径口径一致）
        foreach (var folder in foldersToAdd)
            folder.LinkCount = directLinkCounts.TryGetValue(folder.FolderId, out var n) ? n : 0;

        foreach (var folder in foldersToAdd)
            _ = await ctx.Uow.Folders.AddAsync(folder, ct);
        foreach (var link in linksToAdd)
            _ = await ctx.Uow.Links.AddAsync(link, ct);

        // 批量写入 → 所有文件夹（既有的 + 新导入的）内容均视为变动（既有 TouchAllModified 口径）。
        // ListAllAsync 是 AsNoTracking 快照，取 ID 后经 FindAsync 取跟踪态实体再改写。
        foreach (var folder in await ctx.Uow.Folders.ListAllAsync(ct))
        {
            var tracked = await ctx.Uow.Folders.FindAsync(new FolderId(folder.FolderId), ct);
            if (tracked != null) tracked.UpdatedAt = now;
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
                HumanSummary: summary));
    }
}

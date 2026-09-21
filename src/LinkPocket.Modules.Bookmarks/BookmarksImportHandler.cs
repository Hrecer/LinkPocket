using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>
/// bookmarks.import（Mutation · LongRunning）：从 Netscape 书签文件导入（追加，顶层条目落根级）。
/// 文件夹 ID 内存生成 → 一次性注册 → 引擎单事务提交；新导入的文件夹保留文件解析出的 UpdatedAt。
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
        Description: "Import all bookmarks and folders from a Netscape bookmark file (appended to existing data; top-level entries land at root level)",
        Parameters: [ParamSpec.Req<string>("file_path", "Bookmark HTML file path")],
        Caps: CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var ct = ctx.Ct;

        if (!File.Exists(filePath))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"file does not exist: {filePath}", correlationId: ctx.CorrelationId));

        var doc = await NetscapeReader.ParseFileAsync(filePath, ct);
        if (!doc.IsValid)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath,
                string.IsNullOrEmpty(doc.Error) ? "not a valid bookmark file" : doc.Error,
                correlationId: ctx.CorrelationId));

        var now = DateTime.UtcNow;

        // 文件夹 ID 由实体构造即生成 → 内存直接建立父子关系，无需逐条提交
        var folderIds = new string[doc.Items.Count];
        var foldersToAdd = new List<Folder>(doc.FolderCount);
        var linksToAdd = new List<Link>(doc.LinkCount);

        // 同层唯一命名（Windows 口径）：占用表先预置**库里已有的根级名**，导入出来的每一层在内存里逐项累积。
        // 缺了这一步会出现两种重名：① 文件里同一父下两个同名兄弟；② 导入项与既有文件夹同名。
        // 口径只由命名服务提供（SiblingNameTable 只管占用集合；编号算法在 Kernel 内，模块拿不到、也立不了第二份）。
        var naming = await ctx.Uow.Naming.CreateTableAsync(null, ct);
        var foldersRenamed = 0;

        for (var i = 0; i < doc.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = doc.Items[i];
            var parentFolderId = item.ParentIndex >= 0 ? folderIds[item.ParentIndex] : null;

            if (item.IsFolder)
            {
                // Truncate 签名返回 string?，但 item.Title 解析时保证非空，?? 为 nullable 流分析兜底（保留以免 CS8601）
                var desired = NetscapeReader.Truncate(item.Title, MaxFolderNameLength) ?? "Untitled folder";
                var name = naming.Resolve(parentFolderId, desired);
                if (!string.Equals(name, desired, StringComparison.Ordinal)) foldersRenamed++;

                var folder = new Folder
                {
                    Name = name,
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

        // 追加式导入不改动任何既有文件夹（同层撞名只在**新导入项**之间/与既有名之间做编号，绝不改动既有文件夹），
        // 因此不做全表 UpdatedAt 触模
        // （过去 ListAllAsync 全量逐条 UpdateAsync 是 N 次 UPDATE 的写放大，且会覆盖用户已有文件夹的更新语义）。
        // 新导入的文件夹保留文件解析出的 LAST_MODIFIED（导出→导入→再导出不丢"最后更新"）。

        var summary = $"Imported {foldersToAdd.Count} folders and {linksToAdd.Count} bookmarks"
                      + (foldersRenamed > 0 ? $"({foldersRenamed} same-named entries were auto-numbered)" : "")
                      + (doc.SkippedCount > 0 ? $"({doc.SkippedCount} invalid entries skipped)" : "");
        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                folders_created = foldersToAdd.Count,
                folders_renamed = foldersRenamed,
                links_created = linksToAdd.Count,
                skipped = doc.SkippedCount,
                warnings = doc.Warnings,
            }),
            new ChangeSet(
                Touched: foldersToAdd.Select(f => new EntityRef("folder", f.FolderId))
                    .Concat(linksToAdd.Select(l => new EntityRef("link", l.LinkId)))
                    .ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged, LinkPocket.Contracts.DomainEventNames.FoldersChanged],
                HumanSummary: summary,
                // 容错告警同时走 ChangeSet.Warnings（观测面契约：绝不静默吞掉；data.warnings 供 UI 展示保留）
                Warnings: doc.Warnings.Count > 0 ? doc.Warnings : null));
    }
}

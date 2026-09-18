using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>
/// backup.import（Mutation · Destructive 两阶段确认 · FileIo）：从 .lpbackup 导入。
/// replace = false：追加导入；replace = true：先清空全部数据（含回收站）再导入（= 完全重置）。
/// 导入整体为引擎单事务（拓扑序父先于子），任何一步失败全部回滚；导入后全部文件夹视为变动。
/// </summary>
internal sealed class BackupImportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "backup.import",
        Category: "backup",
        Description: "从 .lpbackup 导入（replace=false 追加；replace=true 先清空全部数据再导入，两阶段确认）",
        Parameters:
        [
            ParamSpec.Req<string>("file_path", "备份文件路径"),
            ParamSpec.Opt<bool>("replace", "true = 清空后导入（完全重置）；缺省 = 追加导入"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive | CommandCaps.FileIo | CommandCaps.LongRunning | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var replace = CommandArgs.OptionalBool(args, "replace");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var file = await BackupIO.ReadAsync(filePath, ct);
        if (!file.Valid)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, string.Join("; ", file.Errors), correlationId: ctx.CorrelationId));

        // —— replace：清空全部数据（含回收站）——
        if (replace)
        {
            foreach (var link in await uow.Trash.ListStandaloneLinksAsync(ct))
                await uow.Trash.RemoveLinkAsync(new LinkId(link.LinkId), ct);
            foreach (var unit in await uow.Trash.ListFoldersAsync(ct))
            {
                foreach (var link in await uow.Trash.ListLinksByUnitAsync(new Kernel.TrashFolderId(unit.TrashFolderId), ct))
                    await uow.Trash.RemoveLinkAsync(new LinkId(link.LinkId), ct);
                await uow.Trash.RemoveFolderAsync(new Kernel.TrashFolderId(unit.TrashFolderId), ct);
            }

            foreach (var link in await uow.Links.ListAsync(new LinkQuerySpec(), ct))
                await uow.Links.RemoveAsync(new LinkId(link.LinkId), ct);
            foreach (var folder in await uow.Folders.ListAllAsync(ct))
                await uow.Folders.RemoveAsync(new FolderId(folder.FolderId), ct);
        }

        // —— 深度计算（带循环防护）：父先于子 ——
        var folders = file.Data.Folders ?? [];
        var links = file.Data.Links ?? [];
        var folderByKey = folders.ToDictionary(f => f.Key);
        var depthMemo = new Dictionary<string, int>();

        int DepthOf(string? key)
        {
            if (key == null) return -1;
            var depth = 0;
            var cur = key;
            while (cur != null)
            {
                if (depthMemo.TryGetValue(cur, out var memo)) { depth += memo; break; }
                if (depth > folders.Count + 1)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.Internal, "备份文件的文件夹层级存在循环引用，无法导入", correlationId: ctx.CorrelationId));
                if (!folderByKey.TryGetValue(cur, out var node)) break;
                cur = node.Parent;
                depth++;
            }
            return depth;
        }

        var sortedFolders = folders
            .Select((f, index) => (f, index, depth: DepthOf(f.Parent) + 1))
            .OrderBy(t => t.depth)
            .ThenBy(t => t.index)
            .ToList();

        // —— 文件夹（临时 key → 新实体 ID 映射）——
        var keyToFolderId = new Dictionary<string, string>();
        var foldersCreated = 0;
        foreach (var t in sortedFolders)
        {
            var f = t.f;
            var folder = new Folder
            {
                Name = f.Name,
                Description = f.Description,
                ParentId = f.Parent != null && keyToFolderId.TryGetValue(f.Parent, out var parentId) ? parentId : null,
                LinkCount = 0,
                SortOrder = f.SortOrder,
                VisitCount = f.VisitCount,
                LastVisitedAt = BackupIO.ParseNullableDateTime(f.LastVisitedAt),
                CreatedAt = BackupIO.ParseDateTime(f.CreatedAt),
                UpdatedAt = BackupIO.ParseDateTime(f.UpdatedAt),
            };
            _ = await uow.Folders.AddAsync(folder, ct);
            keyToFolderId[f.Key] = folder.FolderId;
            foldersCreated++;
        }

        // —— 书签（图标缓存文件随包恢复）——
        var linksCreated = 0;
        foreach (var l in links)
        {
            ct.ThrowIfCancellationRequested();
            string? resolvedFaviconUrl = null;
            if (!string.IsNullOrWhiteSpace(l.FaviconUrl))
                resolvedFaviconUrl = await BackupIO.RestoreFaviconFileAsync(l.FaviconUrl, file.Favicons, ct);

            _ = await uow.Links.AddAsync(new Link
            {
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = resolvedFaviconUrl,
                ListId = l.Folder != null && keyToFolderId.TryGetValue(l.Folder, out var listId) ? listId : null,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                LastVisitedAt = BackupIO.ParseNullableDateTime(l.LastVisitedAt),
                CreatedAt = BackupIO.ParseDateTime(l.CreatedAt),
                UpdatedAt = BackupIO.ParseDateTime(l.UpdatedAt),
            }, ct);
            linksCreated++;
        }

        // —— 直接子链接计数回填 + 全部文件夹视为变动（既有 TouchAllModified 口径）。
        // ListAllAsync 是 AsNoTracking 快照，取 ID 后经 FindAsync 取跟踪态实体再改写。——
        var counts = await uow.Links.CountByFolderAsync(ct);
        var allFolderIds = (await uow.Folders.ListAllAsync(ct)).Select(f => f.FolderId)
            .Concat(keyToFolderId.Values)
            .Distinct();
        foreach (var folderId in allFolderIds)
        {
            var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
            if (folder == null) continue;
            folder.LinkCount = counts.GetValueOrDefault(new FolderId(folder.FolderId));
            folder.UpdatedAt = DateTime.UtcNow;
        }

        var events = new List<string> { "links.changed", "folders.changed" };
        if (replace) events.Add("trash.changed");

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                folders_created = foldersCreated,
                links_created = linksCreated,
                total_items = foldersCreated + linksCreated,
                replace,
            }),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: events,
                HumanSummary: $"已导入 {foldersCreated} 个文件夹、{linksCreated} 个书签" + (replace ? "（清空后导入）" : "")));
    }
}

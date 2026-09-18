using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.delete（Mutation）：删除文件夹。级联三模式（既有口径）：
/// trash_links（默认）= 整子树镜像进回收站（Windows 式；回收站保留原 ID + 位置快照）；
/// delete_all = 连链接一起物理删除；move_to_list = 链接转移到目标文件夹后删除空子树。
/// 删除确认在 UI 层（ConfirmDialog 唯一入口），协议层不打 Destructive 标志（行为等价项）。
/// </summary>
internal sealed class FolderDeleteHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.delete",
        Category: "folders",
        Description: "删除文件夹（默认整子树移入回收站；cascade 可选 delete_all / move_to_list）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "文件夹 ID"),
            ParamSpec.Opt<string>("cascade", "trash_links（默认）| delete_all | move_to_list"),
            ParamSpec.Opt<string>("target_list_id", "move_to_list 模式的目标文件夹 ID"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: null,
        Impact: ImpactSummary.Folder);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var cascade = CommandArgs.OptionalString(args, "cascade") ?? "trash_links";
        var targetListId = CommandArgs.OptionalString(args, "target_list_id");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var allFolders = await uow.Folders.ListAllAsync(ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == id.Value)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));

        // 后代集合（含自身）
        var descendantIds = new List<string>();
        FolderSupport.CollectDescendantIds(allFolders, id.Value, descendantIds);
        descendantIds.Add(id.Value);
        var descendantSet = descendantIds.ToHashSet(StringComparer.Ordinal);
        var subtreeFolders = allFolders.Where(f => descendantSet.Contains(f.FolderId)).ToList();

        // 受影响链接（一次读全量 + 内存过滤——与既有同口径，规模有界）
        var allLinks = await uow.Links.ListAsync(new LinkQuerySpec(), ct);
        var affectedLinks = allLinks.Where(l => l.ListId != null && descendantSet.Contains(l.ListId)).ToList();

        var trashedLinkCount = 0;
        switch (cascade)
        {
            case "delete_all":
                foreach (var link in affectedLinks) await uow.Links.RemoveAsync(new LinkId(link.LinkId), ct);
                foreach (var f in subtreeFolders) await uow.Folders.RemoveAsync(new FolderId(f.FolderId), ct);
                break;

            case "move_to_list":
                if (string.IsNullOrEmpty(targetListId))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.RequiredParam, "move_to_list 模式需要 target_list_id", correlationId: ctx.CorrelationId));
                var target = await uow.Folders.FindAsync(new FolderId(targetListId), ct)
                    ?? throw new EngineException(EngineErrors.Of(
                        EngineErrors.EntityNotFound, $"目标文件夹 {targetListId} 不存在", correlationId: ctx.CorrelationId));
                if (descendantSet.Contains(targetListId))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.CycleDetected,
                        $"目标文件夹「{target.Name}」位于待删除子树内，链接转移后会被一并删除", correlationId: ctx.CorrelationId));

                foreach (var link in affectedLinks)
                {
                    link.ListId = targetListId;
                    await uow.Links.UpdateAsync(link, ct);
                }

                foreach (var f in subtreeFolders) await uow.Folders.RemoveAsync(new FolderId(f.FolderId), ct);
                await RefreshLinkCountAsync(uow, targetListId, ct);
                break;

            default: // trash_links：整子树镜像进回收站
                var trashedAt = DateTime.UtcNow;
                var subtreeIdSet = subtreeFolders.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);

                foreach (var f in subtreeFolders)
                {
                    _ = await uow.Trash.AddFolderAsync(new TrashedFolder
                    {
                        TrashFolderId = f.FolderId,   // 回收站保留原 ID（2026-09-17 定稿）
                        ParentTrashFolderId = f.ParentId != null && subtreeIdSet.Contains(f.ParentId) ? f.ParentId : null,
                        Name = f.Name,
                        OriginFolderId = f.FolderId,
                        OriginPath = await uow.Trees.PathDisplayAsync(new FolderId(f.FolderId), ct),
                        DeletedAt = trashedAt,
                    }, ct);
                }

                foreach (var link in affectedLinks)
                {
                    _ = await uow.Trash.AddLinkAsync(new TrashedLink
                    {
                        LinkId = link.LinkId,
                        Url = link.Url,
                        Title = link.Title,
                        Description = link.Description,
                        FaviconUrl = link.FaviconUrl,
                        TrashFolderId = link.ListId,
                        OriginListId = link.ListId,
                        OriginPath = await uow.Trees.PathDisplayAsync(
                            link.ListId == null ? null : new FolderId(link.ListId), ct),
                        LastVisitedAt = link.LastVisitedAt,
                        VisitCount = link.VisitCount,
                        IsImportant = link.IsImportant,
                        DeletedAt = trashedAt,
                        CreatedAt = link.CreatedAt,
                        UpdatedAt = DateTime.UtcNow,
                    }, ct);
                    await uow.Links.RemoveAsync(new LinkId(link.LinkId), ct);
                    trashedLinkCount++;
                }

                foreach (var f in subtreeFolders) await uow.Folders.RemoveAsync(new FolderId(f.FolderId), ct);
                break;
        }

        // 删除 → 原父级内容有变；move_to_list 目标文件夹也变了
        await uow.Trees.TouchModifiedAsync(
            folder.ParentId == null ? null : new FolderId(folder.ParentId), ct);
        if (cascade == "move_to_list" && !string.IsNullOrEmpty(targetListId))
            await uow.Trees.TouchModifiedAsync(new FolderId(targetListId), ct);

        var events = new List<string> { LinkPocket.Contracts.DomainEventNames.FoldersChanged, LinkPocket.Contracts.DomainEventNames.LinksChanged };
        if (cascade == "trash_links") events.Add(LinkPocket.Contracts.DomainEventNames.TrashChanged);

        return CommandResult.Ok(
            new FolderDeleteResult(cascade, subtreeFolders.Count, trashedLinkCount),
            new ChangeSet(
                Touched: [new EntityRef("folder", id.Value)],
                Events: events,
                HumanSummary: $"已删除文件夹「{folder.Name}」（{cascade}）"));
    }

    /// <summary>直接子链接计数缓存回填（LinkCount 列的既有维护口径）。</summary>
    internal static async Task RefreshLinkCountAsync(Kernel.IUnitOfWork uow, string folderId, CancellationToken ct)
    {
        var counts = await uow.Links.CountByFolderAsync(ct);
        var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
        if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(folderId));
    }
}

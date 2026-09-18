using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>trash.purge_batch（★ 引擎能力，不接 UI · Destructive 两阶段确认）：批量永久删除。</summary>
internal sealed class TrashPurgeBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.purge_batch",
        Category: "trash",
        Description: "批量永久删除回收站条目（link_ids 单条书签快照；folder_ids 整单元子树；不可恢复）",
        Parameters:
        [
            ParamSpec.Opt<IReadOnlyList<string>>("link_ids", "书签快照 ID 列表"),
            ParamSpec.Opt<IReadOnlyList<string>>("folder_ids", "回收站单元 ID 列表"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive,
        Impact: ImpactSummary.Link);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        var folderIds = CommandArgs.StringArray(args, "folder_ids");
        if (linkIds.Count == 0 && folderIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids / folder_ids 至少提供一个", correlationId: ctx.CorrelationId));
        var ct = ctx.Ct;

        foreach (var id in linkIds)
        {
            _ = await ctx.Uow.Trash.FindLinkAsync(new LinkId(id), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站中不存在书签 {id}", correlationId: ctx.CorrelationId));
        }

        var allFolders = await ctx.Uow.Trash.ListFoldersAsync(ct);
        var subtreeCounts = new List<int>();
        foreach (var id in folderIds)
        {
            if (!allFolders.Any(f => f.TrashFolderId == id))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站单元 {id} 不存在", correlationId: ctx.CorrelationId));
            subtreeCounts.Add(TrashSupport.CollectSubtreeIds(allFolders, id).Count);
        }

        // 校验全部通过后执行
        foreach (var id in linkIds)
            await ctx.Uow.Trash.RemoveLinkAsync(new LinkId(id), ct);

        var purgedFolders = 0;
        foreach (var id in folderIds)
        {
            var ids = TrashSupport.CollectSubtreeIds(allFolders, id);
            foreach (var unitId in ids)
            {
                foreach (var link in await ctx.Uow.Trash.ListLinksByUnitAsync(new TrashFolderId(unitId), ct))
                    await ctx.Uow.Trash.RemoveLinkAsync(new LinkId(link.LinkId), ct);
                await ctx.Uow.Trash.RemoveFolderAsync(new TrashFolderId(unitId), ct);
                purgedFolders++;
            }
        }

        return CommandResult.Ok(
            new TrashPurgeBatchResult(linkIds.Count, purgedFolders),
            new ChangeSet(
                Touched: linkIds.Select(i => new EntityRef("trash_link", i))
                    .Concat(folderIds.Select(i => new EntityRef("trash_unit", i)))
                    .ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.TrashChanged],
                HumanSummary: $"已永久删除 {linkIds.Count} 个书签、{purgedFolders} 个回收站单元"));
    }
}

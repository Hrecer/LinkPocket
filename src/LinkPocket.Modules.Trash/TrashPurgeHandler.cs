using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.purge（Mutation · Destructive 两阶段确认）：永久删除——
/// 单条书签快照，或一个回收站单元（含全部子单元与单元内书签快照）。
/// 仅影响回收站表，主表不受影响。
/// </summary>
internal sealed class TrashPurgeHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.purge",
        Category: "trash",
        Description: "永久删除回收站条目（书签快照或整单元子树；不可恢复，需两阶段确认）",
        Parameters:
        [
            ParamSpec.Req<string>("id", "回收站条目 ID"),
            ParamSpec.Req<bool>("is_folder", "true = 回收站单元（整子树清除）；false = 单条书签快照"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive,
        Impact: ImpactSummary.Link);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "id");
        var isFolder = CommandArgs.RequireBool(args, "is_folder");
        var ct = ctx.Ct;

        if (isFolder)
        {
            var all = await ctx.Uow.Trash.ListFoldersAsync(ct);
            if (!all.Any(f => f.TrashFolderId == id))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站单元 {id} 不存在", correlationId: ctx.CorrelationId));

            var ids = TrashSupport.CollectSubtreeIds(all, id);
            var idSet = ids.ToHashSet(StringComparer.Ordinal);

            // 单元内全部书签快照（各单元逐一取出后按子树过滤）
            foreach (var unitId in ids)
            {
                foreach (var link in await ctx.Uow.Trash.ListLinksByUnitAsync(new TrashFolderId(unitId), ct))
                    await ctx.Uow.Trash.RemoveLinkAsync(new LinkId(link.LinkId), ct);
            }

            foreach (var unitId in ids)
                await ctx.Uow.Trash.RemoveFolderAsync(new TrashFolderId(unitId), ct);

            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new { purged = "folder", units = ids.Count }),
                ChangeSet.Of(
                    new EntityRef("trash_unit", id),
                    LinkPocket.Contracts.DomainEventNames.TrashChanged,
                    $"已永久删除回收站单元（含 {ids.Count} 个单元）"));
        }
        else
        {
            _ = await ctx.Uow.Trash.FindLinkAsync(new LinkId(id), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站中不存在书签 {id}", correlationId: ctx.CorrelationId));
            await ctx.Uow.Trash.RemoveLinkAsync(new LinkId(id), ct);

            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new { purged = "link" }),
                ChangeSet.Of(
                    new EntityRef("trash_link", id),
                    LinkPocket.Contracts.DomainEventNames.TrashChanged,
                    "已永久删除回收站书签"));
        }
    }
}

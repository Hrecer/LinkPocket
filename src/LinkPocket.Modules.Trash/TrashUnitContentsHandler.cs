using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.unit_contents（Query）：被删文件夹单元的内容（回收站「打开目录」用）——
/// 直接子单元（folder 条目）+ 子树内全部书签快照（link 条目）。
/// </summary>
internal sealed class TrashUnitContentsHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.unit_contents",
        Category: "trash",
        Description: "Get trash unit contents: direct child units + all bookmark snapshots in the subtree",
        Parameters: [ParamSpec.Req<string>("id", "Trash unit ID")],
        Caps: CommandCaps.Query,
        // 单元内容 = 单元全量 + 每子树一趟（N+1），只读回收站两表
        Cache: CachePolicy.Of(10, DomainEventNames.TrashChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "id");
        var ct = ctx.Ct;

        var all = await ctx.Uow.Trash.ListFoldersAsync(ct);
        if (!all.Any(f => f.TrashFolderId == id))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"trash unit {id} does not exist", correlationId: ctx.CorrelationId));

        var ids = TrashSupport.CollectSubtreeIds(all, id);
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        var result = new List<TrashEntryDto>();

        // 直接子单元（folder 条目；更深层内容随其自身被再次打开）
        foreach (var f in all.Where(f => f.ParentTrashFolderId == id).OrderBy(f => f.DeletedAt))
        {
            result.Add(new TrashEntryDto
            {
                Id = f.TrashFolderId,
                EntryType = LinkPocket.Contracts.TrashEntryType.Folder,
                Name = f.Name,
                Description = f.Description,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
            });
        }

        // 子树内全部书签快照
        foreach (var unitId in ids)
        {
            foreach (var l in await ctx.Uow.Trash.ListLinksByUnitAsync(new TrashFolderId(unitId), ct))
            {
                result.Add(new TrashEntryDto
                {
                    Id = l.LinkId,
                    EntryType = LinkPocket.Contracts.TrashEntryType.Link,
                    Name = l.Title ?? l.Url ?? string.Empty,
                    Url = l.Url,
                    Description = l.Description,
                    FaviconUrl = l.FaviconUrl,
                    OriginPath = l.OriginPath,
                    DeletedAt = l.DeletedAt,
                });
            }
        }

        return CommandResult.Ok(result);
    }
}

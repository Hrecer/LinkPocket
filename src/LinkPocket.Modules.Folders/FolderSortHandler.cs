using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.sort（Mutation）：重排某父目录下的文件夹顺序（sort_order = 列表下标）。</summary>
internal sealed class FolderSortHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.sort",
        Category: "folders",
        Description: "Reorder folders under a parent (the order of item_ids is the new order; parent_id default = root level)",
        Parameters:
        [
            ParamSpec.Opt<string>("parent_id", "Parent folder ID; default = root level"),
            ParamSpec.Req<IReadOnlyList<string>>("item_ids", "Folder IDs in the new order"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var parentId = CommandArgs.OptionalString(args, "parent_id");
        var itemIds = CommandArgs.StringArray(args, "item_ids");
        var ct = ctx.Ct;

        if (itemIds.Count != itemIds.Distinct().Count())
            throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, "item_ids contains duplicate folder IDs: the reorder target must be a set of unique folders",
                correlationId: ctx.CorrelationId));

        var siblings = await ctx.Uow.Folders.ChildrenOfAsync(parentId == null ? null : new FolderId(parentId), ct);
        var validIds = siblings.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            if (!validIds.Contains(itemId))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound,
                    $"folder {itemId} does not belong to the target folder (parent_id={parentId ?? "root"})",
                    correlationId: ctx.CorrelationId));
        }

        for (var i = 0; i < itemIds.Count; i++)
        {
            var folder = await ctx.Uow.Folders.FindAsync(new FolderId(itemIds[i]), ct);
            if (folder != null) folder.SortOrder = i;
        }

        return CommandResult.Ok(
            new FolderSortResult(itemIds.Count),
            ChangeSet.Of(
                new EntityRef("folder", parentId ?? "*"),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"Reordered {itemIds.Count} folder(s)"));
    }
}

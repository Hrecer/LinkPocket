using System.Text.Json;
using LinkPocket.Api;
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
        Description: "重排某父目录下的文件夹顺序（item_ids 顺序即新顺序；parent_id 缺省 = 根级）",
        Parameters:
        [
            ParamSpec.Opt<string>("parent_id", "父目录 ID；缺省 = 根级"),
            ParamSpec.Req<IReadOnlyList<string>>("item_ids", "按新顺序排列的文件夹 ID 列表"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var parentId = CommandArgs.OptionalString(args, "parent_id");
        var itemIds = CommandArgs.StringArray(args, "item_ids");
        var ct = ctx.Ct;

        var siblings = await ctx.Uow.Folders.ChildrenOfAsync(parentId == null ? null : new FolderId(parentId), ct);
        var validIds = siblings.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            if (!validIds.Contains(itemId))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound,
                    $"文件夹 {itemId} 不属于目标目录（parent_id={parentId ?? "根"}）",
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
                "folders.changed",
                $"已重排 {itemIds.Count} 个文件夹"));
    }
}

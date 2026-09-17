using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>trash.tree（Query）：全部回收站单元（含删除根与子单元），LinkCount = 单元子树内书签总数。</summary>
internal sealed class TrashTreeHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.tree",
        Category: "trash",
        Description: "回收站单元树（平铺节点列表；link_count = 单元子树内的书签总数，层级由调用方组装）",
        Parameters: [],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folders = await ctx.Uow.Trash.ListFoldersAsync(ctx.Ct);
        var links = await ctx.Uow.Trash.CountLinksByUnitAsync(ctx.Ct);

        var result = new List<TrashFolderDto>();
        foreach (var f in folders)
        {
            var subtreeIds = TrashSupport.CollectSubtreeIds(folders, f.TrashFolderId);
            var linkCount = subtreeIds.Sum(id => links.GetValueOrDefault(new Kernel.TrashFolderId(id)));

            result.Add(new TrashFolderDto
            {
                TrashFolderId = f.TrashFolderId,
                ParentTrashFolderId = f.ParentTrashFolderId,
                Name = f.Name,
                LinkCount = linkCount,
                DeletedAt = f.DeletedAt,
            });
        }

        return CommandResult.Ok(result);
    }
}

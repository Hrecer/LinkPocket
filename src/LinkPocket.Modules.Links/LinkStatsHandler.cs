using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.stats（Query）：全库计数（与既有 GetCountsAsync 口径逐条等价）：
/// 回收站项数 = 单独删除书签 + 被删文件夹单元（Windows 口径：按删除操作计数）。
/// </summary>
internal sealed class LinkStatsHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.stats",
        Category: "links",
        Description: "全库统计：链接总数 / 回收站项数 / 根级书签数 / 按目录直接子链接计数",
        Parameters: [],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var ct = ctx.Ct;
        var standalone = await ctx.Uow.Trash.ListStandaloneLinksAsync(ct);
        var units = await ctx.Uow.Trash.ListFoldersAsync(ct);

        var dto = new LinkCountsDto
        {
            Total = await ctx.Uow.Links.CountAsync(new LinkFilter(), ct),
            Trash = standalone.Count + units.Count(f => f.ParentTrashFolderId == null),
            RootLevel = await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct),
            ByFolder = (await ctx.Uow.Links.CountByFolderAsync(ct))
                .ToDictionary(kv => kv.Key.Value, kv => kv.Value),
        };
        return CommandResult.Ok(dto);
    }
}

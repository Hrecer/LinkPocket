using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.tree（Query）：全部文件夹平铺列表（UI 自行组装层级），递归计数口径与目录页一致。</summary>
internal sealed class FolderTreeHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.tree",
        Category: "folders",
        Description: "取全部文件夹（平铺、按名称排序；link_count = 递归子链接数、direct_link_count = 直接子链接数，层级由调用方组装）",
        Parameters: [],
        Caps: CommandCaps.Query,
        // 树 = 文件夹全量 + 递归计数全量重算（7.2：树快照缓存 + folders.changed 精确失效）
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var counts = await ctx.Uow.Trees.LinkCountsAsync(ctx.Ct);
        var tree = allFolders
            .Select(f => f.ToDto(counts))
            .OrderBy(f => f.Name, StringComparer.CurrentCulture)
            .ToList();
        return CommandResult.Ok(tree);
    }
}

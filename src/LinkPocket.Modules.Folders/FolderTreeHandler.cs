using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.tree（Query）：全部文件夹平铺列表（UI 自行组装层级），计数两口径与目录页一致。
/// 排序缺省名称升序；<c>sort_by=sort_order</c> 时按 <c>folders.sort</c> 写入的手动顺序（此前只有写路径）。
/// </summary>
internal sealed class FolderTreeHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.tree",
        Category: "folders",
        Description: "取全部文件夹（平铺；link_count = 递归子链接数、direct_link_count = 直接子链接数，层级由调用方组装）",
        Parameters:
        [
            ParamSpec.Opt<string>("sort_by", "name | sort_order | created_at | updated_at | last_visited_at | visit_count"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
        ],
        Caps: CommandCaps.Query,
        // 树 = 文件夹全量 + 计数两口径全量重算（树快照缓存 + folders.changed 精确失效）
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "name";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "asc";

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var counts = await ctx.Uow.Trees.LinkCountsAsync(ctx.Ct);
        var tree = FolderSupport.SortFolders(allFolders.Select(f => f.ToDto(counts)), sortBy, sortOrder);
        return CommandResult.Ok(tree);
    }
}
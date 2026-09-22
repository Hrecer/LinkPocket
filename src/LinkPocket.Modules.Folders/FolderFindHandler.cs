using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.find（★ 引擎能力，不接 UI）：按名称定位文件夹（精确/包含），供 AI 与批量场景按名导航。
/// </summary>
internal sealed class FolderFindHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.find",
        Category: "folders",
        Description: "Find folders by name (exact match by default, optional contains match; used to locate a folder by name)",
        Parameters:
        [
            ParamSpec.Req<string>("name", "Folder name to match"),
            ParamSpec.Opt<bool>("contains", "true = contains match; default = exact match (case-insensitive)"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var contains = CommandArgs.OptionalBool(args, "contains");

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var matched = contains
            ? allFolders.Where(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            : allFolders.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // 命中数有限；逐个补两种计数口径代价可忽略（与 folders.get 同口径）
        var counts = await ctx.Uow.Trees.LinkCountsAsync(ctx.Ct);
        var result = matched
            .Select(f => f.ToDto(counts))
            .OrderBy(f => f.Name, NameOrder.Comparer)
            .ToList();
        return CommandResult.Ok(result);
    }
}

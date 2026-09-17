using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.roots（Query）：根级（无归属）书签列表（缺省 created_at 降序，上限 per_page=50）。</summary>
internal sealed class LinkRootsHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.roots",
        Category: "links",
        Description: "取根级（未归类）书签（sort_by 缺省 created_at、sort_order 缺省 desc；上限 per_page=50）",
        Parameters:
        [
            ParamSpec.Opt<string>("sort_by", "created_at | updated_at | last_visited_at | visit_count | title"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
            ParamSpec.Opt<int>("per_page", "上限（缺省 50）"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "created_at";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "desc";
        var perPage = Math.Max(1, CommandArgs.OptionalInt(args, "per_page", 50));

        var sort = QueryParsing.ParseSort(
            sortBy, QueryParsing.NormalizeOrder(sortOrder), QueryParsing.LinkSortFields, "created_at");
        var items = await ctx.Uow.Links.ListAsync(new LinkQuerySpec
        {
            Filter = new LinkFilter { Unfiled = true },
            Sort = sort,
            Page = new PageSpec(1, perPage),
        }, ctx.Ct);
        return CommandResult.Ok(items.Select(l => l.ToDto()).ToList());
    }
}

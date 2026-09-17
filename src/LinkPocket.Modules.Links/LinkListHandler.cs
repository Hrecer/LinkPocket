using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.list（Query）：分页链接查询（过滤 + 排序 + 分页；per_page 缺省 20）。
/// 排序与分页全部 SQL 下推（含「最后查看」为空的恒排最后，由排序引擎表达）。
/// </summary>
internal sealed class LinkListHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.list",
        Category: "links",
        Description: "分页查询链接（可按目录/关键词/重要/日期过滤；sort_by 缺省 created_at、sort_order 缺省 desc）",
        Parameters:
        [
            ParamSpec.Opt<string>("list_id", "目录 ID；缺省 = 全部"),
            ParamSpec.Opt<string>("search", "关键词（标题/地址/描述包含）"),
            ParamSpec.Opt<bool>("is_important", "是否只看重要书签"),
            ParamSpec.Opt<string>("date_from", "创建时间下界（ISO）"),
            ParamSpec.Opt<string>("date_to", "创建时间上界（ISO）"),
            ParamSpec.Opt<string>("sort_by", "created_at | updated_at | last_visited_at | visit_count | title"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
            ParamSpec.Opt<int>("page", "页码（从 1 起）"),
            ParamSpec.Opt<int>("per_page", "每页数量（缺省 20）"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var listId = FolderIds.Normalize(CommandArgs.OptionalString(args, "list_id"));
        var search = CommandArgs.OptionalString(args, "search");
        var isImportant = CommandArgs.OptionalBoolOrNull(args, "is_important");
        var dateFrom = CommandArgs.OptionalString(args, "date_from");
        var dateTo = CommandArgs.OptionalString(args, "date_to");
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "created_at";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "desc";
        var page = Math.Max(1, CommandArgs.OptionalInt(args, "page", 1));
        var perPage = Math.Max(0, CommandArgs.OptionalInt(args, "per_page", 20));
        var ct = ctx.Ct;

        var filter = new LinkFilter
        {
            Search = search,
            FolderId = listId == null ? null : new FolderId(listId),
            IsImportant = isImportant,
            CreatedFrom = dateFrom == null ? null : LinkSupport.ParseDate(dateFrom, "date_from"),
            CreatedTo = dateTo == null ? null : LinkSupport.ParseDate(dateTo, "date_to"),
        };

        var sort = QueryParsing.ParseSort(sortBy, sortOrder, QueryParsing.LinkSortFields, "created_at");
        var items = await ctx.Uow.Links.ListAsync(new LinkQuerySpec
        {
            Filter = filter,
            Sort = sort,
            Page = new PageSpec(page, perPage),
        }, ct);
        var total = await ctx.Uow.Links.CountAsync(filter, ct);

        var lastPage = perPage > 0 ? (int)Math.Ceiling(total / (double)perPage) : 1;
        var dto = new PagedLinksDto
        {
            Links = items.Select(l => l.ToDto()).ToList(),
            TotalCount = total,
            CurrentPage = page,
            LastPage = lastPage,
        };
        return CommandResult.Ok(dto);
    }
}

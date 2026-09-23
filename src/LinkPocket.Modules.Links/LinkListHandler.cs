using LinkPocket.Data;
using System.Text.Json;
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
        Description: "Paged link query (filter by folder / keyword / important / date; sort_by default created_at, sort_order default desc)",
        Parameters:
        [
            ParamSpec.Opt<string>("list_id", "Folder ID; default = all"),
            ParamSpec.Opt<string>("search", "Keyword (contains in title/url/description)"),
            ParamSpec.Opt<bool>("is_important", "Only important bookmarks"),
            ParamSpec.Opt<string>("date_from", "Creation time lower bound (ISO)"),
            ParamSpec.Opt<string>("date_to", "Creation time upper bound (ISO)"),
            // 枚举 = QueryParsing.LinkSortFieldNames（SQL 下推白名单，7 字段；描述与白名单同源）
            ParamSpec.Opt<string>("sort_by", "title | url | created_at | updated_at | last_visited_at | visit_count | is_important",
                enumValues: QueryParsing.LinkSortFieldNames),
            ParamSpec.Opt<string>("sort_order", "asc | desc", enumValues: ["asc", "desc"]),
            ParamSpec.Opt<int>("page", "Page number (1-based)"),
            ParamSpec.Opt<int>("per_page", "Page size (default 20)"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var listId = CommandArgs.OptionalString(args, "list_id");
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

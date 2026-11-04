using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.smart_list_page（Query）：智能列表的**分页形态**（总数 + 指定排序的一页）。
/// 界面首帧用它拿「真实总数 + 首页真实行」，其余页在后台按同一排序补齐——
/// 十万行不必等整表取完才上屏。per_page=0 = 全部（外部调用方的默认语义）。
/// </summary>
internal sealed class LinkSmartListPageHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.smart_list_page",
        Category: "links",
        Description: "Paged smart list (total + one page; sort_by defaults to the preset's natural order; per_page=0 returns all)",
        Parameters:
        [
            ParamSpec.Req<string>("kind", "recently_added | recently_visited | recently_edited | most_visited",
                enumValues: ["recently_added", "recently_visited", "recently_edited", "most_visited"]),
            ParamSpec.Opt<string>("sort_by", "title | url | created_at | updated_at | last_visited_at | visit_count | is_important",
                enumValues: QueryParsing.LinkSortFieldNames),
            ParamSpec.Opt<string>("sort_order", "asc | desc", enumValues: ["asc", "desc"]),
            ParamSpec.Opt<int>("page", "Page number (1-based; default 1)"),
            ParamSpec.Opt<int>("per_page", "Page size; 0 or omitted = all matching rows"),
            ParamSpec.Opt<int>("days", "Time window in days, only for the recently_* presets (default 7)"),
        ],
        Caps: CommandCaps.Query,
        Cache: CachePolicy.Of(10, DomainEventNames.LinksChanged),
        Client: new("LinkSmartListPageAsync", typeof(SmartListPageDto)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var kind = CommandArgs.RequireString(args, "kind");
        var days = Math.Max(1, CommandArgs.OptionalInt(args, "days", SmartListPresets.DefaultDays));
        var since = DateTime.UtcNow.AddDays(-days);
        var page = Math.Max(1, CommandArgs.OptionalInt(args, "page", 1));
        var perPage = Math.Max(0, CommandArgs.OptionalInt(args, "per_page", 0));
        var ct = ctx.Ct;

        if (!SmartListPresets.TryResolve(kind, since, out var filter, out var naturalSort))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange, $"unknown smart list kind: {kind}", correlationId: ctx.CorrelationId));

        var sortBy = CommandArgs.OptionalString(args, "sort_by");
        var sortOrder = CommandArgs.OptionalString(args, "sort_order");
        var sort = string.IsNullOrEmpty(sortBy)
            ? naturalSort
            : QueryParsing.ParseSort(sortBy, sortOrder, QueryParsing.LinkSortFields, naturalSort[0].Field);

        if (perPage == 0)
        {
            var all = await ctx.Uow.Links.ListDtosAsync(
                new LinkQuerySpec { Filter = filter, Sort = sort, Page = new PageSpec(1, 0) }, ct);
            return CommandResult.Ok(new SmartListPageDto { TotalCount = all.Count, Items = all.ToList() });
        }

        // COUNT 先走（索引覆盖），首页沿排序列取——首帧只付「计数 + 64 行」，
        // 不把十万行 DTO 留在 UI 线程上等。
        var total = await ctx.Uow.Links.CountAsync(filter, ct);
        var rows = await ctx.Uow.Links.ListDtosAsync(
            new LinkQuerySpec { Filter = filter, Sort = sort, Page = new PageSpec(page, perPage) }, ct);
        return CommandResult.Ok(new SmartListPageDto { TotalCount = total, Items = rows.ToList() });
    }
}

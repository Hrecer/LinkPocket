using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.smart_list（Query）：四类智能列表预设（与既有 GetSmartListAsync 口径一致）。</summary>
internal sealed class LinkSmartListHandler : ICommandHandler
{
    private const int DefaultDays = 7;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.smart_list",
        Category: "links",
        Description: "Smart list presets: recently_added | recently_visited | recently_edited | most_visited",
        Parameters:
        [
            ParamSpec.Req<string>("kind", "recently_added | recently_visited | recently_edited | most_visited",
                enumValues: ["recently_added", "recently_visited", "recently_edited", "most_visited"]),
            ParamSpec.Opt<int>("limit", "Maximum rows returned; 0 or omitted = all matching rows"),
            ParamSpec.Opt<int>("days", "Time window in days, only for the recently_* presets (default 7)"),
        ],
        Caps: CommandCaps.Query,
        // 智能列表只查 links 表且结果只受链接表变更影响（recently_* 三键都有索引，缓存省掉重复排序）
        Cache: CachePolicy.Of(10, DomainEventNames.LinksChanged),
        Client: new("LinkSmartListAsync", typeof(List<LinkDto>)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var kind = CommandArgs.RequireString(args, "kind");
        // 智能列表 = 筛选语义（时间窗 / 访问过），不是分页视图：缺省返回**全部命中**。
        // 「前 N」只由调用方显式传 limit 表达（界面只对「最常查看（前 20）」这样做）。
        var limit = Math.Max(0, CommandArgs.OptionalInt(args, "limit", 0));
        var days = Math.Max(1, CommandArgs.OptionalInt(args, "days", DefaultDays));
        var since = DateTime.UtcNow.AddDays(-days);
        var ct = ctx.Ct;

        LinkFilter filter;
        IReadOnlyList<SortSpec> sort;
        switch (kind)
        {
            case "recently_added":
                filter = new LinkFilter { CreatedFrom = since };
                sort = [new SortSpec("created_at", SortDir.Desc)];
                break;
            case "recently_visited":
                filter = new LinkFilter { LastVisitedFrom = since };
                sort = [new SortSpec("last_visited_at", SortDir.Desc)];
                break;
            case "recently_edited":
                filter = new LinkFilter { UpdatedFrom = since };
                sort = [new SortSpec("updated_at", SortDir.Desc)];
                break;
            case "most_visited":
                filter = new LinkFilter { VisitCountMin = 1 };
                sort = [new SortSpec("visit_count", SortDir.Desc), new SortSpec("last_visited_at", SortDir.Desc)];
                break;
            default:
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"unknown smart list kind: {kind}", correlationId: ctx.CorrelationId));
        }

        // 直出 DTO（不做实体物化再 ToDto）：十万行智能列表省一段逐行映射，也少一层临时对象。
        var items = await ctx.Uow.Links.ListDtosAsync(
            new LinkQuerySpec { Filter = filter, Sort = sort, Page = new PageSpec(1, limit) }, ct);
        return CommandResult.Ok(items);
    }
}

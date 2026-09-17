using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.smart_list（Query）：四类智能列表预设（与既有 GetSmartListAsync 口径一致）。</summary>
internal sealed class LinkSmartListHandler : ICommandHandler
{
    private const int DefaultDays = 7;
    private const int DefaultLimit = 50;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.smart_list",
        Category: "links",
        Description: "智能列表预设：recently_added（最近添加）| recently_visited（最近查看）| recently_edited（最近编辑）| most_visited（最常访问，上限 20 语义）",
        Parameters:
        [
            ParamSpec.Req<string>("kind", "recently_added | recently_visited | recently_edited | most_visited"),
            ParamSpec.Opt<int>("limit", "上限（缺省 50）"),
        ],
        Caps: CommandCaps.Query,
        // 智能列表只查 links 表且结果只受链接表变更影响（7.2：recently_* 三键都有索引，缓存省掉重复排序）
        Cache: CachePolicy.Of(10, DomainEventNames.LinksChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var kind = CommandArgs.RequireString(args, "kind");
        var limit = Math.Max(1, CommandArgs.OptionalInt(args, "limit", DefaultLimit));
        var since = DateTime.UtcNow.AddDays(-DefaultDays);
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
                    EngineErrors.EnumOutOfRange, $"未知智能列表类型：{kind}", correlationId: ctx.CorrelationId));
        }

        var items = await ctx.Uow.Links.ListAsync(
            new LinkQuerySpec { Filter = filter, Sort = sort, Page = new PageSpec(1, limit) }, ct);
        return CommandResult.Ok(items.Select(l => l.ToDto()).ToList());
    }
}

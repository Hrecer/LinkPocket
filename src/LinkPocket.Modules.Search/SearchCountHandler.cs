using LinkPocket.Kernel;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>
/// search.count（Query）：与 search.links **同谓词**的命中总数（COUNT 下推，不物化实体、不过管道）。
/// 为什么要单独一条：界面只渲染前几百条（search.links 的 per_page），但"命中 N 条 / 截断提示"要总数——
/// 让主查询退回全量物化只为取个数，在十万级命中下是实测的 ~800ms 纯浪费（12MB JSON 过管道）。
/// 参数与 search.links 的范围/谓词部分完全一致（排序/分页参数与计数无关，不收）。
/// </summary>
internal sealed class SearchCountHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.count",
        Category: "search",
        Description: "Count links matching the search predicate (same scopes as search.links: title/url/description/path)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
        ],
        Caps: CommandCaps.Query,
        // 与 search.links 同一套失效事件；同参重复计数不该重扫
        Cache: CachePolicy.Of(10, DomainEventNames.LinksChanged, DomainEventNames.FoldersChanged, DomainEventNames.TrashChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");

        var count = await SearchSupport.CountAsync(
            ctx.Uow, query,
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            ctx.Ct);

        return CommandResult.Ok(count);
    }
}

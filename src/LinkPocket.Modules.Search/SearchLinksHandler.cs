using LinkPocket.Kernel;
using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>search.links（Query）：四范围组合搜索（与既有 SearchAsync 行为等价；空查询 = LP.VAL.001）。</summary>
internal sealed class SearchLinksHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.links",
        Category: "search",
        Description: "搜索链接（四范围：title/url/description/path；排序缺省 title 升序）",
        Parameters:
        [
            ParamSpec.Req<string>("query", "搜索关键词"),
            ParamSpec.Opt<bool>("search_title", "搜标题（缺省 true）"),
            ParamSpec.Opt<bool>("search_url", "搜地址"),
            ParamSpec.Opt<bool>("search_description", "搜描述"),
            ParamSpec.Opt<bool>("search_path", "搜位置（文件夹名命中 → 子树展开）"),
            ParamSpec.Opt<string>("sort_by", "title | updated_at | last_visited_at | visit_count | created_at"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001（引导空态是界面职责）

        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "title";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "asc";

        var hits = await SearchSupport.MatchAsync(
            ctx.Uow, query,
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            ctx.Ct);

        var sorted = SearchSupport.SortHits(hits, h => h.Link, sortBy, sortOrder);
        return CommandResult.Ok(sorted.Select(h => h.Link.ToDto()).ToList());
    }
}

using LinkPocket.Kernel;
using System.Text.Json;
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
        Description: "Search links (four scopes: title/url/description/path; default sort title ascending)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
            // 与 QueryParsing.LinkSortFields / EfSortEngine.LinkFields 全量对齐（7 字段；枚举引用同一事实源）
            ParamSpec.Opt<string>("sort_by", "title | url | created_at | updated_at | last_visited_at | visit_count | is_important",
                enumValues: QueryParsing.LinkSortFieldNames),
            ParamSpec.Opt<string>("sort_order", "asc | desc", enumValues: ["asc", "desc"]),
            // 外部命令默认全量：不传 / 0 / 负数 = 全量；显式正数 = 调用方主动分页。
            // 界面走 search.session_* 的有界快照会话，不依赖本命令的分页。
            ParamSpec.Opt<int>("page", "Page index (1-based, default 1; ignored when per_page is 0)"),
            ParamSpec.Opt<int>("per_page", "Items per page (default 0 = all results; positive = page size)"),
        ],
        Caps: CommandCaps.Query,
        // 默认全量会产生很大的结果，且外部调用方要求准确优先——不做进程内缓存。
        Client: new("SearchLinksAsync", typeof(List<LinkDto>)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001（引导空态是界面职责）

        // 不再 `?? "title"` 挡默认——缺省/空串/非法值统一由 ParseSort 落回 fallback "title"
        //（此前 `sort_by: ""` 会绕过 ?? 落到 ParseSort 的 created_at，与文档「缺省 title 升序」相悖）
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? string.Empty;
        var sortOrder = CommandArgs.OptionalString(args, "sort_order");
        var (page, perPage) = SearchSupport.ResolveExternalPage(
            CommandArgs.OptionalInt(args, "page", 1),
            CommandArgs.OptionalInt(args, "per_page", 0));

        var hits = await SearchSupport.SearchAsync(
            ctx.Uow, query,
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            sortBy,
            QueryParsing.NormalizeOrder(sortOrder),
            ctx.Ct,
            page,
            perPage);

        return CommandResult.Ok(hits.Select(h => h.Link).ToList());
    }
}

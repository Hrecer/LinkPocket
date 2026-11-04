using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>
/// search.explain（★ 引擎能力，不接 UI）：报告每条命中的具体字段——AI 自校验搜索结果用。
/// <para>
/// 外部命令默认**全量**：不传 / 0 / 负数 = 全部命中；显式正数 = 调用方主动分页。
/// 全量时 Total = 本次返回条数、Truncated = false；分页时 Total 走同谓词 COUNT，
/// Truncated 表示后面还有页。结果始终带 Total/Truncated/Page/PerPage。
/// </para>
/// </summary>
internal sealed class SearchExplainHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.explain",
        Category: "search",
        Description: "Explain search hits (matched fields per link; default all results, total included)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
            ParamSpec.Opt<int>("page", "Page index (1-based, default 1; ignored when per_page is 0)"),
            ParamSpec.Opt<int>("per_page", "Items per page (default 0 = all results; positive = page size)"),
        ],
        Caps: CommandCaps.Query,
        // 默认全量会产生很大的结果，且外部调用方要求准确优先——不做进程内缓存。
        Client: new("SearchExplainAsync", typeof(SearchExplanation)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001
        var searchTitle = CommandArgs.OptionalBool(args, "search_title", true);
        var searchUrl = CommandArgs.OptionalBool(args, "search_url");
        var searchDescription = CommandArgs.OptionalBool(args, "search_description");
        var searchPath = CommandArgs.OptionalBool(args, "search_path");
        var (page, perPage) = SearchSupport.ResolveExternalPage(
            CommandArgs.OptionalInt(args, "page", 1),
            CommandArgs.OptionalInt(args, "per_page", 0));
        var full = perPage == 0;

        // 排序固定 title 升序（explain 只关心命中字段）。
        // ⚠️ 同一命令内 ctx.Uow 是**同一个 DbContext**：两查询不能并行（EF 会拒绝同一上下文的并发操作）。
        var hits = await SearchSupport.SearchAsync(
            ctx.Uow, query, searchTitle, searchUrl, searchDescription, searchPath,
            "title", "asc", ctx.Ct, page, perPage);
        var total = full
            ? hits.Count
            : await SearchSupport.CountAsync(
                ctx.Uow, query, searchTitle, searchUrl, searchDescription, searchPath, ctx.Ct);
        var explanation = new SearchExplanation(
            hits.Select(h => new SearchHit(h.Link.LinkId, h.Matched)).ToList(),
            total,
            !full && total > (long)page * perPage,
            full ? 1 : page,
            full ? hits.Count : perPage);
        return CommandResult.Ok(explanation);
    }
}

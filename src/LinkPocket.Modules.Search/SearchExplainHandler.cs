using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>search.explain（★ 引擎能力，不接 UI）：报告每条命中的具体字段——AI 自校验搜索结果用。</summary>
internal sealed class SearchExplainHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.explain",
        Category: "search",
        Description: "Explain search hits: return the matched fields of each hit link (title/url/description/path)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001

        // 排序固定 title 升序（explain 只关心命中字段，不翻页不排序——
        // 与 search.links 的对象集合可能不同序，调用方不得按位置对齐，见 MODULE.md）
        var hits = await SearchSupport.SearchAsync(
            ctx.Uow, query,
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            "title", "asc",
            ctx.Ct);

        var explanation = new SearchExplanation(hits
            .Select(h => new SearchHit(h.Link.LinkId, h.Matched))
            .ToList());
        return CommandResult.Ok(explanation);
    }
}

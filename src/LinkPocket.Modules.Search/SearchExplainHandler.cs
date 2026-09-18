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
        Description: "解释搜索命中：返回每个命中链接命中的字段列表（title/url/description/path）",
        Parameters:
        [
            ParamSpec.Req<string>("query", "搜索关键词"),
            ParamSpec.Opt<bool>("search_title", "搜标题（缺省 true）"),
            ParamSpec.Opt<bool>("search_url", "搜地址"),
            ParamSpec.Opt<bool>("search_description", "搜描述"),
            ParamSpec.Opt<bool>("search_path", "搜位置"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001

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

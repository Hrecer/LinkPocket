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
            ParamSpec.Opt<bool>("search_url", "搜 URL"),
            ParamSpec.Opt<bool>("search_description", "搜描述"),
            ParamSpec.Opt<bool>("search_path", "搜路径（文件夹名命中 → 子树展开）"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001

        // 排序固定 title 升序（审核 4.6：explain 只关心命中字段，不翻页不排序——
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

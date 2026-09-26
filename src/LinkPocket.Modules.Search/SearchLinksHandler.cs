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
            // 分页（可选；per_page 缺省 0 = 全量，向后兼容）。命中总数用 search.count（同谓词 COUNT，
            // 不物化实体不过管道），界面"命中 N 条 / 截断提示"由它供数。
            ParamSpec.Opt<int>("page", "Page index (1-based, default 1)"),
            ParamSpec.Opt<int>("per_page", "Items per page (0 = all, default 0)"),
        ],
        Caps: CommandCaps.Query,
        // 四个范围都是 `LIKE '%…%'`（子串匹配，索引帮不上忙，只能全表扫）——这条查询在界面上
        // 会被反复触发（范围热切换、事件刷新、防抖补跑），同一次数据状态下的重复调用不该各扫一遍。
        // 缓存键含参数；依赖事件 = 链接/文件夹/回收站变化，写操作照旧立即失效（不牺牲"不展示过期结果"）。
        Cache: CachePolicy.Of(10, DomainEventNames.LinksChanged, DomainEventNames.FoldersChanged, DomainEventNames.TrashChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var query = CommandArgs.RequireString(args, "query");   // 空查询 = LP.VAL.001（引导空态是界面职责）

        // 不再 `?? "title"` 挡默认——缺省/空串/非法值统一由 ParseSort 落回 fallback "title"
        //（此前 `sort_by: ""` 会绕过 ?? 落到 ParseSort 的 created_at，与文档「缺省 title 升序」相悖）
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? string.Empty;
        var sortOrder = CommandArgs.OptionalString(args, "sort_order");

        var hits = await SearchSupport.SearchAsync(
            ctx.Uow, query,
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            sortBy,
            QueryParsing.NormalizeOrder(sortOrder),
            ctx.Ct,
            CommandArgs.OptionalInt(args, "page", 1),
            CommandArgs.OptionalInt(args, "per_page", 0));

        return CommandResult.Ok(hits.Select(h => h.Link.ToDto()).ToList());
    }
}

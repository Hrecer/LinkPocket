using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>
/// search.session_open / page / close：把一次搜索的 count 与全部分页钉在**同一个 SQLite 读事务**里。
/// 这是界面“总数先定 + 占位 + 原位填充”的严格一致性来源；外部仍可继续用 search.links/count。
/// </summary>
internal sealed class SearchSessionOpenHandler(EngineLimits limits, SearchSessionStore store) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.session_open",
        Category: "search",
        Description: "Open a search snapshot session (stable count + first page under one SQLite read transaction)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
            ParamSpec.Opt<string>("sort_by", "title | url | created_at | updated_at | last_visited_at | visit_count | is_important"),
            ParamSpec.Opt<string>("sort_order", "asc | desc", enumValues: ["asc", "desc"]),
            ParamSpec.Opt<int>("first_page_size", "Rows returned with the session (default 0; 0 = none)"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var requested = CommandArgs.OptionalInt(args, "first_page_size", 0);
        var firstPageSize = requested <= 0 ? 0 : Math.Min(requested, limits.MaxPageSize);
        var result = await store.OpenAsync(
            CommandArgs.RequireString(args, "query"),
            CommandArgs.OptionalBool(args, "search_title", true),
            CommandArgs.OptionalBool(args, "search_url"),
            CommandArgs.OptionalBool(args, "search_description"),
            CommandArgs.OptionalBool(args, "search_path"),
            CommandArgs.OptionalString(args, "sort_by") ?? string.Empty,
            QueryParsing.NormalizeOrder(CommandArgs.OptionalString(args, "sort_order")),
            firstPageSize,
            ctx.Ct);
        return CommandResult.Ok(result);
    }
}

internal sealed class SearchSessionPageHandler(EngineLimits limits, SearchSessionStore store) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.session_page",
        Category: "search",
        Description: "Read one page from a search snapshot session (same snapshot as its count)",
        Parameters:
        [
            ParamSpec.Req<string>("session_id", "Session id from search.session_open"),
            ParamSpec.Opt<int>("page", "Page index (1-based, default 1)"),
            ParamSpec.Opt<int>("per_page", "Items per page (default 500; 0 = engine max 10000)"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var (page, perPage) = SearchSupport.ResolvePage(
            CommandArgs.OptionalInt(args, "page", 1),
            CommandArgs.OptionalInt(args, "per_page", SearchSupport.DefaultPageSize),
            limits);
        var rows = await store.PageAsync(
            CommandArgs.RequireString(args, "session_id"), page, perPage, ctx.Ct);
        return CommandResult.Ok(rows);
    }
}

internal sealed class SearchSessionCloseHandler(SearchSessionStore store) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.session_close",
        Category: "search",
        Description: "Close a search snapshot session early (idempotent; otherwise expires after a short TTL)",
        Parameters: [ParamSpec.Req<string>("session_id", "Session id from search.session_open")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var closed = await store.CloseAsync(CommandArgs.RequireString(args, "session_id"));
        return CommandResult.Ok(closed);
    }
}

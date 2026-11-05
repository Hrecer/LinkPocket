using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Search;

/// <summary>
/// search.reconcile（Query，界面专用）：只对**已变更的链接 ID**重新判定命中并返回最新 DTO。
/// 与 search.session_open 使用同一套范围/谓词；不排序、不分页——排序位置由持有完整结果集的
/// 界面按引擎同一套排序键计算（见 SearchViewModel 的活结果集）。
///
/// <para><b>为什么需要它</b>：界面收到精确变更（进程内 ChangeSet.Touched / 外部变更流的 Touched）后，
/// 不能拿旧 DTO 猜命中，也不能为一行改动重扫全量。`IdIn + SearchScope` 走主键限定候选，
/// 成本与变更数成正比；单行改名/移动/删除不再重建十万行。</para>
///
/// <para><b>降级</b>：变更 ID 超过 <see cref="MaxReconcileIds"/> 时直接要求全量刷新——
/// 大批量逐行对账的收益低于一次快照，且能避免 IPC/内存放大。外部宿主不注册本命令。</para>
/// </summary>
internal sealed class SearchReconcileHandler : ICommandHandler
{
    /// <summary>单次增量对账的 ID 上限；超出 = 一次全量快照更便宜。</summary>
    public const int MaxReconcileIds = 512;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "search.reconcile",
        Category: "search",
        Description: "Re-evaluate changed link IDs against a search predicate (UI live-result reconciliation)",
        Parameters:
        [
            ParamSpec.Req<string>("query", "Search keyword"),
            ParamSpec.Opt<bool>("search_title", "Search titles (default true)"),
            ParamSpec.Opt<bool>("search_url", "Search URLs"),
            ParamSpec.Opt<bool>("search_description", "Search descriptions"),
            ParamSpec.Opt<bool>("search_path", "Search paths (a folder-name hit expands its subtree)"),
            ParamSpec.Opt<IReadOnlyList<string>>("link_ids", "Changed link IDs to reconcile"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var ids = CommandArgs.StringArray(args, "link_ids")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return CommandResult.Ok(new SearchReconcileResult([], NeedsFullRefresh: false));
        if (ids.Length > MaxReconcileIds)
            return CommandResult.Ok(new SearchReconcileResult([], NeedsFullRefresh: true));

        var query = CommandArgs.RequireString(args, "query");
        var searchTitle = CommandArgs.OptionalBool(args, "search_title", true);
        var searchUrl = CommandArgs.OptionalBool(args, "search_url");
        var searchDesc = CommandArgs.OptionalBool(args, "search_description");
        var searchPath = CommandArgs.OptionalBool(args, "search_path");

        // 与快照会话同一套目录命中/范围构造：路径范围下，链接是否仍是命中取决于当前目录位置。
        FolderNameSearch? folderHits = null;
        if (searchPath && !string.IsNullOrWhiteSpace(query))
            folderHits = await ctx.Uow.Folders.SearchByNameAsync(query, includeDescendants: true, sortBy: null, ctx.Ct);

        var scope = SearchSupport.BuildScope(query, searchTitle, searchUrl, searchDesc, searchPath, folderHits);
        if (!scope.Any)
            return CommandResult.Ok(new SearchReconcileResult([], NeedsFullRefresh: false));

        var matched = await ctx.Uow.Links.ListDtosAsync(new LinkQuerySpec
        {
            Filter = new LinkFilter { SearchScope = scope, IdIn = ids },
            Page = new PageSpec(1, 0),
        }, ctx.Ct);

        return CommandResult.Ok(new SearchReconcileResult(matched, NeedsFullRefresh: false));
    }
}

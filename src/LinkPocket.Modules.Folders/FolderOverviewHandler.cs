using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.overview（Query）：浏览页主视图专用组合快照 —— 目录页 + 全量文件夹树 + 根级链接数，
/// 三次消费一次取齐、同一读池 UoW（单快照：contents/tree/root 计数不再跨命令漂移）。
/// 参数与 folders.contents 完全一致；响应 = contentsDTO + tree + root_link_count。
/// 消费者：浏览页 RefreshAsync（原三条命令 → 一条）。
/// </summary>
internal sealed class FolderOverviewHandler(EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.overview",
        Category: "folders",
        Description: "A snapshot matching the browser main view: folder page (contents) + full folder tree + root link count + all links (injected as tree leaves; parameters as folders.contents)",
        Parameters:
        [
            ParamSpec.Opt<string>("folder_id", "Folder ID; default = root (the root is not an entity and has no ID)"),
            // 双列表排序（链接走 LinkSortFieldNames、子文件夹走 FolderSupport.SortFieldNames），枚举 = 两者并集
            ParamSpec.Opt<string>("sort_by", "Sort field: title | url | created_at | updated_at | last_visited_at | visit_count | is_important (links) / name | sort_order | created_at | updated_at | last_visited_at | visit_count (child folders)",
                enumValues: [.. QueryParsing.LinkSortFieldNames, .. FolderSupport.SortFieldNames]),
            ParamSpec.Opt<string>("sort_order", "asc | desc", enumValues: ["asc", "desc"]),
            ParamSpec.Opt<int>("page", "Page number (1-based)"),
            ParamSpec.Opt<int>("per_page", "Links per page; 0 = everything (bounded by the engine cap, truncated = true when capped)"),
        ],
        Caps: CommandCaps.Query,
        // 依赖事件 = 目录页与树（folders.changed）+ 根级计数（links.changed）；两路失效与组成命令一致
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var dto = await FolderViewCore.BuildAsync(ctx, args, limits, withTreeLinks: true);
        return CommandResult.Ok(dto);
    }
}
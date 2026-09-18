using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.overview（Query）：浏览页主视图专用组合快照 —— 目录页 + 全量文件夹树 + 根级链接数，
/// 三次消费一次取齐、同一读池 UoW（2.10-45 单快照：contents/tree/root 计数不再跨命令漂移）。
/// 参数与 folders.contents 完全一致；响应 = contentsDTO + tree + root_link_count。
/// 消费者：浏览页 RefreshAsync（原三条命令 → 一条）。
/// </summary>
internal sealed class FolderOverviewHandler(EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.overview",
        Category: "folders",
        Description: "浏览页主视图一致的快照：目录页(contents) + 全量文件夹树 + 根级链接数（参数同 folders.contents）",
        Parameters:
        [
            ParamSpec.Opt<string>("folder_id", "目录 ID；缺省 = 根「全部书签」（根不是实体、无 ID）"),
            ParamSpec.Opt<string>("sort_by", "排序字段：title | updated_at | last_visited_at | visit_count | created_at（链接）/ name | sort_order | updated_at | last_visited_at | visit_count | created_at（子文件夹）"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
            ParamSpec.Opt<int>("page", "页码（从 1 起）"),
            ParamSpec.Opt<int>("per_page", "每页链接数；0 = 全量（受引擎上限约束，触限时 truncated = true）"),
        ],
        Caps: CommandCaps.Query,
        // 依赖事件 = 目录页与树（folders.changed）+ 根级计数（links.changed）；两路失效与组成命令一致
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var dto = await FolderViewCore.BuildAsync(ctx, args, limits);
        return CommandResult.Ok(dto);
    }
}
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.contents（Query）：目录页 = 子文件夹 + 链接 + 面包屑 + 计数。
/// 实际逻辑在 <see cref="FolderViewCore.BuildAsync"/>（与 folders.overview 共用同一 UoW 单快照）；
/// 本命令契约：<b>响应形状不变</b>（tree/root_link_count 恒为 null，不参与序列化）。
/// </summary>
internal sealed class FolderContentsHandler(EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.contents",
        Category: "folders",
        Description: "取一个目录页：直接子文件夹 + 直接子链接 + 面包屑路径（folder_id 缺省 = 根「全部书签」）",
        Parameters:
        [
            ParamSpec.Opt<string>("folder_id", "目录 ID；缺省 = 根「全部书签」（根不是实体、无 ID）"),
            ParamSpec.Opt<string>("sort_by", "排序字段：title | updated_at | last_visited_at | visit_count | created_at（链接）/ name | sort_order | updated_at | last_visited_at | visit_count | created_at（子文件夹）"),
            ParamSpec.Opt<string>("sort_order", "asc | desc"),
            ParamSpec.Opt<int>("page", "页码（从 1 起）"),
            ParamSpec.Opt<int>("per_page", "每页链接数；0 = 全量（受引擎上限约束，触限时 truncated = true）"),
        ],
        Caps: CommandCaps.Query,
        // 目录页 = 4 次查询（文件夹全量 + 计数两口径 + 链接），UI 每次刷新/导航都要；
        // 结果只受「文件夹/链接变更」影响 → 内容类缓存（事件驱动失效）
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        // 契约：folders.contents 的响应形状不新增字段（组合视图数据只由 folders.overview 交付）
        var dto = await FolderViewCore.BuildAsync(ctx, args, limits);
        dto.Tree = null;
        dto.RootLinkCount = null;
        dto.TreeLinks = null;
        return CommandResult.Ok(dto);
    }
}
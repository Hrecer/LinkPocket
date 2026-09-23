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
        Description: "Get one folder page: direct child folders + direct child links + breadcrumb path (folder_id default = root)",
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
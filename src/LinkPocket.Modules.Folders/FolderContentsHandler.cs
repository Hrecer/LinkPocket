using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.contents（Query）：目录页 = 子文件夹 + 链接 + 面包屑 + 计数。
/// 链接列表的**排序与分页全部 SQL 下推**（不再全量拉进内存排序后再 Skip/Take）；
/// 页大小受 <see cref="EngineLimits.MaxPageSize"/> 约束，触限时响应带 <c>truncated</c> 标志（绝不静默截断）。
/// 子文件夹/链接排序口径一致；根目录只显示根级书签；面包屑含根显示名。
/// </summary>
internal sealed class FolderContentsHandler(EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.contents",
        Category: "folders",
        Description: "取一个目录页：直接子文件夹 + 直接子链接 + 面包屑路径（folder_id 缺省 = 根「全部书签」）",
        Parameters:
        [
            ParamSpec.Opt<string>("folder_id", "目录 ID；缺省或 \"0\" = 根"),
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
        var folderId = FolderIds.Normalize(CommandArgs.OptionalString(args, "folder_id"));
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "title";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "asc";
        var page = Math.Max(1, CommandArgs.OptionalInt(args, "page", 1));
        var perPage = Math.Max(0, CommandArgs.OptionalInt(args, "per_page", 0));
        var ct = ctx.Ct;

        // 页大小：0 = 调用方要全量 → 按引擎上限取；显式值超上限 → 夹到上限。
        // 两种情况都必须在响应里可见（truncated），绝不静默截断。
        var wantsAll = perPage == 0;
        var effectivePerPage = wantsAll ? limits.MaxPageSize : Math.Min(perPage, limits.MaxPageSize);
        var exceedsLimit = !wantsAll && perPage > limits.MaxPageSize;

        var isRoot = FolderIds.IsRoot(folderId);
        var allFolders = await ctx.Uow.Folders.ListAllAsync(ct);
        var counts = await ctx.Uow.Trees.LinkCountsAsync(ct);

        // 排序下推：字段白名单 + ID 兜底（「最后查看」为空的恒排最后由排序引擎统一表达）
        var sort = QueryParsing.ParseSort(
            sortBy, QueryParsing.NormalizeOrder(sortOrder), QueryParsing.LinkSortFields, "created_at");

        var dto = new FolderContentsDto { FolderId = folderId, PerPage = effectivePerPage };
        List<FolderDto> SortFolders(IEnumerable<Folder> source)
            => FolderSupport.SortFolders(source.Select(f => f.ToDto(counts)), sortBy, sortOrder);

        if (isRoot)
        {
            dto.FolderName = FolderIds.RootDisplayName;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == null));
            dto.DirectLinkCount = await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct);
            dto.Truncated = exceedsLimit || (wantsAll && dto.DirectLinkCount > effectivePerPage);
            dto.Links = (await ctx.Uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { Unfiled = true },
                Sort = sort,
                Page = new PageSpec(1, effectivePerPage),
            }, ct)).Select(l => l.ToDto()).ToList();
            dto.CurrentPage = 1;
            dto.LastPage = 1;
            dto.Breadcrumb = [FolderIds.RootDisplayName];
        }
        else
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"文件夹 {folderId} 不存在", correlationId: ctx.CorrelationId));
            dto.FolderName = folder.Name;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == folderId));
            dto.DirectLinkCount = counts.Direct.TryGetValue(new FolderId(folderId!), out var direct) ? direct : 0;
            dto.Truncated = exceedsLimit || (wantsAll && dto.DirectLinkCount > effectivePerPage);
            dto.Links = (await ctx.Uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { FolderId = new FolderId(folderId!) },
                Sort = sort,
                Page = new PageSpec(page, effectivePerPage),
            }, ct)).Select(l => l.ToDto()).ToList();
            dto.CurrentPage = page;
            // 总页数由直接子链接计数推出（SQL 端 COUNT，无需把全部行拉回来数）
            dto.LastPage = perPage > 0
                ? (int)Math.Ceiling(dto.DirectLinkCount / (double)effectivePerPage)
                : 1;
            dto.Breadcrumb = FolderSupport.BuildBreadcrumb(folder, allFolders);
        }

        return CommandResult.Ok(dto);
    }
}
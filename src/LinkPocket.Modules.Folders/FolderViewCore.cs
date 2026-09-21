using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// 目录页视图的共享构建核心（folders.contents / folders.overview 共用）：
/// 一次调用内完成"目录页 + 全量Folder树 + 根级链接数"，全部位于同一个读池短 UoW
/// （同一 DbContext/连接）——三条命令各自独立时点的问题收敛为<b>命令内单快照</b>。
/// folders.contents 交付时清空 tree/root_link_count 保持原响应形状；folders.overview 全量交付。
/// </summary>
internal static class FolderViewCore
{
    public static async Task<FolderContentsDto> BuildAsync(ICommandContext ctx, JsonElement args, EngineLimits limits,
        bool withTreeLinks = false)
    {
        var folderId = CommandArgs.OptionalString(args, "folder_id");
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
        // 全量文件夹 + 计数两口径 + 链接：同一 UoW，读者见提交边界的一致快照
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
            dto.FolderName = FolderIds.RootToken;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == null));
            dto.DirectLinkCount = await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct);
            dto.Truncated = exceedsLimit || (wantsAll && dto.DirectLinkCount > effectivePerPage);
            dto.Links = (await ctx.Uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { Unfiled = true },
                Sort = sort,
                Page = new PageSpec(page, effectivePerPage),
            }, ct)).Select(l => l.ToDto()).ToList();
            dto.CurrentPage = page;
            dto.LastPage = perPage > 0
                ? (int)Math.Ceiling(dto.DirectLinkCount / (double)effectivePerPage)
                : 1;
            dto.Breadcrumb = [FolderIds.RootToken];
        }
        else
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"folder {folderId} does not exist", correlationId: ctx.CorrelationId));
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

        // —— overview 附加数据（同一 UoW，单快照）——
        // 树 = 全量文件夹平铺（名称升序，folders.tree 缺省同构）；根级链接数与 links.stats.RootLevel 同口径。
        dto.Tree = FolderSupport.SortFolders(allFolders.Select(f => f.ToDto(counts)), "name", "asc");
        dto.RootLinkCount = isRoot
            ? dto.DirectLinkCount                                // 根分支已算过根级数，直接复用
            : await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct);

        // 全库活动链接（树叶子注入数据源）：每链接携带 list_id 归属目录（null = 根级），
        // UI 按父目录分组后把直接链接叶子挂到对应文件夹节点下；同一 UoW 单快照（不跨命令漂移），
        // 名称升序 + ID 兜底 = 与树同口径的确定性输出（size 0 = 全量）。
        // 仅 folders.overview 需要（withTreeLinks=true）：folders.contents 是 10k 库性能门槛命令，
        // 不为它付全量链接代价（响应形状本来就恒 null）。
        if (withTreeLinks)
        {
            dto.TreeLinks = (await ctx.Uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter(),
                Sort = new[] { new SortSpec("title", SortDir.Asc) },
                Page = new PageSpec(1, 0),
            }, ct)).Select(l => l.ToDto()).ToList();
        }

        return dto;
    }
}
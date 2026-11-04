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
/// folders.contents 交付时清空 tree/root_link_count 保持原响应形状；folders.overview 默认全量交付，
/// <c>include_tree=false</c>（界面已有树）时走定向快路径：只取当前目录 + 直接子目录 + 本层计数。
/// </summary>
internal static class FolderViewCore
{
    public static async Task<FolderContentsDto> BuildAsync(
        ICommandContext ctx, JsonElement args, bool withTreeLinks = false, bool withBreadcrumb = true,
        bool withTree = true)
    {
        var folderId = CommandArgs.OptionalString(args, "folder_id");
        var sortBy = CommandArgs.OptionalString(args, "sort_by") ?? "title";
        var sortOrder = CommandArgs.OptionalString(args, "sort_order") ?? "asc";
        var page = Math.Max(1, CommandArgs.OptionalInt(args, "page", 1));
        var perPage = Math.Max(0, CommandArgs.OptionalInt(args, "per_page", 0));
        var ct = ctx.Ct;

        // 外部目录命令默认全量：0 = 全部（PageSpec.Size 0 → SQL 无 LIMIT）；
        // 显式正数 = 调用方主动分页。引擎不再用 MaxPageSize 静默截断，准确优先。
        var effectivePerPage = perPage;
        var effectivePage = effectivePerPage == 0 ? 1 : page;

        var isRoot = FolderIds.IsRoot(folderId);
        // 全量文件夹 + 计数两口径 + 链接：同一 UoW，读者见提交边界的一致快照。
        // ⚠️ include_tree=false（界面已有新树）：**不读全量目录表**——十万目录时这一条 ≈410ms，
        // 且整棵树的 DTO 还要序列化过 IPC。目录页只需要「当前目录 + 它的子目录」，
        // 计数也只算子目录这两口径；面包屑这种需要全量父链的形态仍回退到全量路径。
        var fastPage = !withTree && !withBreadcrumb && !withTreeLinks;
        FolderId? parentId = isRoot ? null : new FolderId(folderId!);
        IReadOnlyList<Folder> allFolders;
        FolderLinkCounts counts;
        if (fastPage)
        {
            allFolders = await ctx.Uow.Folders.ChildrenOfAsync(parentId, ct);
            counts = await ctx.Uow.Trees.LinkCountsForChildrenAsync(parentId, ct);
        }
        else
        {
            allFolders = await ctx.Uow.Folders.ListAllAsync(ct);
            counts = await ctx.Uow.Trees.LinkCountsAsync(ct);
        }

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
            dto.Links = (await ctx.Uow.Links.ListDtosAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { Unfiled = true },
                Sort = sort,
                Page = new PageSpec(effectivePage, effectivePerPage),
            }, ct)).ToList();
            dto.CurrentPage = effectivePage;
            dto.LastPage = effectivePerPage > 0
                ? Math.Max(1, (int)Math.Ceiling(dto.DirectLinkCount / (double)effectivePerPage))
                : 1;
            if (withBreadcrumb) dto.Breadcrumb = [FolderIds.RootToken];
        }
        else
        {
            var folder = fastPage
                ? await ctx.Uow.Folders.FindAsync(new FolderId(folderId!), ct)
                : allFolders.FirstOrDefault(f => f.FolderId == folderId);
            if (folder is null)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"folder {folderId} does not exist", correlationId: ctx.CorrelationId));
            dto.FolderName = folder.Name;
            dto.SubFolders = SortFolders(allFolders.Where(f => f.ParentId == folderId));
            // 快速路径的计数只覆盖「子目录」；当前目录自己的直接链接单独点查（索引 COUNT，0.1ms 级）。
            dto.DirectLinkCount = fastPage
                ? await ctx.Uow.Links.CountAsync(new LinkFilter { FolderId = new FolderId(folderId!) }, ct)
                : counts.Direct.TryGetValue(new FolderId(folderId!), out var direct) ? direct : 0;
            dto.Links = (await ctx.Uow.Links.ListDtosAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { FolderId = new FolderId(folderId!) },
                Sort = sort,
                Page = new PageSpec(effectivePage, effectivePerPage),
            }, ct)).ToList();
            dto.CurrentPage = effectivePage;
            // 总页数由直接子链接计数推出（SQL 端 COUNT，无需把全部行拉回来数）
            dto.LastPage = effectivePerPage > 0
                ? Math.Max(1, (int)Math.Ceiling(dto.DirectLinkCount / (double)effectivePerPage))
                : 1;
            if (withBreadcrumb) dto.Breadcrumb = FolderSupport.BuildBreadcrumb(folder, allFolders);
        }

        // —— overview 附加数据（同一 UoW，单快照）——
        // 树 = 全量文件夹平铺（名称升序，folders.tree 缺省同构）；根级链接数与 links.stats.RootLevel 同口径。
        // include_tree=false：树由界面自己持有并复用，本次不交付、也不付全量 DTO/序列化的钱。
        dto.Tree = fastPage
            ? null
            : FolderSupport.SortFolders(allFolders.Select(f => f.ToDto(counts)), "name", "asc");
        dto.RootLinkCount = fastPage
            ? null
            : isRoot
                ? dto.DirectLinkCount                            // 根分支已算过根级数，直接复用
                : await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct);

        // 外部 folders.overview 默认携带全库活动链接（树叶子数据源）；界面显式传
        // include_tree_links=false —— 浏览页左栏只显示文件夹，不再为每次刷新搬全库链接。
        // 这一项是外部命令的完整数据面，不能因为界面不用就删掉（准确优先）。
        if (withTreeLinks)
        {
            dto.TreeLinks = (await ctx.Uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter(),
                Sort = [new SortSpec("title", SortDir.Asc)],
                Page = new PageSpec(1, 0),
            }, ct)).Select(l => l.ToTreeDto()).ToList();
        }

        return dto;
    }
}
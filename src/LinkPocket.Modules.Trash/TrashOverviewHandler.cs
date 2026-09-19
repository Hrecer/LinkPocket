using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.overview（Query）：回收站页主视图专用组合快照 —— 全量单元（含子树计数与原位置）
/// + 全量书签快照（每项携归属单元 <c>trash_folder_id</c>，null = 根级单独删除），
/// 一次取齐、同一读池 UoW（树 / 主栏 / 面包屑不再跨命令漂移）。
/// 与 <c>folders.overview</c> 同一哲学：树叶子注入的数据源随树同快照返回。
/// </summary>
internal sealed class TrashOverviewHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.overview",
        Category: "trash",
        Description: "回收站页一致的快照：全量单元（子树计数 + 原位置）+ 全量书签快照（每项携归属单元）",
        Parameters: [],
        Caps: CommandCaps.Query,
        // 结构快照 = 两表全量（单元 + 链接，与 trash.tree / trash.list 同源）
        Cache: CachePolicy.Of(10, DomainEventNames.TrashChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var ct = ctx.Ct;
        var folders = await ctx.Uow.Trash.ListFoldersAsync(ct);
        var counts = await ctx.Uow.Trash.CountLinksByUnitAsync(ct);

        var folderDtos = new List<TrashFolderDto>();
        foreach (var f in folders)
        {
            var subtreeIds = TrashSupport.CollectSubtreeIds(folders, f.TrashFolderId);
            folderDtos.Add(new TrashFolderDto
            {
                TrashFolderId = f.TrashFolderId,
                ParentTrashFolderId = f.ParentTrashFolderId,
                Name = f.Name,
                LinkCount = subtreeIds.Sum(id => counts.GetValueOrDefault(new TrashFolderId(id))),
                DeletedAt = f.DeletedAt,
                OriginPath = f.OriginPath,
            });
        }

        var linkDtos = new List<TrashEntryDto>();
        foreach (var l in await ctx.Uow.Trash.ListStandaloneLinksAsync(ct))
            linkDtos.Add(ToLinkEntry(l, null));
        foreach (var unit in folders)
        {
            foreach (var l in await ctx.Uow.Trash.ListLinksByUnitAsync(new TrashFolderId(unit.TrashFolderId), ct))
                linkDtos.Add(ToLinkEntry(l, unit.TrashFolderId));
        }

        return CommandResult.Ok(new TrashOverviewDto { Folders = folderDtos, Links = linkDtos });
    }

    /// <summary>链接快照 → 条目 DTO（trash.list 同款命名口径：标题为空回落 URL）。</summary>
    private static TrashEntryDto ToLinkEntry(TrashedLink l, string? unitId) => new()
    {
        Id = l.LinkId,
        EntryType = TrashEntryType.Link,
        Name = string.IsNullOrEmpty(l.Title) ? l.Url : l.Title!,
        Url = l.Url,
        FaviconUrl = l.FaviconUrl,
        OriginPath = l.OriginPath,
        DeletedAt = l.DeletedAt,
        TrashFolderId = unitId,
    };
}

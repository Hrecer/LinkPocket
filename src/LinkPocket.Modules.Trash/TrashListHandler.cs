using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.list（Query）：回收站平铺列表（Windows 口径）——
/// 单独删除的书签 + 被删文件夹单元根，按删除时间倒序（行为等价项：平铺不含单元内部条目）。
/// </summary>
internal sealed class TrashListHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.list",
        Category: "trash",
        Description: "回收站平铺列表：单独删除的书签 + 被删文件夹单元根（按删除时间倒序）",
        Parameters: [],
        Caps: CommandCaps.Query,
        // 平铺列表只读回收站两表（回收站页每次刷新都取）
        Cache: CachePolicy.Of(10, DomainEventNames.TrashChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var entries = new List<TrashEntryDto>();

        var unitRoots = (await ctx.Uow.Trash.ListFoldersAsync(ctx.Ct))
            .Where(f => f.ParentTrashFolderId == null);
        foreach (var f in unitRoots)
        {
            entries.Add(new TrashEntryDto
            {
                Id = f.TrashFolderId,
                EntryType = "folder",
                Name = f.Name,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
            });
        }

        foreach (var t in await ctx.Uow.Trash.ListStandaloneLinksAsync(ctx.Ct))
        {
            entries.Add(new TrashEntryDto
            {
                Id = t.LinkId,
                EntryType = "link",
                Name = string.IsNullOrEmpty(t.Title) ? t.Url : t.Title!,
                Url = t.Url,
                FaviconUrl = t.FaviconUrl,
                OriginPath = t.OriginPath,
                DeletedAt = t.DeletedAt,
            });
        }

        return CommandResult.Ok(entries.OrderByDescending(e => e.DeletedAt).ToList());
    }
}

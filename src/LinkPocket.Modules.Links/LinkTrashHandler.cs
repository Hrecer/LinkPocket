using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.trash（Mutation）：把链接移入回收站（软删除，可恢复）。
/// 回收站保留原 ID + 位置快照（origin_list_id / origin_path）；原文件夹父链 Touch。
/// 删除确认在 UI 层（ConfirmDialog「将 X 移入回收站吗？」唯一入口）。
/// </summary>
internal sealed class LinkTrashHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.trash",
        Category: "links",
        Description: "把链接移入回收站（软删除，可恢复；回收站保留原 ID 与原位置快照）",
        Parameters: [ParamSpec.Req<string>("id", "链接 ID")],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: null,   // 逆向需旧值（落点 = 删除前位置）→ 由处理器回填，见下方 undo
        Impact: ImpactSummary.Link);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var ct = ctx.Ct;

        var link = await ctx.Uow.Links.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"链接 {id} 不存在", correlationId: ctx.CorrelationId));
        var originListId = link.ListId;

        // 快照入库 + 主表删除（同一工作单元 → 引擎单事务原子提交）
        var snapshot = new TrashedLink
        {
            LinkId = link.LinkId,   // 回收站保留原 ID（2026-09-17 定稿）
            Url = link.Url,
            Title = link.Title,
            Description = link.Description,
            FaviconUrl = link.FaviconUrl,
            TrashFolderId = null,   // 单独删除的书签挂在回收站根
            OriginListId = link.ListId,
            OriginPath = await ctx.Uow.Trees.PathDisplayAsync(
                link.ListId == null ? null : new FolderId(link.ListId), ct),
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            IsImportant = link.IsImportant,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = link.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await ctx.Uow.Trash.AddLinkAsync(snapshot, ct);
        await ctx.Uow.Links.RemoveAsync(id, ct);

        // 链接被移入回收站 → 原文件夹内容有变
        await ctx.Uow.Trees.TouchModifiedAsync(
            originListId == null ? null : new FolderId(originListId), ct);

        // 撤销载荷：撤销"删链接" = 还原回**删除前所在目录**（to: origin）——描述符"退回原参数"会丢落点
        // 信息（旧实现 = 撤销之后落根，与 Windows 口径不符）；需旧值的命令一律由处理器回填（撤销红线）。
        // 重做 = 再次移入回收站（默认重放原命令，原 ID 不变）。
        return CommandResult.Ok(
            new LinkTrashResult(id.Value, snapshot.OriginPath),
            new LinkPocket.Contracts.ChangeSet(
                Touched: [new LinkPocket.Contracts.EntityRef("link", id.Value)],
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged, LinkPocket.Contracts.DomainEventNames.TrashChanged],
                HumanSummary: $"已将「{link.Title ?? link.Url}」移入回收站"),
            [new UndoInverseStep("trash.restore",
                JsonSerializer.SerializeToElement(new { id = id.Value, to = "origin" }))]);
    }
}

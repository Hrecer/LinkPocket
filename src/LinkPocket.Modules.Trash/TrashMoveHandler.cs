using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.move（Mutation）：**回收站内搬移** —— 书签快照可移入/移出被删单元（缺省目标 = 回收站根），
/// 单元可改挂到另一个单元或退回根。
///
/// <para>**这不是还原**：只改回收站内的归属（trash_links.trash_folder_id / trash_folders.parent_id），
/// 原位置快照（origin_path / origin_folder_id）与主表一律不动；user 定稿：回收站不给撤销（无逆向载荷）。</para>
///
/// <para>成环（单元移入它自己或它的子树）即拒绝（同 folders.move 判据：子树集合包含目标）。</para>
/// </summary>
internal sealed class TrashMoveHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.move",
        Category: "trash",
        Description: "回收站内搬移：书签快照移入/移出被删单元，或单元改挂（目标缺省 = 回收站根；成环即拒绝；无撤销）",
        Parameters:
        [
            ParamSpec.Req<string>("id", "回收站条目 ID（link = 原链接 ID；folder = 单元 ID）"),
            ParamSpec.Req<bool>("is_folder", "true = 回收站单元；false = 单条书签快照"),
            ParamSpec.Opt<string>("target_trash_folder_id", "目标单元 ID；缺省 = 回收站根"),
        ],
        Caps: CommandCaps.Mutation,
        Impact: ImpactSummary.Link);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "id");
        var isFolder = CommandArgs.RequireBool(args, "is_folder");
        var target = CommandArgs.OptionalString(args, "target_trash_folder_id");
        var ct = ctx.Ct;

        var folders = await ctx.Uow.Trash.ListFoldersAsync(ct);
        TrashFolderId? targetUnit = target == null ? null : new TrashFolderId(target);
        if (target != null && !folders.Any(f => f.TrashFolderId == target))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"回收站单元 {target} 不存在", correlationId: ctx.CorrelationId));

        if (isFolder)
        {
            var unit = folders.FirstOrDefault(f => f.TrashFolderId == id)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站单元 {id} 不存在", correlationId: ctx.CorrelationId));

            if (target == id)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.CycleDetected, "不能把单元移入它自己", correlationId: ctx.CorrelationId));
            if (target != null && TrashSupport.CollectSubtreeIds(folders, id).Contains(target))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.CycleDetected,
                    $"把「{unit.Name}」移入它自己的子单元会产生循环引用", correlationId: ctx.CorrelationId));

            await ctx.Uow.Trash.MoveFolderAsync(new TrashFolderId(id), targetUnit, ct);
            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new { id, is_folder = true, target_trash_folder_id = target }),
                ChangeSet.Of(
                    new EntityRef("trash_unit", id),
                    LinkPocket.Contracts.DomainEventNames.TrashChanged,
                    $"已移动回收站单元「{unit.Name}」"));
        }

        var link = await ctx.Uow.Trash.FindLinkAsync(new LinkId(id), ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"回收站书签 {id} 不存在", correlationId: ctx.CorrelationId));

        await ctx.Uow.Trash.MoveLinkAsync(new LinkId(id), targetUnit, ct);
        var name = string.IsNullOrEmpty(link.Title) ? link.Url : link.Title!;
        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { id, is_folder = false, target_trash_folder_id = target }),
            ChangeSet.Of(
                new EntityRef("trash_link", id),
                LinkPocket.Contracts.DomainEventNames.TrashChanged,
                $"已移动回收站书签「{name}」"));
    }
}

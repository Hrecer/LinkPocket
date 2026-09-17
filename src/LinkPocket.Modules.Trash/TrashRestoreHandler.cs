using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore（Mutation）：从回收站还原链接（固定还原到根「全部书签」——行为等价项；回收站保留原 ID）。
/// </summary>
internal sealed class TrashRestoreHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore",
        Category: "trash",
        Description: "从回收站还原链接（固定还原到根级「全部书签」）",
        Parameters: [ParamSpec.Req<string>("id", "回收站书签快照 ID（= 原 link_id）")],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: "links.trash");

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var ct = ctx.Ct;

        var snapshot = await ctx.Uow.Trash.FindLinkAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"回收站中不存在书签 {id}", correlationId: ctx.CorrelationId));

        var restored = new Link
        {
            LinkId = snapshot.LinkId,
            Url = snapshot.Url,
            Title = snapshot.Title,
            Description = snapshot.Description,
            FaviconUrl = snapshot.FaviconUrl,
            ListId = null,   // 还原落根（行为等价项）
            LastVisitedAt = snapshot.LastVisitedAt,
            VisitCount = snapshot.VisitCount,
            IsImportant = snapshot.IsImportant,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await ctx.Uow.Links.AddAsync(restored, ct);
        await ctx.Uow.Trash.RemoveLinkAsync(id, ct);

        return CommandResult.Ok(
            new TrashRestoreResult(restored.LinkId),
            ChangeSet.Of(
                new EntityRef("link", restored.LinkId),
                "trash.changed",
                $"已还原「{restored.Title ?? restored.Url}」到「{FolderIds.RootDisplayName}」"));
    }
}

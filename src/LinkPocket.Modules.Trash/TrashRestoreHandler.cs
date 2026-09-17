using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore（Mutation）：从回收站还原链接（回收站保留原 ID）。
/// 缺省落根「全部书签」（行为等价项，界面不变）；<c>to_origin: true</c> 还原到删除前所在目录
/// （快照里本就存着 <c>origin_folder_id</c>）——原目录已不存在时回落根，并在结果里如实回报落点。
/// </summary>
internal sealed class TrashRestoreHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore",
        Category: "trash",
        Description: "从回收站还原链接（缺省落根级；to_origin = true 还原到删除前所在目录）",
        Parameters:
        [
            ParamSpec.Req<string>("id", "回收站书签快照 ID（= 原 link_id）"),
            ParamSpec.Opt<bool>("to_origin", "true = 还原到删除前所在目录（原目录已不存在时落根）；缺省 false = 落根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: "links.trash");

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var toOrigin = CommandArgs.OptionalBool(args, "to_origin");
        var ct = ctx.Ct;

        var snapshot = await ctx.Uow.Trash.FindLinkAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"回收站中不存在书签 {id}", correlationId: ctx.CorrelationId));

        // 原位还原：仅在「请求了 + 原目录仍在」时生效；否则落根（不静默假装还原成功）
        var originId = toOrigin ? FolderIds.Normalize(snapshot.OriginListId) : null;
        var originExists = originId != null
                           && await ctx.Uow.Folders.FindAsync(new FolderId(originId), ct) != null;
        var targetListId = originExists ? originId : null;

        var restored = new Link
        {
            LinkId = snapshot.LinkId,
            Url = snapshot.Url,
            Title = snapshot.Title,
            Description = snapshot.Description,
            FaviconUrl = snapshot.FaviconUrl,
            ListId = targetListId,
            LastVisitedAt = snapshot.LastVisitedAt,
            VisitCount = snapshot.VisitCount,
            IsImportant = snapshot.IsImportant,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await ctx.Uow.Links.AddAsync(restored, ct);
        await ctx.Uow.Trash.RemoveLinkAsync(id, ct);

        // 落进某个目录 → 该目录内容有变（落根无实体可 touch）
        if (targetListId != null)
            await ctx.Uow.Trees.TouchModifiedAsync(new FolderId(targetListId), ct);

        var location = targetListId == null
            ? FolderIds.RootDisplayName
            : await ctx.Uow.Trees.PathDisplayAsync(new FolderId(targetListId), ct);
        return CommandResult.Ok(
            new TrashRestoreResult(restored.LinkId, targetListId, originExists),
            ChangeSet.Of(
                new EntityRef("link", restored.LinkId),
                "trash.changed",
                $"已还原「{restored.Title ?? restored.Url}」到「{location}」"));
    }
}
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>trash.restore_batch（★ 引擎能力，不接 UI）：批量还原链接（固定落根；任一失败整批失败）。</summary>
internal sealed class TrashRestoreBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore_batch",
        Category: "trash",
        Description: "批量从回收站还原链接（固定落根；原子单事务）",
        Parameters: [ParamSpec.Req<IReadOnlyList<string>>("ids", "回收站书签快照 ID 列表")],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var ids = CommandArgs.StringArray(args, "ids");
        if (ids.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "ids 不能为空", correlationId: ctx.CorrelationId));
        var ct = ctx.Ct;

        foreach (var id in ids)
        {
            var snapshot = await ctx.Uow.Trash.FindLinkAsync(new LinkId(id), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"回收站中不存在书签 {id}", correlationId: ctx.CorrelationId));

            _ = await ctx.Uow.Links.AddAsync(new Link
            {
                LinkId = snapshot.LinkId,
                Url = snapshot.Url,
                Title = snapshot.Title,
                Description = snapshot.Description,
                FaviconUrl = snapshot.FaviconUrl,
                ListId = null,
                LastVisitedAt = snapshot.LastVisitedAt,
                VisitCount = snapshot.VisitCount,
                IsImportant = snapshot.IsImportant,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            }, ct);
            await ctx.Uow.Trash.RemoveLinkAsync(new LinkId(id), ct);
        }

        return CommandResult.Ok(
            new TrashRestoreBatchResult(ids.Count),
            new ChangeSet(
                Touched: ids.Select(i => new EntityRef("link", i)).ToList(),
                Events: ["links.changed", "trash.changed"],
                HumanSummary: $"已还原 {ids.Count} 个书签"));
    }
}

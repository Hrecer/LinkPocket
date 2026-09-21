using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore_batch（Mutation · 混合批量）：链接与单元**同一条流水线**、单事务原子
/// （任一项硬失败 → 整批回滚 + 指明失败 id）。形状对齐 <c>purge_batch</c>（link_ids / folder_ids），
/// 落点语义与单条命令一致（to 缺省 origin；先单元后链接——链接的 origin 可指向同批还原的单元）。
/// </summary>
internal sealed class TrashRestoreBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore_batch",
        Category: "trash",
        Description: "Restore links and units from the trash in batch (mixed; default returns to the pre-deletion location; one atomic transaction)",
        Parameters:
        [
            ParamSpec.Opt<IReadOnlyList<string>>("link_ids", "List of trash bookmark snapshot IDs"),
            ParamSpec.Opt<IReadOnlyList<string>>("folder_ids", "List of trash unit IDs"),
            ParamSpec.Opt<string>("to", "origin (default) = pre-deletion location; root = root level"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        var folderIds = CommandArgs.StringArray(args, "folder_ids");
        if (linkIds.Count == 0 && folderIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids and folder_ids must not both be empty",
                JsonSerializer.SerializeToElement(new { @param = "link_ids" })));
        var to = TrashRestoreSupport.ReadLandingMode(args);

        var outcome = await TrashRestoreSupport.RestoreAsync(ctx, linkIds, folderIds, to, null, ctx.Ct);

        var touched = outcome.Links.Select(l => new EntityRef("link", l.LinkId))
            .Concat(outcome.Units.Select(u => new EntityRef("folder", u.UnitId)))
            .ToList();
        var fellNote = outcome.FellBackToRoot.Count > 0
            ? $"; {outcome.FellBackToRoot.Count} item(s) lost their original location and landed at root"
            : string.Empty;
        return CommandResult.Ok(
            new TrashRestoreBatchResult(
                outcome.Links.Count,
                outcome.RestoredFolderIds.Count,
                outcome.Units.Count,
                outcome.FellBackToRoot,
                outcome.Renamed,
                outcome.Links.Count(l => l.Duplicate)),
            new ChangeSet(
                Touched: touched,
                Events: [DomainEventNames.FoldersChanged, DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                HumanSummary: $"Restored {outcome.Links.Count} link(s) and {outcome.Units.Count} unit(s) ({outcome.RestoredFolderIds.Count} folder(s)){fellNote}"),
            outcome.UndoSteps);
    }
}

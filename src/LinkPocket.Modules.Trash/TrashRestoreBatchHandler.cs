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
        Description: "批量从回收站还原链接与单元（混合；缺省回删除前位置；原子单事务）",
        Parameters:
        [
            ParamSpec.Opt<IReadOnlyList<string>>("link_ids", "回收站书签快照 ID 列表"),
            ParamSpec.Opt<IReadOnlyList<string>>("folder_ids", "回收站单元 ID 列表"),
            ParamSpec.Opt<string>("to", "origin（缺省）= 删除前位置；root = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        var folderIds = CommandArgs.StringArray(args, "folder_ids");
        if (linkIds.Count == 0 && folderIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids 与 folder_ids 不能同时为空",
                JsonSerializer.SerializeToElement(new { @param = "link_ids" })));
        var to = TrashRestoreSupport.ReadLandingMode(args);

        var outcome = await TrashRestoreSupport.RestoreAsync(ctx, linkIds, folderIds, to, null, ctx.Ct);

        var touched = outcome.Links.Select(l => new EntityRef("link", l.LinkId))
            .Concat(outcome.Units.Select(u => new EntityRef("folder", u.UnitId)))
            .ToList();
        var fellNote = outcome.FellBackToRoot.Count > 0
            ? $"；{outcome.FellBackToRoot.Count} 项原位置已不存在落根"
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
                HumanSummary: $"已还原 {outcome.Links.Count} 个链接、{outcome.Units.Count} 个单元（{outcome.RestoredFolderIds.Count} 个文件夹）{fellNote}"),
            outcome.UndoSteps);
    }
}

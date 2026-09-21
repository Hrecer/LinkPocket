using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.visit_batch（★ 引擎能力，不接 UI）：批量记账查看（链接各自 +1；所在目录父链各刷一次）。</summary>
internal sealed class LinkVisitBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.visit_batch",
        Category: "links",
        Description: "Record visits in batch (each link count +1 and last visit refreshed; the folder parent chain is refreshed once per deduplicated folder)",
        Parameters: [ParamSpec.Req<IReadOnlyList<string>>("link_ids", "List of link IDs")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        if (linkIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids must not be empty", correlationId: ctx.CorrelationId));
        var ct = ctx.Ct;

        var touchedFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"link {linkId} does not exist", correlationId: ctx.CorrelationId));
            link.VisitCount++;
            link.LastVisitedAt = DateTime.UtcNow;
            if (link.ListId != null) touchedFolders.Add(link.ListId);
        }

        foreach (var folderId in touchedFolders)
            await ctx.Uow.Trees.RecordFolderViewAsync(new FolderId(folderId), ct);

        return CommandResult.Ok(
            new LinkBatchResult("recorded", linkIds.Count),
            new ChangeSet(
                Touched: linkIds.Select(id => new EntityRef("link", id)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: $"Recorded {linkIds.Count} visit(s)"));
    }
}

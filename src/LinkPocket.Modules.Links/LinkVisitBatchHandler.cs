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
        Description: "批量记录查看（每个链接计数 +1、最后查看刷新；所在文件夹父链去重后各刷新一次）",
        Parameters: [ParamSpec.Req<IReadOnlyList<string>>("link_ids", "链接 ID 列表")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        if (linkIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids 不能为空", correlationId: ctx.CorrelationId));
        var ct = ctx.Ct;

        var touchedFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"链接 {linkId} 不存在", correlationId: ctx.CorrelationId));
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
                HumanSummary: $"已记录 {linkIds.Count} 次查看"));
    }
}

using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.copy_batch（★ 引擎能力，不接 UI）：批量复制链接到目标目录（新 ID，字段原样，UpdatedAt 刷新）。</summary>
internal sealed class LinkCopyBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.copy_batch",
        Category: "links",
        Description: "Copy links to a target folder in batch (new links are created; target_list_id default = root level)",
        Parameters:
        [
            ParamSpec.Req<IReadOnlyList<string>>("link_ids", "List of link IDs"),
            ParamSpec.Opt<string>("target_list_id", "Target folder ID; default = root level"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        if (linkIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids must not be empty", correlationId: ctx.CorrelationId));
        var target = CommandArgs.OptionalString(args, "target_list_id");
        var ct = ctx.Ct;

        if (target != null)
            _ = await ctx.Uow.Folders.FindAsync(new FolderId(target), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"target folder {target} does not exist", correlationId: ctx.CorrelationId));

        var created = new List<string>();
        var diff = new List<FieldChange>();
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"link {linkId} does not exist", correlationId: ctx.CorrelationId));

            var copy = new Link
            {
                Url = link.Url,
                Title = link.Title,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                ListId = target,
                LastVisitedAt = link.LastVisitedAt,
                VisitCount = link.VisitCount,
                IsImportant = link.IsImportant,
                CreatedAt = link.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = await ctx.Uow.Links.AddAsync(copy, ct);
            created.Add(copy.LinkId);
            diff.AddRange(EntityDiff.Diff(copy.LinkId, null, LinkSnapshot.Of(copy)));   // 复制 = 创建口径的全字段新值
        }

        await ctx.Uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);
        if (target != null) await LinkSupport.RefreshLinkCountAsync(ctx.Uow, target, ct);

        return CommandResult.Ok(
            new LinkBatchResult("copied", created.Count),
            new ChangeSet(
                Touched: created.Select(id => new EntityRef("link", id)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: $"Copied {created.Count} link(s)",
                Diff: diff.Count > 0 ? diff : null));
    }
}

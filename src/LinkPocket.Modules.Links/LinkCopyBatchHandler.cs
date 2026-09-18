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
        Description: "批量复制链接到目标目录（生成新链接；target_list_id 缺省 = 根级）",
        Parameters:
        [
            ParamSpec.Req<IReadOnlyList<string>>("link_ids", "链接 ID 列表"),
            ParamSpec.Opt<string>("target_list_id", "目标目录 ID；缺省 = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var linkIds = CommandArgs.StringArray(args, "link_ids");
        if (linkIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids 不能为空", correlationId: ctx.CorrelationId));
        var target = CommandArgs.OptionalString(args, "target_list_id");
        var ct = ctx.Ct;

        if (target != null)
            _ = await ctx.Uow.Folders.FindAsync(new FolderId(target), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"目标文件夹 {target} 不存在", correlationId: ctx.CorrelationId));

        var created = new List<string>();
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"链接 {linkId} 不存在", correlationId: ctx.CorrelationId));

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
        }

        await ctx.Uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);
        if (target != null) await LinkSupport.RefreshLinkCountAsync(ctx.Uow, target, ct);

        return CommandResult.Ok(
            new LinkBatchResult("copied", created.Count),
            new ChangeSet(
                Touched: created.Select(id => new EntityRef("link", id)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: $"已复制 {created.Count} 个链接"));
    }
}

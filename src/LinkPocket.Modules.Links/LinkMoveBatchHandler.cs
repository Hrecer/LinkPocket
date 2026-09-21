using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.move_batch（★ 引擎能力，不接 UI）：批量移动链接到目标目录。
/// 单命令单事务（消灭前端逐条过协议的 O(n²)）；新旧目录父链 Touch + LinkCount 回填。
/// </summary>
internal sealed class LinkMoveBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.move_batch",
        Category: "links",
        Description: "Move links to a target folder in batch (one atomic transaction; target_list_id default = root level)",
        Parameters:
        [
            ParamSpec.Req<IReadOnlyList<string>>("link_ids", "List of link IDs"),
            ParamSpec.Opt<string>("target_list_id", "Target folder ID; default = root level"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);   // 可撤销；逆向参数由处理器回填（每项一步）

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

        var previousFolders = new HashSet<string>(StringComparer.Ordinal);
        var oldListIds = new Dictionary<string, string?>(StringComparer.Ordinal);   // 撤销载荷要带旧目录（变更前快照）
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"link {linkId} does not exist", correlationId: ctx.CorrelationId));
            if (link.ListId != null) previousFolders.Add(link.ListId);
            oldListIds[linkId] = link.ListId;
            link.ListId = target;
            link.UpdatedAt = DateTime.UtcNow;
        }

        // 新旧目录父链内容变化 + LinkCount 缓存回填
        foreach (var folderId in previousFolders)
            await ctx.Uow.Trees.TouchModifiedAsync(new FolderId(folderId), ct);
        await ctx.Uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);
        if (target != null) await LinkSupport.RefreshLinkCountAsync(ctx.Uow, target, ct);
        foreach (var folderId in previousFolders)
            await LinkSupport.RefreshLinkCountAsync(ctx.Uow, folderId, ct);

        // 撤销载荷：每项一步（各自旧目录可能不同）→ 逆向 = 单项移回原目录。
        // 用 move_batch 而非 links.update：只有它能表达"移回根级"（target_list_id 缺省 = 根）。
        var undo = linkIds
            .Where(id => oldListIds[id] != target)
            .Select(id => new UndoInverseStep("links.move_batch",
                JsonSerializer.SerializeToElement(new { link_ids = new[] { id }, target_list_id = oldListIds[id] })))
            .ToList();

        return CommandResult.Ok(
            new LinkBatchResult("moved", linkIds.Count),
            new ChangeSet(
                Touched: linkIds.Select(id => new EntityRef("link", id)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: $"Moved {linkIds.Count} link(s)"),
            undo.Count > 0 ? undo : null);
    }
}

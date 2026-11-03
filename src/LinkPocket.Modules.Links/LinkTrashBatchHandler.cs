using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.trash_batch（Mutation）：批量移入回收站（单事务、集合化写入）。
/// 语义与逐条 links.trash 一致（原 ID + 原位置快照），一次调用 = 一条撤销记录：
/// 撤销 = trash.restore_batch（合并还原），重做 = 本命令再删一次。
/// </summary>
internal sealed class LinkTrashBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.trash_batch",
        Category: "links",
        Description: "Move links to the trash in batch (one atomic transaction; keeps original IDs and locations)",
        Parameters:
        [
            ParamSpec.Req<IReadOnlyList<string>>("link_ids", "List of link IDs"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: null,   // 逆向需带落点/批量参数 → 由处理器回填
        Impact: ImpactSummary.Link,
        Client: new("LinkTrashBatchAsync", typeof(LinkBatchResult)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var rawIds = CommandArgs.StringArray(args, "link_ids");
        var linkIds = rawIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (linkIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "link_ids must not be empty", correlationId: ctx.CorrelationId));

        var uow = ctx.Uow;
        var ct = ctx.Ct;

        // 预检：一次性取回 + 按输入顺序核对缺失（硬失败 → 整批不写）
        var links = new List<Link>(linkIds.Count);
        foreach (var chunk in linkIds.Chunk(500))
            links.AddRange(await uow.Links.ListAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { IdIn = chunk },
            }, ct));
        var byId = links.ToDictionary(l => l.LinkId, StringComparer.Ordinal);
        var ordered = new List<Link>(linkIds.Count);
        foreach (var id in linkIds)
        {
            if (!byId.TryGetValue(id, out var link))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"link {id} does not exist", correlationId: ctx.CorrelationId));
            ordered.Add(link);
        }

        var ownsTx = !uow.HasActiveTransaction;
        ITransactionScope? tx = null;
        if (ownsTx)
        {
            tx = uow.BeginTransaction();
            await tx.BeginAsync(ct);
        }
        try
        {
            var trashedAt = DateTime.UtcNow;
            var diff = new List<FieldChange>(ordered.Count);
            var groups = ordered.GroupBy(l => l.ListId).ToList();
            foreach (var group in groups)
            {
                var originPath = await uow.Trees.PathCanonicalAsync(
                    group.Key == null ? null : new FolderId(group.Key), ct);
                await uow.Trash.TrashLinksOfFolderSelectionAsync(
                    group.Key, originPath, group.Select(l => l.LinkId).ToArray(), trashedAt, ct);
                foreach (var link in group)
                    diff.AddRange(EntityDiff.Diff(link.LinkId, LinkSnapshot.Of(link), null));
            }
            foreach (var group in groups)
                await uow.Trees.TouchModifiedAsync(group.Key == null ? null : new FolderId(group.Key), ct);

            if (ownsTx)
            {
                await uow.CommitAsync(ct);   // EF 侧收尾（父链 Touch）
                await tx!.CommitAsync(ct);
            }

            var undoArgs = JsonSerializer.SerializeToElement(new { link_ids = linkIds });
            return CommandResult.Ok(
                new LinkBatchResult("trash", linkIds.Count),
                new ChangeSet(
                    Touched: ordered.Select(l => new EntityRef("link", l.LinkId)).ToArray(),
                    Events: [DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                    HumanSummary: $"{linkIds.Count} bookmark(s) moved to trash",
                    Diff: diff.Count > 0 ? diff : null),
                [new UndoInverseStep(
                    "trash.restore_batch",
                    JsonSerializer.SerializeToElement(new { link_ids = linkIds, to = "origin" }),
                    new UndoAction("links.trash_batch", undoArgs))]);
        }
        catch
        {
            if (ownsTx && tx != null)
            {
                try { await tx.RollbackAsync(ct); } catch { /* 回滚失败不覆盖原始异常 */ }
            }
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }
}

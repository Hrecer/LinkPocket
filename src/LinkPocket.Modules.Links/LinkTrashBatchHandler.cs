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

        // raw SQL 搬运必须与 EF 父链触碰共用一个显式事务；事务从**预检之前**开始：
        // 预检与搬运读同一份已提交快照，避免"预检后到开事务之间被并发删除"时
        // 仍把整批报成成功（实际一行都没搬进回收站）的假入站。
        var ownsTx = !uow.HasActiveTransaction;
        ITransactionScope? tx = null;
        if (ownsTx)
        {
            tx = uow.BeginTransaction();
            await tx.BeginAsync(ct);
        }
        try
        {
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

            var trashedAt = DateTime.UtcNow;
            var moved = 0;
            var diff = new List<FieldChange>(ordered.Count);
            var groups = ordered.GroupBy(l => l.ListId).ToList();
            foreach (var group in groups)
            {
                var originPath = await uow.Trees.PathCanonicalAsync(
                    group.Key == null ? null : new FolderId(group.Key), ct);
                moved += await uow.Trash.TrashLinksOfFolderSelectionAsync(
                    group.Key, originPath, group.Select(l => l.LinkId).ToArray(), trashedAt, ct);
                foreach (var link in group)
                    diff.AddRange(EntityDiff.Diff(link.LinkId, LinkSnapshot.Of(link), null));
            }

            // 集合搬运按 (目录, 选中 ID) 限定；行数对不上说明链接在事务快照之外被移动/删除，
            // 此时继续提交会漏快照（原链接没了、回收站也没有）。硬失败回滚，绝不静默吞项。
            if (moved != ordered.Count)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.BatchAborted,
                    $"batch trash aborted: expected {ordered.Count} link(s) but moved {moved}; the library changed concurrently",
                    correlationId: ctx.CorrelationId));

            foreach (var group in groups)
                await uow.Trees.TouchModifiedAsync(group.Key == null ? null : new FolderId(group.Key), ct);

            if (ownsTx)
            {
                await uow.CommitAsync(ct);   // EF 侧收尾（父链 Touch）
                await tx!.CommitAsync(ct);
            }

            var undoArgs = JsonSerializer.SerializeToElement(new { link_ids = linkIds });
            return CommandResult.Ok(
                new LinkBatchResult("trash", ordered.Count),
                new ChangeSet(
                    Touched: ordered.Select(l => new EntityRef("link", l.LinkId)).ToArray(),
                    Events: [DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                    HumanSummary: $"{ordered.Count} bookmark(s) moved to trash",
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

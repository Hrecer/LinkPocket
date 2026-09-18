using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.move_batch（★ 引擎能力，不接 UI）：批量移动链接到目标目录。
/// 单命令单事务（方案 7.2：消灭前端逐条过协议的 O(n²)）；新旧目录父链 Touch + LinkCount 回填。
/// </summary>
internal sealed class LinkMoveBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.move_batch",
        Category: "links",
        Description: "批量移动链接到目标目录（原子单事务；target_list_id 缺省 = 根级）",
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

        var previousFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var linkId in linkIds)
        {
            var link = await ctx.Uow.Links.FindAsync(new LinkId(linkId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"链接 {linkId} 不存在", correlationId: ctx.CorrelationId));
            if (link.ListId != null) previousFolders.Add(link.ListId);
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

        return CommandResult.Ok(
            new LinkBatchResult("moved", linkIds.Count),
            new ChangeSet(
                Touched: linkIds.Select(id => new EntityRef("link", id)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: $"已移动 {linkIds.Count} 个链接"));
    }
}

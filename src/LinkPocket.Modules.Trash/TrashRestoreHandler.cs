using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore（Mutation）：从回收站还原**单条链接**（保留原 ID）。
/// 落点：<c>to = "origin"</c>（缺省）还原到删除前所在目录（原目录已不存在时落根并在结果里如实回报）；
/// <c>to = "root"</c> 显式落根。执行走唯一流水线 <see cref="TrashRestoreSupport"/>。
/// </summary>
internal sealed class TrashRestoreHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore",
        Category: "trash",
        Description: "Restore a link from the trash (default returns to its pre-deletion folder; when that folder is gone it lands at root and is reported as such)",
        Parameters:
        [
            ParamSpec.Req<string>("id", "Trash bookmark snapshot ID (= the original link_id)"),
            ParamSpec.Opt<string>("to", "origin (default) = pre-deletion folder; root = root level"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: "links.trash");   // 撤销 = 再移入回收站（与落点无关）；重做/落点由处理器回填，见流水线

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "id");
        var to = TrashRestoreSupport.ReadLandingMode(args);

        var outcome = await TrashRestoreSupport.RestoreAsync(ctx, [id], [], to, null, ctx.Ct);
        var info = outcome.Links.Single();

        var location = info.Landing == null
            ? FolderIds.RootToken
            : await ctx.Uow.Trees.PathCanonicalAsync(new FolderId(info.Landing), ctx.Ct);
        var fellNote = info.FellBackToRoot ? "(the original folder is gone, fell back to root level)" : string.Empty;
        return CommandResult.Ok(
            new TrashRestoreResult(info.LinkId, info.Landing, info.FellBackToRoot),
            new ChangeSet(
                Touched: [new EntityRef("link", info.LinkId)],
                Events: [DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                HumanSummary: $"Restored '{info.Title ?? info.Url}' to '{location}'{fellNote}",
                Diff: outcome.Diff.Count > 0 ? outcome.Diff : null),
            outcome.UndoSteps);
    }
}

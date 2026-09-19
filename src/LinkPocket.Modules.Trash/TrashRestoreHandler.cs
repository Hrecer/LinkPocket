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
        Description: "从回收站还原链接（缺省落回删除前所在目录；原目录已不存在时落根并如实回报）",
        Parameters:
        [
            ParamSpec.Req<string>("id", "回收站书签快照 ID（= 原 link_id）"),
            ParamSpec.Opt<string>("to", "origin（缺省）= 删除前所在目录；root = 根级"),
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
            ? FolderIds.RootDisplayName
            : await ctx.Uow.Trees.PathDisplayAsync(new FolderId(info.Landing), ctx.Ct);
        var fellNote = info.FellBackToRoot ? "（原目录已不存在，回调根级）" : string.Empty;
        return CommandResult.Ok(
            new TrashRestoreResult(info.LinkId, info.Landing, info.FellBackToRoot),
            new ChangeSet(
                Touched: [new EntityRef("link", info.LinkId)],
                Events: [DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                HumanSummary: $"已还原「{info.Title ?? info.Url}」到「{location}」{fellNote}"),
            outcome.UndoSteps);
    }
}

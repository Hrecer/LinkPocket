using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore_unit（Mutation）：还原一个**回收站单元**——整棵被删文件夹子树（含子单元与单元内书签快照），
/// 全部保留原 ID（与回收站"保留原 ID"的既有口径一致，还原后引用与选中可继续追踪）。
///
/// <para>落点：<c>to = "origin"</c>（缺省）落回删除前所在父目录（原父已不存在时落根并如实回报"回落"）；
/// <c>to = "root"</c> 落根；<c>target_parent_id</c> = 显式落点（与 to 互斥、且必须存在——错就报错，
/// 不静默改落点）。执行走唯一流水线 <see cref="TrashRestoreSupport"/>。</para>
///
/// <para>本命令是 <c>folders.delete</c>（cascade=trash_links）的逆向，使"删文件夹"可被 Ctrl+Z 撤销。</para>
/// </summary>
internal sealed class TrashRestoreUnitHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore_unit",
        Category: "trash",
        Description: "还原回收站单元（整棵被删文件夹子树 + 单元内书签；保留原 ID；缺省落回删除前所在父目录）",
        Parameters:
        [
            ParamSpec.Req<string>("unit_id", "回收站单元 ID（= 原文件夹 ID）"),
            ParamSpec.Opt<string>("to", "origin（缺省）= 删除前所在父目录（原父已不存在时落根并如实回报）；root = 根级"),
            ParamSpec.Opt<string>("target_parent_id", "显式落点父目录 ID（与 to 互斥且必须存在）"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        Impact: ImpactSummary.Folder);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var unitId = CommandArgs.RequireString(args, "unit_id");
        var toRaw = CommandArgs.OptionalString(args, "to");
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        if (toRaw != null && target != null)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, "「to」与「target_parent_id」互斥，不能同时传",
                JsonSerializer.SerializeToElement(new { @param = "target_parent_id" })));
        var to = toRaw == null ? TrashRestoreSupport.ToOrigin : TrashRestoreSupport.ValidateLandingMode(toRaw);

        var outcome = await TrashRestoreSupport.RestoreAsync(ctx, [], [unitId], to, target, ctx.Ct);
        var info = outcome.Units.Single();

        var location = info.Landing == null
            ? FolderIds.RootDisplayName
            : await ctx.Uow.Trees.PathDisplayAsync(new FolderId(info.Landing), ctx.Ct);
        var renameNote = outcome.Renamed.Count == 0 ? string.Empty : $"（自动编号 {outcome.Renamed.Count} 项）";
        var fellNote = info.FellBackToRoot ? "（原父已不存在，回调根级）" : string.Empty;
        return CommandResult.Ok(
            new TrashRestoreUnitResult(unitId, info.Landing, outcome.RestoredFolderIds.Count, outcome.Links.Count, info.FellBackToRoot),
            new ChangeSet(
                Touched: [new EntityRef("folder", unitId)],
                Events: [DomainEventNames.FoldersChanged, DomainEventNames.LinksChanged, DomainEventNames.TrashChanged],
                HumanSummary: $"已还原文件夹单元（{outcome.RestoredFolderIds.Count} 个文件夹 / {outcome.Links.Count} 个链接）到「{location}」{renameNote}{fellNote}"),
            outcome.UndoSteps);
    }
}

/// <summary>还原结果：落点（null = 根）+ 恢复规模 + 是否因原父消失而回落根级（如实回报，绝不静默）。</summary>
public sealed record TrashRestoreUnitResult(
    string UnitId, string? Landing, int FolderCount, int LinkCount, bool FellBackToRoot);

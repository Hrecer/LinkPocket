using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.move（Mutation）：移动文件夹；target 缺省 = 根。成环即拒绝；新旧两个父链 Touch。
/// **目标层同层唯一命名**（Windows 口径）：撞名自动编号「名 (2)」。</summary>
internal sealed class FolderMoveHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.move",
        Category: "folders",
        Description: "移动文件夹到目标父目录（target_parent_id 缺省 = 根级「全部书签」）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "文件夹 ID"),
            ParamSpec.Opt<string>("target_parent_id", "目标父目录 ID；缺省 = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);   // 可撤销；逆向参数由处理器回填（见 undo）

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        var ct = ctx.Ct;

        var folder = await ctx.Uow.Folders.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));
        var previousParentId = folder.ParentId;

        if (target == id.Value)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.CycleDetected, "不能把文件夹移入它自己", correlationId: ctx.CorrelationId));

        if (target != null)
        {
            if (await ctx.Uow.Trees.WouldCreateCycleAsync(id, new FolderId(target), ct))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.CycleDetected, $"把「{folder.Name}」移动到 {target} 会产生循环引用", correlationId: ctx.CorrelationId));
            _ = await ctx.Uow.Folders.FindAsync(new FolderId(target), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"父文件夹 {target} 不存在", correlationId: ctx.CorrelationId));
        }

        // 同层唯一命名（目标层，排除自身）：撞名 → 「名 (2)」（Windows 口径，编号口径唯一出处 = Kernel FolderNaming）。
        // 移到自己所在层时自身被排除 → 名字保持不变，无需任何特例分支。
        folder.Name = await FolderNaming.ResolveAsync(ctx.Uow, target, folder.Name, folder.FolderId, ct);
        folder.ParentId = target;
        folder.UpdatedAt = DateTime.UtcNow;

        // 原父级与新父级的内容构成变化（与既有两处 Touch 等价）
        await ctx.Uow.Trees.TouchModifiedAsync(
            previousParentId == null ? null : new FolderId(previousParentId), ct);
        await ctx.Uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);

        // 撤销载荷：仅当父目录真的变了才可撤销（移到自己所在层 = 无操作，不入栈）。
        // 逆向参数带**旧父目录**——引擎无法从原参数反推旧值，这正是过去"移动不可撤销"的结构原因。
        var undo = previousParentId == target
            ? null
            : new[]
            {
                new UndoInverseStep("folders.move",
                    JsonSerializer.SerializeToElement(new { folder_id = id.Value, target_parent_id = previousParentId }))
            };

        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"已移动文件夹「{folder.Name}」"),
            undo);
    }
}

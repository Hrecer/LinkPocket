using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.move（Mutation）：移动文件夹；target 缺省 = 根。成环即拒绝；新旧两个父链 Touch。</summary>
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
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

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

        folder.ParentId = target;
        folder.UpdatedAt = DateTime.UtcNow;

        // 原父级与新父级的内容构成变化（与既有两处 Touch 等价）
        await ctx.Uow.Trees.TouchModifiedAsync(
            previousParentId == null ? null : new FolderId(previousParentId), ct);
        await ctx.Uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);

        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                "folders.changed",
                $"已移动文件夹「{folder.Name}」"));
    }
}

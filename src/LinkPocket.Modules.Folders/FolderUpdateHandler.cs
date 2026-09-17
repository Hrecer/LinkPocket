using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.update（Mutation）：改名 / 改描述；（可选）改父目录。
/// parent_id 语义与既有口径一致：缺省或根值 = 「不改父级」；要移动请用 folders.move。
/// 自身被改名/移动 + 原父级、新父级的内容构成都发生变化（三处父链 Touch）。
/// </summary>
internal sealed class FolderUpdateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.update",
        Category: "folders",
        Description: "修改文件夹（名称/描述；parent_id 仅在显式传入且不同时才切换父级——换父请优先用 folders.move）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "文件夹 ID"),
            ParamSpec.Opt<string>("name", "新名称"),
            ParamSpec.Opt<string>("description", "新描述"),
            ParamSpec.Opt<string>("parent_id", "新父目录 ID（缺省 = 不改父级）"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var name = CommandArgs.OptionalString(args, "name");
        var description = CommandArgs.OptionalString(args, "description");
        var parentId = FolderIds.Normalize(CommandArgs.OptionalString(args, "parent_id"));
        var ct = ctx.Ct;

        var folder = await ctx.Uow.Folders.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));
        var previousParentId = folder.ParentId;

        if (parentId != null && parentId != folder.ParentId)
        {
            if (parentId == id.Value)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.CycleDetected, "不能把文件夹设为它自己的父目录", correlationId: ctx.CorrelationId));

            if (await ctx.Uow.Trees.WouldCreateCycleAsync(id, new FolderId(parentId), ct))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.CycleDetected, $"把「{folder.Name}」移动到 {parentId} 会产生循环引用", correlationId: ctx.CorrelationId));

            _ = await ctx.Uow.Folders.FindAsync(new FolderId(parentId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"父文件夹 {parentId} 不存在", correlationId: ctx.CorrelationId));
        }

        if (!string.IsNullOrEmpty(name)) folder.Name = name.Trim();
        if (description != null) folder.Description = description;
        if (parentId != null) folder.ParentId = parentId;
        folder.UpdatedAt = DateTime.UtcNow;

        // 自身被改名 / 被移动，以及原父级、新父级的内容构成变化（与既有三处 Touch 等价）
        await ctx.Uow.Trees.TouchModifiedAsync(id, ct);
        await ctx.Uow.Trees.TouchModifiedAsync(
            previousParentId == null ? null : new FolderId(previousParentId), ct);
        if (parentId != null && parentId != previousParentId)
            await ctx.Uow.Trees.TouchModifiedAsync(new FolderId(parentId), ct);

        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                "folders.changed",
                $"已更新文件夹「{folder.Name}」"));
    }
}

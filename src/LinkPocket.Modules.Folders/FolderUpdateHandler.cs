using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.update（Mutation）：改名 / 改描述——**只改自身属性，绝不换父**。
/// 换父是唯一的另一条语义，收敛到 <c>folders.move</c>：一个动作一个入口，不存在"两处都能换父"的误用面。
/// 自身被改名 → 自身与父链的 UpdatedAt 刷新。
/// </summary>
internal sealed class FolderUpdateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.update",
        Category: "folders",
        Description: "修改文件夹（仅名称/描述；换父请用 folders.move）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "文件夹 ID"),
            ParamSpec.Opt<string>("name", "新名称"),
            ParamSpec.Opt<string>("description", "新描述"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var name = CommandArgs.OptionalString(args, "name");
        var description = CommandArgs.OptionalString(args, "description");
        var ct = ctx.Ct;

        var folder = await ctx.Uow.Folders.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));

        if (name != null)
        {
            var trimmedName = name.Trim();
            if (trimmedName.Length == 0)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.RequiredParam, "名称不能为空", correlationId: ctx.CorrelationId));
            folder.Name = trimmedName;
        }
        if (description != null) folder.Description = description;
        folder.UpdatedAt = DateTime.UtcNow;

        // 改名 → 自身与全部祖先的内容构成变化（与既有 Touch 口径等价）
        await ctx.Uow.Trees.TouchModifiedAsync(id, ct);

        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"已更新文件夹「{folder.Name}」"));
    }
}

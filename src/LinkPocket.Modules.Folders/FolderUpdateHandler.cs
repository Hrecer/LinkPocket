using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.update（Mutation）：改名 / 改描述——**只改自身属性，绝不换父**。
/// 换父是唯一的另一条语义，收敛到 <c>folders.move</c>：一个动作一个入口，不存在"两处都能换父"的误用面。
/// **改名走同层唯一命名**（Windows 口径）：撞名自动编号「名 (2)」，自身不算占用者。
/// 自身被改名 → 自身与父链的 UpdatedAt 刷新。
/// </summary>
internal sealed class FolderUpdateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.update",
        Category: "folders",
        Description: "Update a folder (name and description only; use folders.move to change the parent)",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "Folder ID"),
            ParamSpec.Opt<string>("name", "New name"),
            ParamSpec.Opt<string>("description", "New description"),
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
                EngineErrors.EntityNotFound, $"folder {id} does not exist", correlationId: ctx.CorrelationId));
        var before = FolderSnapshot.Of(folder);   // 字段级 diff 的旧值快照（改名编号前）

        if (name != null)
        {
            if (name.Trim().Length == 0)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.RequiredParam, "name must not be empty", correlationId: ctx.CorrelationId));
            // 同层唯一命名（排除自身）：改名撞名 → 「名 (2)」（Windows 口径，编号口径唯一出处 = 命名服务 IFolderNaming）
            folder.Name = await ctx.Uow.Naming.ResolveAsync(folder.ParentId, name, folder.FolderId, ct);
        }
        if (description != null) folder.Description = description;
        folder.UpdatedAt = DateTime.UtcNow;

        // 改名 → 自身与全部祖先的内容构成变化（与既有 Touch 口径等价）
        await ctx.Uow.Trees.TouchModifiedAsync(id, ct);

        var diff = EntityDiff.Diff(folder.FolderId, before, FolderSnapshot.Of(folder));
        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"Folder '{folder.Name}' updated",
                diff: diff.Count > 0 ? diff : null));
    }
}

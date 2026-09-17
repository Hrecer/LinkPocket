using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.create（Mutation）：新建文件夹（同名允许——既有口径；重命名策略只在移动/复制/批量场景）。
/// 新建 → 父链内容有变（TouchModified）。
/// </summary>
internal sealed class FolderCreateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.create",
        Category: "folders",
        Description: "新建文件夹（可指定父目录与描述；parent_id 缺省 = 根级）",
        Parameters:
        [
            ParamSpec.Req<string>("name", "文件夹名"),
            ParamSpec.Opt<string>("description", "描述"),
            ParamSpec.Opt<string>("parent_id", "父目录 ID；缺省 = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var description = CommandArgs.OptionalString(args, "description");
        var parentId = FolderIds.Normalize(CommandArgs.OptionalString(args, "parent_id"));
        var ct = ctx.Ct;

        if (parentId != null)
            _ = await ctx.Uow.Folders.FindAsync(new FolderId(parentId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"父文件夹 {parentId} 不存在", correlationId: ctx.CorrelationId));

        var folder = new Folder
        {
            Name = name.Trim(),
            Description = description,
            ParentId = parentId,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await ctx.Uow.Folders.AddAsync(folder, ct);

        // 新增子文件夹 → 父链内容有变
        await ctx.Uow.Trees.TouchModifiedAsync(
            parentId == null ? null : new FolderId(parentId), ct);

        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                "folders.changed",
                $"已创建文件夹「{folder.Name}」"));
    }
}

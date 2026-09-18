using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.create（Mutation）：新建文件夹。
/// **同层唯一命名**（Windows 口径）：与父目录下已有兄弟撞名一律自动编号「名 (2)」，
/// 编号口径由 Kernel <see cref="FolderNaming"/> 单一决定（不同目录可同名）。
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
        var parentId = CommandArgs.OptionalString(args, "parent_id");
        var ct = ctx.Ct;

        if (parentId != null)
            _ = await ctx.Uow.Folders.FindAsync(new FolderId(parentId), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"父文件夹 {parentId} 不存在", correlationId: ctx.CorrelationId));

        // 同层唯一命名：与父目录下已有兄弟撞名 → 「名 (2)」（编号口径唯一出处 = Kernel FolderNaming）
        var resolvedName = await FolderNaming.ResolveAsync(ctx.Uow, parentId, name, null, ct);

        var folder = new Folder
        {
            Name = resolvedName,
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

        // 撤销载荷：撤销"新建"= 把刚建的文件夹移入回收站（软删除）。
        // **重做必须显式给出** = 从回收站还原该单元（保留原 ID + 回原父）；重放 folders.create 会生成新 ID。
        return CommandResult.Ok(
            folder.ToDto(null),
            ChangeSet.Of(
                new EntityRef("folder", folder.FolderId),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"已创建文件夹「{folder.Name}」"),
            [new UndoInverseStep("folders.delete",
                JsonSerializer.SerializeToElement(new { folder_id = folder.FolderId, cascade = "trash_links" }),
                new UndoAction("trash.restore_unit",
                    JsonSerializer.SerializeToElement(new { unit_id = folder.FolderId, target_parent_id = parentId })))]);
    }
}

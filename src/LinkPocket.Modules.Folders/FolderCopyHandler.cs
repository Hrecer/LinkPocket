using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.copy（Mutation）：深层复制文件夹（含子树与链接；名称保持原样——
/// 同名自动编号是调用方/UI 层的既有职责）。新 ID 由实体构造生成，单工作单元一次提交。
/// </summary>
internal sealed class FolderCopyHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.copy",
        Category: "folders",
        Description: "深层复制文件夹（含全部子文件夹与书签；返回新文件夹 ID）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "源文件夹 ID"),
            ParamSpec.Opt<string>("target_parent_id", "目标父目录 ID；缺省 = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        if (target != null)
            _ = await uow.Folders.FindAsync(new FolderId(target), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"目标文件夹 {target} 不存在", correlationId: ctx.CorrelationId));

        var allFolders = await uow.Folders.ListAllAsync(ct);
        var source = allFolders.FirstOrDefault(f => f.FolderId == id.Value)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));

        var allLinks = await uow.Links.ListAsync(new LinkQuerySpec(), ct);
        var linksByFolder = allLinks
            .Where(l => l.ListId != null)
            .GroupBy(l => l.ListId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // 深拷贝：新实体在内存建立父子关系，一次注册、单提交（与既有最终状态逐字段等价）
        var newFolder = new Folder
        {
            Name = source.Name,
            Description = source.Description,
            ParentId = target,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await uow.Folders.AddAsync(newFolder, ct);
        await CopyLinksAsync(uow, id.Value, newFolder.FolderId, linksByFolder, ct);
        await CopyChildrenAsync(uow, allFolders, id.Value, newFolder.FolderId, linksByFolder, ct);

        await uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);

        return CommandResult.Ok(
            new FolderCopyResult(newFolder.FolderId),
            ChangeSet.Of(
                new EntityRef("folder", newFolder.FolderId),
                "folders.changed",
                $"已复制文件夹「{source.Name}」"));
    }

    private static async Task CopyChildrenAsync(
        Kernel.IUnitOfWork uow, IReadOnlyList<Folder> allFolders,
        string sourceParentId, string destParentId,
        IReadOnlyDictionary<string, List<Link>> linksByFolder, CancellationToken ct)
    {
        foreach (var child in allFolders.Where(f => f.ParentId == sourceParentId))
        {
            var newChild = new Folder
            {
                Name = child.Name,
                Description = child.Description,
                ParentId = destParentId,
                LinkCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = await uow.Folders.AddAsync(newChild, ct);
            await CopyLinksAsync(uow, child.FolderId, newChild.FolderId, linksByFolder, ct);
            await CopyChildrenAsync(uow, allFolders, child.FolderId, newChild.FolderId, linksByFolder, ct);
        }
    }

    private static async Task CopyLinksAsync(
        Kernel.IUnitOfWork uow, string sourceFolderId, string destFolderId,
        IReadOnlyDictionary<string, List<Link>> linksByFolder, CancellationToken ct)
    {
        if (!linksByFolder.TryGetValue(sourceFolderId, out var links)) return;
        foreach (var link in links)
        {
            _ = await uow.Links.AddAsync(new Link
            {
                Url = link.Url,
                Title = link.Title,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                ListId = destFolderId,
                LastVisitedAt = link.LastVisitedAt,
                VisitCount = link.VisitCount,
                IsImportant = link.IsImportant,
                CreatedAt = link.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            }, ct);
        }
    }
}

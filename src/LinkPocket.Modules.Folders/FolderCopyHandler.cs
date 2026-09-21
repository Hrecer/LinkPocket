using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.copy（Mutation）：深层复制文件夹（含子树与链接）。
/// **目标层与子树内部一律经单一命名服务**（Windows 口径）：副本与目标目录已有兄弟撞名 → 「名 (2)」；
/// 子树每一层也用同一张占用表累积（不再依赖"源子树自身已满足同层唯一"这一隐性假设——
/// 坏数据/外部来源一旦不满足，直写原名的旧写法会让 v4 唯一索引拒绝整条命令）。
/// 新 ID 由实体构造生成，单工作单元一次提交。
/// </summary>
internal sealed class FolderCopyHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.copy",
        Category: "folders",
        Description: "Deep-copy a folder (including all child folders and bookmarks; returns the new folder ID)",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "Source folder ID"),
            ParamSpec.Opt<string>("target_parent_id", "Target parent folder ID; default = root level"),
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
                    EngineErrors.EntityNotFound, $"target folder {target} does not exist", correlationId: ctx.CorrelationId));

        var allFolders = await uow.Folders.ListAllAsync(ct);
        var source = allFolders.FirstOrDefault(f => f.FolderId == id.Value)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"folder {id} does not exist", correlationId: ctx.CorrelationId));

        var allLinks = await uow.Links.ListAsync(new LinkQuerySpec(), ct);
        var linksByFolder = allLinks
            .Where(l => l.ListId != null)
            .GroupBy(l => l.ListId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // 深拷贝：新实体在内存建立父子关系，一次注册、单提交（与既有最终状态逐字段等价）
        var newFolder = new Folder
        {
            // 目标层同层唯一命名（编号口径唯一出处 = Kernel 命名服务）
            Name = await uow.Naming.ResolveAsync(target, source.Name, null, ct),
            Description = source.Description,
            ParentId = target,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = await uow.Folders.AddAsync(newFolder, ct);
        await CopyLinksAsync(uow, id.Value, newFolder.FolderId, linksByFolder, ct);

        // 子树每一层共用一张占用表：键 = 目标父层，各层独立累积（新文件夹在库里还没有子项，无需查库预置）
        await CopyChildrenAsync(uow, allFolders, id.Value, newFolder.FolderId, linksByFolder,
            uow.Naming.CreateTable(), ct);

        await uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);

        // 撤销载荷：撤销"复制"= 把副本（含整棵子树）移入回收站。
        // **重做必须显式给出** = 从回收站还原副本（保留原 ID）；重放 folders.copy 会再复制一份新 ID。
        return CommandResult.Ok(
            new FolderCopyResult(newFolder.FolderId, newFolder.Name),
            ChangeSet.Of(
                new EntityRef("folder", newFolder.FolderId),
                LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                $"Folder '{source.Name}' copied"),
            [new UndoInverseStep("folders.delete",
                JsonSerializer.SerializeToElement(new { folder_id = newFolder.FolderId, cascade = "trash_links" }),
                new UndoAction("trash.restore_unit",
                    JsonSerializer.SerializeToElement(new { unit_id = newFolder.FolderId, target_parent_id = target })))]);
    }

    private static async Task CopyChildrenAsync(
        Kernel.IUnitOfWork uow, IReadOnlyList<Folder> allFolders,
        string sourceParentId, string destParentId,
        IReadOnlyDictionary<string, List<Link>> linksByFolder, SiblingNameTable naming, CancellationToken ct)
    {
        foreach (var child in allFolders.Where(f => f.ParentId == sourceParentId))
        {
            var newChild = new Folder
            {
                // 子层同样经命名服务（占用表按目标父层累积；源子树不满足同层唯一也不至于整条命令被索引拒绝）
                Name = naming.Resolve(destParentId, child.Name),
                Description = child.Description,
                ParentId = destParentId,
                LinkCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = await uow.Folders.AddAsync(newChild, ct);
            await CopyLinksAsync(uow, child.FolderId, newChild.FolderId, linksByFolder, ct);
            await CopyChildrenAsync(uow, allFolders, child.FolderId, newChild.FolderId, linksByFolder, naming, ct);
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

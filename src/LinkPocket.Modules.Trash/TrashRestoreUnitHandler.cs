using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.restore_unit（Mutation）：还原一个**回收站单元**——整棵被删文件夹子树（含子单元与单元内书签快照），
/// 全部保留原 ID（与回收站"保留原 ID"的既有口径一致，还原后引用与选中可继续追踪）。
/// 落点：<c>target_parent_id</c> 指定；缺省 = 根级；**原父已不存在时回落根级并在结果里如实回报**
/// （不静默假装还原到了原位）。
/// 新增理由（2026-09-19）：回收站此前只有**链接**还原，文件夹删除完全不可撤销；
/// 本命令是 <c>folders.delete</c>（cascade=trash_links）的逆向，使"删文件夹"可被 Ctrl+Z 撤销。
/// </summary>
internal sealed class TrashRestoreUnitHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.restore_unit",
        Category: "trash",
        Description: "还原回收站单元（整棵被删文件夹子树 + 单元内书签；保留原 ID；缺省落根级）",
        Parameters:
        [
            ParamSpec.Req<string>("unit_id", "回收站单元 ID（= 原文件夹 ID）"),
            ParamSpec.Opt<string>("target_parent_id", "还原到的父目录 ID；缺省 = 根级；原父已不存在时回落根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        Impact: ImpactSummary.Folder);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var unitId = CommandArgs.RequireString(args, "unit_id");
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var all = await uow.Trash.ListFoldersAsync(ct);
        if (!all.Any(f => string.Equals(f.TrashFolderId, unitId, StringComparison.Ordinal)))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"回收站单元 {unitId} 不存在", correlationId: ctx.CorrelationId));

        var ids = TrashSupport.CollectSubtreeIds(all, unitId);
        var idSet = ids.ToHashSet(StringComparer.Ordinal);

        // 落点校验：请求了原位还原但原父已不存在 → 回落根级（如实回报，见 fell_back_to_root）
        var targetExists = target != null && await uow.Folders.FindAsync(new FolderId(target), ct) != null;
        var landing = targetExists ? target : null;

        // 重建文件夹（保留原 ID）。根单元先落（其父 = 落点），子单元的父 = 回收站里记录的父
        //（回收站镜像了原层级，且 ID 保留 → 直接就是原父 ID）。
        var units = all.Where(f => idSet.Contains(f.TrashFolderId)).ToList();
        var ordered = units.Where(u => string.Equals(u.TrashFolderId, unitId, StringComparison.Ordinal))
            .Concat(units.Where(u => !string.Equals(u.TrashFolderId, unitId, StringComparison.Ordinal)));

        // 落点层同层唯一命名（Windows 口径）：只有「单元根」会落到已有内容的目录，撞名 → 「名 (2)」。
        // 经单一命名服务的占用表：落点层先预置库里既有名，子单元层在下面随还原逐项累积（都不用查库）。
        var naming = await uow.Naming.CreateTableAsync(landing, ct);
        var rootUnitName = ordered.First().Name;
        var rootName = naming.Resolve(landing, rootUnitName);
        var renameNote = string.Equals(rootName, rootUnitName, StringComparison.Ordinal)
            ? string.Empty
            : $"（重命名：「{rootUnitName}」→「{rootName}」）";

        var restoredFolders = 0;
        foreach (var unit in ordered)
        {
            var isRoot = string.Equals(unit.TrashFolderId, unitId, StringComparison.Ordinal);
            // 子单元：父 = 本次一起还原的单元（回收站镜像原层级且 ID 保留），其内部原本满足同层唯一；
            // 仍经占用表兜底——坏数据/异常来源的重名在这里被编号，而不是撞 v4 唯一索引让整条还原失败。
            var unitName = isRoot ? rootName : naming.Resolve(unit.ParentTrashFolderId, unit.Name);
            _ = await uow.Folders.AddAsync(new Folder
            {
                FolderId = unit.TrashFolderId,     // 保留原 ID
                Name = unitName,
                ParentId = isRoot ? landing : unit.ParentTrashFolderId,
                LinkCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }, ct);
            restoredFolders++;
        }

        // 还原单元内书签快照（TrashFolderId = 它原本所属的文件夹，ID 保留 → 落回原文件夹）
        var restoredLinks = 0;
        foreach (var id in ids)
        {
            foreach (var snapshot in await uow.Trash.ListLinksByUnitAsync(new TrashFolderId(id), ct))
            {
                _ = await uow.Links.AddAsync(new Link
                {
                    LinkId = snapshot.LinkId,      // 保留原 ID
                    Url = snapshot.Url,
                    Title = snapshot.Title,
                    Description = snapshot.Description,
                    FaviconUrl = snapshot.FaviconUrl,
                    ListId = snapshot.TrashFolderId,
                    LastVisitedAt = snapshot.LastVisitedAt,
                    VisitCount = snapshot.VisitCount,
                    IsImportant = snapshot.IsImportant,
                    CreatedAt = snapshot.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                }, ct);
                await uow.Trash.RemoveLinkAsync(new LinkId(snapshot.LinkId), ct);
                restoredLinks++;
            }
        }

        // 单元出回收站 + 计数回填（直接用 Kernel 计数，不依赖其它模块的 internal 支撑——模块间黑盒）
        foreach (var id in ids)
            await uow.Trash.RemoveFolderAsync(new TrashFolderId(id), ct);
        var counts = await uow.Links.CountByFolderAsync(ct);
        foreach (var id in ids)
        {
            var folder = await uow.Folders.FindAsync(new FolderId(id), ct);
            if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(id));
        }

        await uow.Trees.TouchModifiedAsync(landing == null ? null : new FolderId(landing), ct);

        var location = landing == null
            ? FolderIds.RootDisplayName
            : await uow.Trees.PathDisplayAsync(new FolderId(landing), ct);
        return CommandResult.Ok(
            new TrashRestoreUnitResult(unitId, landing, restoredFolders, restoredLinks, target != null && !targetExists),
            new ChangeSet(
                Touched: [new EntityRef("folder", unitId)],
                Events: [LinkPocket.Contracts.DomainEventNames.FoldersChanged,
                         LinkPocket.Contracts.DomainEventNames.LinksChanged,
                         LinkPocket.Contracts.DomainEventNames.TrashChanged],
                HumanSummary: $"已还原文件夹单元（{restoredFolders} 个文件夹 / {restoredLinks} 个链接）到「{location}」{renameNote}"),
            // 撤销"还原单元" = 再次删除该单元（回到回收站）——逆向参数带单元 ID
            [new UndoInverseStep("folders.delete",
                JsonSerializer.SerializeToElement(new { folder_id = unitId, cascade = "trash_links" }))]);
    }
}

/// <summary>还原结果：落点 + 恢复规模 + 是否因原父消失而回落根级（如实回报，绝不静默）。</summary>
public sealed record TrashRestoreUnitResult(
    string UnitId, string? TargetParentId, int FolderCount, int LinkCount, bool FellBackToRoot);

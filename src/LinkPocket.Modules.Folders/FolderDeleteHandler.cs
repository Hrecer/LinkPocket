using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.delete（Mutation）：删除文件夹。级联三模式（既有口径）：
/// trash_links（默认）= 整子树镜像进回收站（Windows 式；回收站保留原 ID + 位置快照）；
/// delete_all = 连链接一起物理删除；move_to_list = 链接转移到目标文件夹后删除空子树。
/// 删除确认在 UI 层（ConfirmDialog 唯一入口），协议层不打 Destructive 标志（行为等价项）。
/// </summary>
internal sealed class FolderDeleteHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.delete",
        Category: "folders",
        Description: "Delete a folder (the whole subtree goes to trash by default; cascade may be delete_all / move_to_list)",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "Folder ID"),
            ParamSpec.Opt<string>("cascade", "trash_links (default) | delete_all | move_to_list",
                enumValues: ["trash_links", "delete_all", "move_to_list"]),
            ParamSpec.Opt<string>("target_list_id", "Target folder ID for move_to_list mode"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible,
        UndoInverse: null,   // 逆向参数需计算（且仅 trash_links 可逆）→ 由处理器回填，见下方 undo
        Impact: ImpactSummary.Folder,
        Client: new("FolderDeleteAsync", typeof(FolderDeleteResult)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var cascade = CommandArgs.OptionalString(args, "cascade") ?? "trash_links";
        var targetListId = CommandArgs.OptionalString(args, "target_list_id");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var allFolders = await uow.Folders.ListAllAsync(ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == id.Value)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"folder {id} does not exist", correlationId: ctx.CorrelationId));

        // 后代集合（含自身）
        var descendantIds = new List<string>();
        FolderSupport.CollectDescendantIds(allFolders, id.Value, descendantIds);
        descendantIds.Add(id.Value);
        var descendantSet = descendantIds.ToHashSet(StringComparer.Ordinal);
        var subtreeFolders = allFolders.Where(f => descendantSet.Contains(f.FolderId)).ToList();

        // 受影响链接：**按子树范围下推查询**（idx_links_folder；一次取齐，不再全表读回来内存过滤——
        // 大库上"删一个文件夹 = 全表物化 2 万+ 链接"是用户实测的删除卡顿主因之一）。
        var affectedLinks = (await uow.Links.ListAsync(new LinkQuerySpec
        {
            Filter = new LinkFilter { FolderIdsIn = descendantIds },
        }, ct)).ToList();

        // OriginPath 按目录缓存：同目录里的每一条链接路径相同，逐条重算 = 每条一次全目录表读
        //（万条 subtree 实测把删除拖到几十秒，见 WARNINGS）。
        var pathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<string> PathOfAsync(FolderId? folder)
        {
            var key = folder?.Value ?? "\u0000root";   // 空键不代表任何 ID；根（null）单独占一个键
            if (pathCache.TryGetValue(key, out var cached)) return cached;
            var path = await uow.Trees.PathCanonicalAsync(folder, ct);
            pathCache[key] = path;
            return path;
        }

        var trashedLinkCount = 0;
        var diff = new List<FieldChange>();
        // 子树搬运含 **set-based SQL**（回收站镜像/物理删除；见 TrashLinksOfFoldersAsync）——
        // raw SQL 没有"待提交"这回事，必须与 EF 登记同属一个显式事务：没有外层事务（干跑/事务批自开）就自建，
        // 否则 raw SQL 先自动提交、后续 EF 失败会留下"半搬"状态（文件夹还在、链接已清）。
        var ownsTx = !uow.HasActiveTransaction;
        ITransactionScope? tx = null;
        if (ownsTx)
        {
            tx = uow.BeginTransaction();
            await tx.BeginAsync(ct);
        }
        try
        {
        switch (cascade)
        {
            case "delete_all":
                foreach (var link in affectedLinks)
                    diff.AddRange(EntityDiff.Diff(link.LinkId, LinkSnapshot.Of(link), null));   // 物理删除 = 原值快照
                await uow.Links.RemoveRangeAsync(affectedLinks, ct);   // 批量登记删除（逐条 RemoveAsync = 每行一条 SELECT）
                foreach (var f in subtreeFolders)
                    diff.AddRange(EntityDiff.Diff(f.FolderId, FolderSnapshot.Of(f), null));
                await uow.Folders.RemoveRangeAsync(subtreeFolders, ct);
                break;

            case "move_to_list":
                if (string.IsNullOrEmpty(targetListId))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.RequiredParam, "move_to_list mode requires target_list_id", correlationId: ctx.CorrelationId));
                var target = await uow.Folders.FindAsync(new FolderId(targetListId), ct)
                    ?? throw new EngineException(EngineErrors.Of(
                        EngineErrors.EntityNotFound, $"target folder {targetListId} does not exist", correlationId: ctx.CorrelationId));
                if (descendantSet.Contains(targetListId))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.CycleDetected,
                        $"target folder '{target.Name}' is inside the subtree being deleted, so moved links would be deleted with it", correlationId: ctx.CorrelationId));

                foreach (var link in affectedLinks)
                {
                    var before = LinkSnapshot.Of(link);
                    link.ListId = targetListId;
                    diff.AddRange(EntityDiff.Diff(link.LinkId, before, LinkSnapshot.Of(link)));   // 链接转移 = 归属变更
                    await uow.Links.UpdateAsync(link, ct);
                }

                foreach (var f in subtreeFolders)
                    diff.AddRange(EntityDiff.Diff(f.FolderId, FolderSnapshot.Of(f), null));
                await uow.Folders.RemoveRangeAsync(subtreeFolders, ct);
                await RefreshLinkCountAsync(uow, targetListId, ct);
                break;

            default: // trash_links：整子树镜像进回收站
                var trashedAt = DateTime.UtcNow;
                var subtreeIdSet = subtreeFolders.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);
                var folderPaths = new List<(string FolderId, string OriginPath)>(subtreeFolders.Count);

                foreach (var f in subtreeFolders)
                {
                    // **幂等守卫**：回收站键 = 原文件夹 ID（TrashedFolder.TrashFolderId）——同一文件夹
                    // 不可能二进回收站。批/宏的嵌套步骤共享同一 UoW，脚本里若出现重复 ID（AI 用 {ref}
                    // 拼错、或两次删除同一实体），第二次 Add 会在 EF 身份映射里撞同键，直接把
                    // InvalidOperationException 冒到 UI 线程（实测弹"界面异常，建议重启"）。这里显式拦截：
                    // 已在回收站 = 如实报 EntityNotFound（批整体回滚，语义正确），绝不静默二次入站。
                    if (await uow.Trash.FindFolderAsync(new TrashFolderId(f.FolderId), ct) is not null)
                        throw new EngineException(EngineErrors.Of(
                            EngineErrors.EntityNotFound,
                            $"folder {f.FolderId} is already in the trash (duplicate delete in the same batch?)",
                            correlationId: ctx.CorrelationId));

                    diff.AddRange(EntityDiff.Diff(f.FolderId, FolderSnapshot.Of(f), null));   // 删除 = 原位置与身份快照
                    var folderPath = await PathOfAsync(new FolderId(f.FolderId));
                    folderPaths.Add((f.FolderId, folderPath));
                    _ = await uow.Trash.AddFolderAsync(new TrashedFolder
                    {
                        TrashFolderId = f.FolderId,   // 回收站保留原 ID
                        ParentTrashFolderId = f.ParentId != null && subtreeIdSet.Contains(f.ParentId) ? f.ParentId : null,
                        Name = f.Name,
                        OriginFolderId = f.FolderId,
                        OriginParentFolderId = f.ParentId,   // v5：原父目录（原位还原的数据依据；NULL = 原在根）
                        OriginPath = folderPath,
                        Description = f.Description,
                        SortOrder = f.SortOrder,
                        CreatedAt = f.CreatedAt,
                        LastVisitedAt = f.LastVisitedAt,
                        VisitCount = f.VisitCount,
                        DeletedAt = trashedAt,
                    }, ct);
                }

                // 链接搬运走 set-based SQL（一个目录一条 INSERT..SELECT + 一条 DELETE）：
                // 万行子树逐行 EF 登记实测 2s+（快照对象 + 跟踪器 + 提交批），本路径只留 SQL 一层；
                // **快照列逐列一致**（UpdatedAt = 本次删除时刻、OriginPath = 所在目录路径、trash_folder_id = 原目录）。
                trashedLinkCount = await uow.Trash.TrashLinksOfFoldersAsync(folderPaths, trashedAt, ct);
                foreach (var link in affectedLinks)
                    diff.AddRange(EntityDiff.Diff(link.LinkId, LinkSnapshot.Of(link), null));   // 进回收站 = 原位置与身份快照（审计/撤销台账口径不变）

                await uow.Folders.RemoveRangeAsync(subtreeFolders, ct);
                break;
        }

        // 删除 → 原父级内容有变；move_to_list 目标文件夹也变了
        await uow.Trees.TouchModifiedAsync(
            folder.ParentId == null ? null : new FolderId(folder.ParentId), ct);
        if (cascade == "move_to_list" && !string.IsNullOrEmpty(targetListId))
            await uow.Trees.TouchModifiedAsync(new FolderId(targetListId), ct);

        if (ownsTx)
        {
            await uow.CommitAsync(ct);   // EF 登记（回收站单元行 / 文件夹删除 / 计数触碰）
            await tx!.CommitAsync(ct);   // 连同 raw SQL 一起提交（同提同滚）
        }
        }
        catch
        {
            if (ownsTx && tx != null)
            {
                try { await tx.RollbackAsync(ct); } catch { /* 回滚失败不覆盖原始异常 */ }
            }
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }

        var events = new List<string> { LinkPocket.Contracts.DomainEventNames.FoldersChanged, LinkPocket.Contracts.DomainEventNames.LinksChanged };
        if (cascade == "trash_links") events.Add(LinkPocket.Contracts.DomainEventNames.TrashChanged);

        // 撤销载荷：**仅 trash_links（整子树进回收站）可撤销** —— 逆向 = 还原该单元回**原父目录**
        // （v5：origin_parent_folder_id 是数据事实，回填 to: origin 即可，不再传原父 ID）。
        // delete_all（物理删除）与 move_to_list（链接已转移、空子树已删）**不可逆** → 不发载荷，不入撤销栈。
        var undo = cascade == "trash_links"
            ? new[]
            {
                new UndoInverseStep("trash.restore_unit",
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new { unit_id = id.Value, to = "origin" }))
            }
            : null;

        return CommandResult.Ok(
            new FolderDeleteResult(cascade, subtreeFolders.Count, trashedLinkCount),
            new ChangeSet(
                Touched: [new EntityRef("folder", id.Value)],
                Events: events,
                HumanSummary: $"Folder '{folder.Name}' deleted ({cascade})",
                Diff: diff.Count > 0 ? diff : null),
            undo);
    }

    /// <summary>直接子链接计数缓存回填（LinkCount 列的既有维护口径）。</summary>
    internal static async Task RefreshLinkCountAsync(Kernel.IUnitOfWork uow, string folderId, CancellationToken ct)
    {
        var counts = await uow.Links.CountByFolderAsync(ct);
        var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
        if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(folderId));
    }
}

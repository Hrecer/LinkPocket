using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// 还原的**唯一流水线**（trash.restore / trash.restore_unit / trash.restore_batch 三命令共用同一实现）：
/// 预检 → 先单元后链接 → 落点解析 → 命名编号 → 计数回填 → 撤销载荷全部只在这里（同类行为只有一条流水线）。
///
/// <para><b>落点解析（唯一算法）</b>：<c>to = "origin"</c>（缺省）→ 落回删除前位置（快照 origin）。
/// origin 为 NULL = 原在根（正常路径；最极端兜底同此语义——v5 后正常进站的单元必有记录）；
/// 原位置已不存在（被删/被永久删除）→ 落根 + <c>fell_back_to_root</c> 如实回报（不静默假装落回原位）。
/// <c>to = "root"</c> → 显式落根。</para>
///
/// <para><b>顺序</b>：先单元、后链接——链接的 origin 可能是同批还原的单元（此时按 ID 落回该目录，
/// 与单元是否被编号改名无关）；父子单元同选时子被父覆盖（先标准化，处理顺序无关）。</para>
///
/// <para><b>批内可见性</b>：EF 仓储查询只见已提交状态，批内新增对后续查询不可见——因此
/// "同批已还原/同批已有同 URL"一律由本流水线**自记账**（集合/清单），绝不依赖"再查一次库"。</para>
///
/// <para><b>原子性</b>：任一项硬失败（回收站中不存在 / 主表同 ID 冲突）→ 抛错 → 引擎单事务整批回滚。</para>
/// </summary>
internal static class TrashRestoreSupport
{
    public const string ToOrigin = "origin";
    public const string ToRoot = "root";

    /// <summary>三命令共用的 to 读取：缺省 origin；取值越界 → LP.VAL.003。</summary>
    public static string ReadLandingMode(JsonElement args)
        => ValidateLandingMode(CommandArgs.OptionalString(args, "to") ?? ToOrigin);

    public static string ValidateLandingMode(string to)
    {
        if (to is not (ToOrigin or ToRoot))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange,
                $"parameter 'to' has an invalid value: {to} (expected {ToOrigin} | {ToRoot})",
                JsonSerializer.SerializeToElement(new { @param = "to", value = to })));
        return to;
    }

    public static async Task<TrashRestoreOutcome> RestoreAsync(
        ICommandContext ctx,
        IReadOnlyList<string> linkIds,
        IReadOnlyList<string> folderIds,
        string to,
        string? explicitParent,
        CancellationToken ct)
    {
        var uow = ctx.Uow;

        // —— 0. 预检（硬失败 → 整批回滚 + 指明失败 id；坏数据绝不覆盖既有实体）——
        foreach (var id in linkIds)
        {
            if (await uow.Trash.FindLinkAsync(new LinkId(id), ct) == null)
                throw NotFound($"bookmark {id} is not in the trash", ctx);
            if (await uow.Links.FindAsync(new LinkId(id), ct) != null)
                throw Conflict($"bookmark {id} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);
        }

        var allUnits = await uow.Trash.ListFoldersAsync(ct);
        var unitById = allUnits.ToDictionary(u => u.TrashFolderId, StringComparer.Ordinal);
        foreach (var id in folderIds)
        {
            if (!unitById.ContainsKey(id))
                throw NotFound($"trash unit {id} does not exist", ctx);
            if (await uow.Folders.FindAsync(new FolderId(id), ct) != null)
                throw Conflict($"folder {id} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);
        }
        if (explicitParent != null && await uow.Folders.FindAsync(new FolderId(explicitParent), ct) == null)
            throw NotFound($"target folder {explicitParent} does not exist", ctx);

        // 批内记账（EF 查询看不到未提交的新增 → "同批已还原"必须自记账）
        var restoredFolderIds = new List<string>();
        var restoredFolderIdSet = new HashSet<string>(StringComparer.Ordinal);
        var restoredLinkIds = new HashSet<string>(StringComparer.Ordinal);
        var restoredLinks = new List<RestoredLinkInfo>();
        var restoredUnits = new List<RestoredUnitInfo>();
        var fellBackToRoot = new List<string>();
        var renamed = new List<TrashRestoreRename>();
        var undoSteps = new List<UndoInverseStep>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var namingTables = new Dictionary<string, SiblingNameTable>(StringComparer.Ordinal);

        // —— 1. 先单元（选择顺序；父子同选 → 子被父覆盖）——
        foreach (var unitId in SelectEffectiveUnits(allUnits, folderIds))
        {
            var root = unitById[unitId];
            var subtreeIds = TrashSupport.CollectSubtreeIds(allUnits, unitId);
            var subtreeSet = subtreeIds.ToHashSet(StringComparer.Ordinal);

            string? landing;
            bool fellBack;
            if (explicitParent != null)
            {
                (landing, fellBack) = (explicitParent, false);
            }
            else
            {
                (landing, fellBack) = await ResolveLandingAsync(uow, to, root.OriginParentFolderId, restoredFolderIdSet, ct);
            }

            if (fellBack) fellBackToRoot.Add(root.Name);

            // 落点层共享占用表（批内两个同名单元 → 「资料」「资料 (2)」）；子层随还原逐项累积
            var tableKey = landing ?? string.Empty;
            if (!namingTables.TryGetValue(tableKey, out var naming))
                naming = namingTables[tableKey] = await uow.Naming.CreateTableAsync(landing, ct);

            foreach (var member in OrderByHierarchy(allUnits, root, subtreeSet))
            {
                if (restoredFolderIdSet.Contains(member.TrashFolderId)) continue;   // 防御：批内已还原（正常不可达）
                var isRoot = string.Equals(member.TrashFolderId, unitId, StringComparison.Ordinal);
                var name = isRoot
                    ? naming.Resolve(landing, member.Name)
                    : naming.Resolve(member.ParentTrashFolderId, member.Name);
                if (!string.Equals(name, member.Name, StringComparison.Ordinal))
                    renamed.Add(new TrashRestoreRename(member.Name, name));

                if (await uow.Folders.FindAsync(new FolderId(member.TrashFolderId), ct) != null)
                    throw Conflict($"folder {member.TrashFolderId} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);

                _ = await uow.Folders.AddAsync(new Folder
                {
                    FolderId = member.TrashFolderId,   // 保留原 ID
                    Name = name,
                    ParentId = isRoot ? landing : member.ParentTrashFolderId,
                    LinkCount = 0,
                    Description = member.Description,
                    SortOrder = member.SortOrder,
                    CreatedAt = member.CreatedAt ?? DateTime.UtcNow,   // 快照缺失（v5 前进站）如实给当前时间，不伪造
                    LastVisitedAt = member.LastVisitedAt,
                    VisitCount = member.VisitCount,
                    UpdatedAt = DateTime.UtcNow,
                }, ct);
                restoredFolderIds.Add(member.TrashFolderId);
                restoredFolderIdSet.Add(member.TrashFolderId);
            }

            // 单元内链接：按 TrashFolderId 镜像回填（保留原 ID/全字段，不逐条评估 origin——origin 只决定单元根落点）
            foreach (var subId in subtreeIds)
            {
                foreach (var snapshot in await uow.Trash.ListLinksByUnitAsync(new TrashFolderId(subId), ct))
                {
                    if (restoredLinkIds.Contains(snapshot.LinkId)) continue;   // 防御（正常不可达）
                    if (await uow.Links.FindAsync(new LinkId(snapshot.LinkId), ct) != null)
                        throw Conflict($"bookmark {snapshot.LinkId} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);
                    var duplicate = await DuplicatedAtAsync(uow, restoredLinks, snapshot.TrashFolderId, snapshot.Url, ct);
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
                    restoredLinks.Add(new RestoredLinkInfo(
                        snapshot.LinkId, snapshot.Url, snapshot.Title, snapshot.TrashFolderId, false, duplicate));
                    restoredLinkIds.Add(snapshot.LinkId);
                }
            }

            foreach (var id in subtreeIds)
                await uow.Trash.RemoveFolderAsync(new TrashFolderId(id), ct);
            restoredUnits.Add(new RestoredUnitInfo(unitId, landing, fellBack));
            if (landing != null) touched.Add(landing);

            // 撤销载荷：每单元一条 folders.delete（级联回回收站）；重做显式 = 再还原该单元（保留原 ID）。
            // 覆盖去重：落点已在**同批先还原的单元**树内（如"先删子、后删父"再一起还原）→ 该祖先单元的
            // 撤销步（再删回回收站）已覆盖本单元——两条重叠步会在撤销/重做时对同一实体双次处理（冲突），
            // 故不单独发步（外层单元再删/再还原时本单元随行，位置保真）。
            if (landing == null || !restoredFolderIdSet.Contains(landing))
            {
                undoSteps.Add(new UndoInverseStep("folders.delete",
                    JsonSerializer.SerializeToElement(new { folder_id = unitId, cascade = "trash_links" }),
                    new UndoAction("trash.restore_unit",
                        JsonSerializer.SerializeToElement(new { unit_id = unitId, to = ToOrigin }))));
            }
        }

        // —— 2. 后链接（此时再评估 origin：原目录若是同批刚还原的单元，按 ID 落回）——
        foreach (var id in linkIds)
        {
            if (restoredLinkIds.Contains(id)) continue;   // 随同批单元一并还原 → 去重
            var snapshot = await uow.Trash.FindLinkAsync(new LinkId(id), ct)
                ?? throw NotFound($"bookmark {id} is not in the trash", ctx);   // 预检已保证；防御

            var (landing, fellBack) = await ResolveLandingAsync(uow, to, snapshot.OriginListId, restoredFolderIdSet, ct);
            if (fellBack) fellBackToRoot.Add(snapshot.Title ?? snapshot.Url);

            var duplicate = await DuplicatedAtAsync(uow, restoredLinks, landing, snapshot.Url, ct);
            _ = await uow.Links.AddAsync(new Link
            {
                LinkId = snapshot.LinkId,      // 保留原 ID
                Url = snapshot.Url,
                Title = snapshot.Title,
                Description = snapshot.Description,
                FaviconUrl = snapshot.FaviconUrl,
                ListId = landing,
                LastVisitedAt = snapshot.LastVisitedAt,
                VisitCount = snapshot.VisitCount,
                IsImportant = snapshot.IsImportant,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            }, ct);
            await uow.Trash.RemoveLinkAsync(new LinkId(id), ct);
            restoredLinks.Add(new RestoredLinkInfo(snapshot.LinkId, snapshot.Url, snapshot.Title, landing, fellBack, duplicate));
            restoredLinkIds.Add(id);
            if (landing != null) touched.Add(landing);

            // 撤销载荷：每链接一条 links.trash；重做显式 = 再还原该链接（保留原 ID）。
            // 覆盖去重：落点在同批已还原的单元树内（含 C2 落回单元的链接）→ 该单元的撤销步已覆盖本链接
            //（同上：重叠步会在撤销/重做时对同一实体双次处理）。
            if (landing == null || !restoredFolderIdSet.Contains(landing))
            {
                undoSteps.Add(new UndoInverseStep("links.trash",
                    JsonSerializer.SerializeToElement(new { id }),
                    new UndoAction("trash.restore",
                        JsonSerializer.SerializeToElement(new { id, to = ToOrigin }))));
            }
        }

        // —— 3. 收尾：落点 Touch（同目录只一次）+ 计数回填 ——
        foreach (var landing in touched)
            await uow.Trees.TouchModifiedAsync(new FolderId(landing), ct);
        if (restoredFolderIds.Count > 0)
        {
            var counts = await uow.Links.CountByFolderAsync(ct);
            foreach (var id in restoredFolderIds)
            {
                var folder = await uow.Folders.FindAsync(new FolderId(id), ct);
                if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(id));
            }
        }

        return new TrashRestoreOutcome(restoredLinks, restoredUnits, restoredFolderIds, fellBackToRoot, renamed, undoSteps);
    }

    /// <summary>父子同选标准化：任一所选单元的祖先也在所选集合内 → 该单元被祖先覆盖（只处理祖先，顺序无关）。</summary>
    private static List<string> SelectEffectiveUnits(IReadOnlyList<TrashedFolder> allUnits, IReadOnlyList<string> folderIds)
    {
        var selected = folderIds.ToHashSet(StringComparer.Ordinal);
        var byId = allUnits.ToDictionary(u => u.TrashFolderId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effective = new List<string>();
        foreach (var id in folderIds)
        {
            if (!seen.Add(id)) continue;   // 输入去重（保序）
            var covered = false;
            var parent = byId[id].ParentTrashFolderId;
            var guard = 0;
            while (parent != null && guard++ < 10_000)   // guard 防坏数据环
            {
                if (selected.Contains(parent)) { covered = true; break; }
                parent = byId.TryGetValue(parent, out var unit) ? unit.ParentTrashFolderId : null;
            }
            if (!covered) effective.Add(id);
        }
        return effective;
    }

    /// <summary>子树还原顺序：根先落、父必先于子（FK 要求父行先存在；同层保持快照顺序）。</summary>
    private static List<TrashedFolder> OrderByHierarchy(
        IReadOnlyList<TrashedFolder> allUnits, TrashedFolder root, HashSet<string> subtreeSet)
    {
        var ordered = new List<TrashedFolder> { root };
        var added = new HashSet<string>(StringComparer.Ordinal) { root.TrashFolderId };
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var unit in allUnits)
            {
                if (added.Contains(unit.TrashFolderId) || !subtreeSet.Contains(unit.TrashFolderId)) continue;
                if (unit.ParentTrashFolderId != null && added.Contains(unit.ParentTrashFolderId))
                {
                    ordered.Add(unit);
                    added.Add(unit.TrashFolderId);
                    progress = true;
                }
            }
        }
        return ordered;
    }

    /// <summary>落点解析（唯一算法）：root → 根；origin → 快照原位置（NULL = 原在根；缺失 → 落根 + 回落标记）。</summary>
    private static async Task<(string? Landing, bool FellBack)> ResolveLandingAsync(
        IUnitOfWork uow, string to, string? originParentId, HashSet<string> batchRestoredFolders, CancellationToken ct)
    {
        if (to == ToRoot) return (null, false);
        if (originParentId == null) return (null, false);   // 原在根（正常路径；最极端兜底同此语义）
        if (batchRestoredFolders.Contains(originParentId)) return (originParentId, false);   // 同批刚还原（批内不可查库）
        if (await uow.Folders.FindAsync(new FolderId(originParentId), ct) != null) return (originParentId, false);
        return (null, true);   // 原位置已不存在 → 落根 + 如实回报
    }

    /// <summary>落点处是否已有同 URL 的其他链接（信息性计数，D7）——含同批先还原的（批内不可查库）。</summary>
    private static async Task<bool> DuplicatedAtAsync(
        IUnitOfWork uow, IReadOnlyList<RestoredLinkInfo> batchRestored, string? landing, string url, CancellationToken ct)
    {
        if (batchRestored.Any(l => string.Equals(l.Landing, landing, StringComparison.Ordinal)
                                   && string.Equals(l.Url, url, StringComparison.Ordinal)))
            return true;
        return (await uow.Links.FindByUrlAsync(url, ct))
            .Any(l => string.Equals(l.ListId, landing, StringComparison.Ordinal));
    }

    private static EngineException NotFound(string message, ICommandContext ctx)
        => new(EngineErrors.Of(EngineErrors.EntityNotFound, message, correlationId: ctx.CorrelationId));

    /// <summary>主表同 ID 冲突（坏数据防御）——按数据错误上报；绝不覆盖既有实体。</summary>
    private static EngineException Conflict(string message, ICommandContext ctx)
        => new(EngineErrors.Of(EngineErrors.DbError, message, correlationId: ctx.CorrelationId));
}

/// <summary>流水线产出（三命令各自投影成自己的结果 DTO）。</summary>
internal sealed record TrashRestoreOutcome(
    IReadOnlyList<RestoredLinkInfo> Links,
    IReadOnlyList<RestoredUnitInfo> Units,
    IReadOnlyList<string> RestoredFolderIds,
    IReadOnlyList<string> FellBackToRoot,
    IReadOnlyList<TrashRestoreRename> Renamed,
    IReadOnlyList<UndoInverseStep> UndoSteps);

/// <summary>一条被还原的链接：落点 + 是否回落根 + 落点处是否已有同 URL（信息性）。</summary>
internal sealed record RestoredLinkInfo(
    string LinkId, string Url, string? Title, string? Landing, bool FellBackToRoot, bool Duplicate);

/// <summary>一个被还原的单元（选择项；含子单元的还原行数见 RestoredFolderIds）。</summary>
internal sealed record RestoredUnitInfo(string UnitId, string? Landing, bool FellBackToRoot);

using System.Diagnostics;
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
        // 批/干跑：复用外层事务（批引擎负责提交/回滚）
        if (uow.HasActiveTransaction)
            return await RestoreCoreAsync(ctx, linkIds, folderIds, to, explicitParent, ct);

        // 单命令 / 撤销步骤：自建事务，让集合化 SQL 与 EF 登记同提同滚
        var tx = uow.BeginTransaction();
        await tx.BeginAsync(ct);
        try
        {
            var outcome = await RestoreCoreAsync(ctx, linkIds, folderIds, to, explicitParent, ct);
            await uow.CommitAsync(ct);   // EF 侧收尾（TouchModified 等）
            await tx.CommitAsync(ct);
            return outcome;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* 回滚失败不覆盖原始异常 */ }
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static async Task<TrashRestoreOutcome> RestoreCoreAsync(
        ICommandContext ctx,
        IReadOnlyList<string> linkIds,
        IReadOnlyList<string> folderIds,
        string to,
        string? explicitParent,
        CancellationToken ct)
    {
        var uow = ctx.Uow;
        // 阶段计时（Debug 才开）：复杂撤销"到底贵在哪一段"的取证入口；正式级别零开销。
        var phaseWatch = LpLog.IsEnabled(LogLevel.Debug) ? Stopwatch.StartNew() : null;
        var phaseLast = 0L;
        void Phase(string name)
        {
            if (phaseWatch is null) return;
            var now = phaseWatch.ElapsedMilliseconds;
            LpLog.Write(LogLevel.Debug, "engine.trash",
                $"restore phase={name} dt={now - phaseLast}ms total={now}ms links={linkIds.Count} units={folderIds.Count}");
            phaseLast = now;
        }

        // —— 0. 预检（硬失败 → 整批回滚 + 指明失败 id；坏数据绝不覆盖既有实体）——
        // 快照留在本地复用：还原阶段不再对同一条链接二次查库（大条目撤销/批量还原的单步成本）。
        var loadedSnapshots = await uow.Trash.ListLinksByIdsAsync(
            linkIds.Select(id => new LinkId(id)).ToArray(), ct);
        var loadedById = loadedSnapshots.ToDictionary(s => s.LinkId, StringComparer.Ordinal);
        var linkSnapshots = new Dictionary<string, TrashedLink>(linkIds.Count, StringComparer.Ordinal);
        foreach (var id in linkIds)
        {
            if (!loadedById.TryGetValue(id, out var snapshot))
                throw NotFound($"bookmark {id} is not in the trash", ctx);
            linkSnapshots[id] = snapshot;
        }
        // 主表同 ID 冲突检查：一次 `id in (...)`（原实现每条一次 FindAsync——批量还原/重做时是线性点查）
        if (linkSnapshots.Count > 0)
        {
            var existingIds = await uow.Links.ListExistingIdsAsync(linkIds, ct);   // 原生 reader 直读，免 EF 编译/物化
            if (existingIds.Count > 0)
            {
                var conflictSet = existingIds.ToHashSet(StringComparer.Ordinal);
                var conflictId = linkIds.FirstOrDefault(conflictSet.Contains) ?? existingIds[0];
                throw Conflict($"bookmark {conflictId} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);
            }
        }
        Phase("precheck");

        // 纯链接还原（撤销 links.trash / 批量还原链接）不碰单元：整张回收站文件夹表读回来纯属浪费。
        var allUnits = folderIds.Count > 0 ? await uow.Trash.ListFoldersAsync(ct) : [];
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
        // 批内重复判据（落点 + URL）：原实现对每条链接扫已还原清单（O(n²)），400 条尚可、
        // 上千条就是纯浪费；改用集合判重，语义不变。
        var restoredKeys = new HashSet<(string? Landing, string Url)>();
        var restoredUnits = new List<RestoredUnitInfo>();
        var fellBackToRoot = new List<string>();
        var renamed = new List<TrashRestoreRename>();
        var undoSteps = new List<UndoInverseStep>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var namingTables = new Dictionary<string, SiblingNameTable>(StringComparer.Ordinal);
        var diff = new List<FieldChange>();   // 字段级 diff：还原 = 实体重新回到主表（Before 为"不适用"，After 为落点后的现值）

        // —— 1. 先单元（选择顺序；父子同选 → 子被父覆盖）——
        HashSet<string>? mainFolderIds = null;   // 大单元：一次全量读替代逐文件夹点查（懒加载、跨单元复用）
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
                (landing, fellBack) = await ResolveLandingAsync(uow, to, root.OriginParentFolderId, restoredFolderIdSet, null, ct);
            }

            if (fellBack) fellBackToRoot.Add(root.Name);

            // 落点层共享占用表（批内两个同名单元 → 「资料」「资料 (2)」）；子层随还原逐项累积
            var tableKey = landing ?? string.Empty;
            if (!namingTables.TryGetValue(tableKey, out var naming))
                naming = namingTables[tableKey] = await uow.Naming.CreateTableAsync(landing, ct);

            var members = OrderByHierarchy(allUnits, root, subtreeSet);
            if (members.Count >= 20)
                mainFolderIds ??= (await uow.Folders.FindManyAsync(
                        members.Select(m => new FolderId(m.TrashFolderId)).ToArray(), ct))
                    .Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);

            var folderRows = new List<Folder>(members.Count);
            foreach (var member in members)
            {
                if (restoredFolderIdSet.Contains(member.TrashFolderId)) continue;   // 防御：批内已还原（正常不可达）
                var isRoot = string.Equals(member.TrashFolderId, unitId, StringComparison.Ordinal);
                var name = isRoot
                    ? naming.Resolve(landing, member.Name)
                    : naming.Resolve(member.ParentTrashFolderId, member.Name);
                if (!string.Equals(name, member.Name, StringComparison.Ordinal))
                    renamed.Add(new TrashRestoreRename(member.Name, name));

                var folderExists = mainFolderIds is null
                    ? await uow.Folders.FindAsync(new FolderId(member.TrashFolderId), ct) != null
                    : !mainFolderIds.Add(member.TrashFolderId);   // Add=false = 已存在（顺带占位给后续成员）
                if (folderExists)
                    throw Conflict($"folder {member.TrashFolderId} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);

                folderRows.Add(new Folder
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
                });
                diff.AddRange(EntityDiff.Diff(member.TrashFolderId, null,
                    new FolderSnapshot(name, member.Description, isRoot ? landing : member.ParentTrashFolderId)));
                restoredFolderIds.Add(member.TrashFolderId);
                restoredFolderIdSet.Add(member.TrashFolderId);
            }
            if (folderRows.Count > 0)
                await uow.Trash.RestoreFoldersAsync(folderRows, ct);   // 集合化插入 + 清回收站行

            Phase("unit-folders");

            // 单元内链接：按 TrashFolderId 镜像回填（保留原 ID/全字段，不逐条评估 origin——origin 只决定单元根落点）
            var unitLinkSnapshots = await uow.Trash.ListLinksByUnitsAsync(
                subtreeIds.Select(id => new TrashFolderId(id)).ToArray(), ct);
            var unitRestoreMap = new List<(string LinkId, string? Landing)>(unitLinkSnapshots.Count);

            // 主表同 ID 冲突检查：一次 `id in (...)` 下推（原实现每条一次 FindAsync——400 条链接 = 400 次点查，
            // 大子树还原实测 1.1s 的主因）
            var conflictIds = new HashSet<string>(StringComparer.Ordinal);
            if (unitLinkSnapshots.Count > 0)
                conflictIds.UnionWith(await uow.Links.ListExistingIdsAsync(
                    unitLinkSnapshots.Select(s => s.LinkId).ToArray(), ct));
            foreach (var snapshot in unitLinkSnapshots)
            {
                if (restoredLinkIds.Contains(snapshot.LinkId)) continue;   // 防御（正常不可达）
                if (conflictIds.Contains(snapshot.LinkId))
                    throw Conflict($"bookmark {snapshot.LinkId} already exists in the main table (corrupt-data conflict), refusing to overwrite", ctx);
                // 单元内落点 = 刚还原的目录（其 ID 在回收站期间不可能被主表占用）→ 只判批内重复，免一次按 URL 的库查询
                var duplicate = !restoredKeys.Add((snapshot.TrashFolderId, snapshot.Url));
                var restored = new Link
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
                };
                diff.AddRange(EntityDiff.Diff(snapshot.LinkId, null, LinkSnapshot.Of(restored)));
                unitRestoreMap.Add((snapshot.LinkId, snapshot.TrashFolderId));
                restoredLinks.Add(new RestoredLinkInfo(
                    snapshot.LinkId, snapshot.Url, snapshot.Title, snapshot.TrashFolderId, false, duplicate));
                restoredLinkIds.Add(snapshot.LinkId);
            }
            if (unitRestoreMap.Count > 0)
                await uow.Trash.RestoreLinksAsync(unitRestoreMap, ct);
            Phase("unit-links");
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
        // 落点存在性用**一次**文件夹全表读取建集合，替代每条链接一次 Folders.FindAsync
        // （批量还原/重做几百条时这是线性点查的主要来源之一；文件夹数量有限，一次读更划算）。
        HashSet<string>? existingFolderIds = null;
        if (linkIds.Count >= 20)
        {
            // 只查这批链接的 origin 目录（`id in (...)`），不为一两条读全表、也不逐条点查
            var originIds = linkIds
                .Select(id => linkSnapshots[id].OriginListId)
                .Where(id => id != null)
                .Distinct(StringComparer.Ordinal)
                .Select(id => new FolderId(id!))
                .ToArray();
            if (originIds.Length > 0)
                existingFolderIds = (await uow.Folders.FindManyAsync(originIds, ct))
                    .Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);
        }

        // 重复 URL 的库内判定批量下推（只查这批链接的 URL；逐条 FindByUrlAsync 在 300 条时是 300 次点查）
        HashSet<(string? ListId, string Url)>? existingUrlKeys = null;
        if (linkIds.Count >= 20)
        {
            existingUrlKeys = new HashSet<(string?, string)>();
            var urls = linkIds.Select(id => linkSnapshots[id].Url)
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var chunk in urls.Chunk(500))
                foreach (var pair in await uow.Links.ListLandingUrlPairsAsync(chunk, ct))
                    existingUrlKeys.Add(pair);
        }

        var linkRestoreMap = new List<(string LinkId, string? Landing)>(linkIds.Count);
        foreach (var id in linkIds)
        {
            if (restoredLinkIds.Contains(id)) continue;   // 随同批单元一并还原 → 去重
            var snapshot = linkSnapshots[id];   // 预检快照复用（不再二次查库）

            var (landing, fellBack) = await ResolveLandingAsync(uow, to, snapshot.OriginListId,
                restoredFolderIdSet, existingFolderIds, ct);
            if (fellBack) fellBackToRoot.Add(snapshot.Title ?? snapshot.Url);

            // 批内重复用集合判（O(1)）；批外用已下推的 URL 集合，少量链接才走点查
            var duplicate = !restoredKeys.Add((landing, snapshot.Url));
            if (!duplicate)
                duplicate = existingUrlKeys?.Contains((landing, snapshot.Url))
                            ?? await HasExistingAtAsync(uow, landing, snapshot.Url, ct);
            var restoredLink = new Link
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
            };
            diff.AddRange(EntityDiff.Diff(snapshot.LinkId, null, LinkSnapshot.Of(restoredLink)));
            linkRestoreMap.Add((snapshot.LinkId, landing));
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
        if (linkRestoreMap.Count > 0)
            await uow.Trash.RestoreLinksAsync(linkRestoreMap, ct);
        Phase("links");

        // —— 3. 收尾：落点 Touch（同目录只一次）——
        // 不再回填 LinkCount：它是 [NotMapped] 的内存语义，还原出的 Folder 实体不会返回给任何调用方，
        // 而这段"CountByFolderAsync + 逐文件夹 FindAsync"在 100 层单元还原上实测白付 304ms。
        foreach (var landing in touched)
            await uow.Trees.TouchModifiedAsync(new FolderId(landing), ct);

        Phase("tail");
        return new TrashRestoreOutcome(restoredLinks, restoredUnits, restoredFolderIds, fellBackToRoot, renamed, undoSteps, diff);
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
        IUnitOfWork uow, string to, string? originParentId, HashSet<string> batchRestoredFolders,
        HashSet<string>? existingFolderIds, CancellationToken ct)
    {
        if (to == ToRoot) return (null, false);
        if (originParentId == null) return (null, false);   // 原在根（正常路径；最极端兜底同此语义）
        if (batchRestoredFolders.Contains(originParentId)) return (originParentId, false);   // 同批刚还原（批内不可查库）
        if (existingFolderIds is not null)
            return existingFolderIds.Contains(originParentId) ? (originParentId, false) : (null, true);
        if (await uow.Folders.FindAsync(new FolderId(originParentId), ct) != null) return (originParentId, false);
        return (null, true);   // 原位置已不存在 → 落根 + 如实回报
    }

    /// <summary>落点处是否已有同 URL 的其他链接（信息性计数，D7）。</summary>
    private static async Task<bool> HasExistingAtAsync(
        IUnitOfWork uow, string? landing, string url, CancellationToken ct)
        => (await uow.Links.FindByUrlAsync(url, ct))
            .Any(l => string.Equals(l.ListId, landing, StringComparison.Ordinal));

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
    IReadOnlyList<UndoInverseStep> UndoSteps,
    IReadOnlyList<FieldChange> Diff);

/// <summary>一条被还原的链接：落点 + 是否回落根 + 落点处是否已有同 URL（信息性）。</summary>
internal sealed record RestoredLinkInfo(
    string LinkId, string Url, string? Title, string? Landing, bool FellBackToRoot, bool Duplicate);

/// <summary>一个被还原的单元（选择项；含子单元的还原行数见 RestoredFolderIds）。</summary>
internal sealed record RestoredUnitInfo(string UnitId, string? Landing, bool FellBackToRoot);

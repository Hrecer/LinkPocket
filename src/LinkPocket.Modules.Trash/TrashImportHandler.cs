using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// trash.import（Mutation · FileIo）：从 .lptrash 导入回收站条目。
///
/// <para><b>只接受空回收站</b>（非空 → <c>LP.STATE.006</c> 拒绝并附指引）——"搬运而非合并"：
/// 更新流程里它把旧装的回收站原样搬回新装，两边各自合并会制造无法解释的重复。</para>
///
/// <para><b>保留实体原 ID</b>（单元 ID / 链接 ID 全保留；与 lpbackup 的临时 key 模型不同）。</para>
///
/// <para><b>origin 引用三级解析</b>（导入时对新活树解析，结果写回快照）：
/// ① 原 ID 在现活树存在 → 直接用；② 按 canonical <c>origin_path</c> 对新活树解析（单元解析其**父**路径 =
/// 自身路径去掉最后一段；链接解析其路径本身）；③ 都失败 → 记 NULL（= 还原到根）+ 计数如实暴露。
/// 实际流程里新装的文件夹 ID 全部重排（backup.import 另建实体），② 才是主通道。</para>
///
/// <para><b>校验先于写库</b>：包内一致性（重复/空 ID、悬空引用）、时间戳、目标非空、活树冲突，
/// 全部通过之后才写第一行。</para>
/// </summary>
internal sealed class TrashImportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "trash.import",
        Category: "trash",
        Description: $"Import recycle bin entries from a {TrashIO.FileExtension} (the target recycle bin must be empty;"
                     + " original ids are preserved; origin references are resolved id → canonical path → root with"
                     + " honest fallback counts)",
        Parameters: [ParamSpec.Req<string>("file_path", "Trash file path")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.LongRunning | CommandCaps.SupportsCancellation,
        Client: new("TrashImportAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var file = await TrashIO.ReadAsync(filePath, ct);
        if (!file.Valid)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, string.Join("; ", file.Errors), correlationId: ctx.CorrelationId));

        var units = file.Data.Folders ?? [];
        var links = file.Data.Links ?? [];

        // —— 包内一致性（外部输入：重复 / 空 ID、悬空引用一律报明确错误，绝不静默修正）——
        var unitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in units)
        {
            if (string.IsNullOrWhiteSpace(u.Id))
                throw Refuse(ctx, "the trash file contains an empty unit id and cannot be imported");
            if (!unitIds.Add(u.Id))
                throw Refuse(ctx, $"the trash file contains a duplicate unit id '{u.Id}' and cannot be imported");
        }
        var danglingUnits = units
            .Where(u => u.ParentId != null && !unitIds.Contains(u.ParentId))
            .Select(u => $"unit '{u.Name}' -> unknown parent '{u.ParentId}'")
            .ToList();
        var linkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            if (string.IsNullOrWhiteSpace(l.Id))
                throw Refuse(ctx, "the trash file contains an empty bookmark id and cannot be imported");
            if (!linkIds.Add(l.Id))
                throw Refuse(ctx, $"the trash file contains a duplicate bookmark id '{l.Id}' and cannot be imported");
            if (l.TrashFolderId != null && !unitIds.Contains(l.TrashFolderId))
                danglingUnits.Add($"bookmark '{l.Title ?? l.Url}' -> unknown unit '{l.TrashFolderId}'");
        }
        if (danglingUnits.Count > 0)
            throw Refuse(ctx, "the trash file's references are incomplete (if it was split or tampered with after export, use the file exported in one piece):"
                              + string.Join("; ", danglingUnits.Take(5)));

        // —— 时间戳：**有值但解析不了 = 拒绝整包**（字段缺失才允许显式取当下时间，绝不把损坏伪装成"刚刚删的"）——
        var badTimestamps = new List<string>();
        var unitTimes = new Dictionary<string, (DateTime? CreatedAt, DateTime? LastVisitedAt, DateTime DeletedAt)>(StringComparer.Ordinal);
        foreach (var u in units)
        {
            var createdAt = ParseTime(u.CreatedAt, $"unit '{u.Name}' created_at", badTimestamps);
            var lastVisited = ParseTime(u.LastVisitedAt, $"unit '{u.Name}' last_visited_at", badTimestamps);
            var deletedAt = ParseTime(u.DeletedAt, $"unit '{u.Name}' deleted_at", badTimestamps);
            unitTimes[u.Id] = (createdAt, lastVisited, deletedAt ?? DateTime.UtcNow);
        }
        var linkTimes = new Dictionary<string, (DateTime? LastVisitedAt, DateTime CreatedAt, DateTime UpdatedAt, DateTime DeletedAt)>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            var lastVisited = ParseTime(l.LastVisitedAt, $"bookmark '{l.Title ?? l.Url}' last_visited_at", badTimestamps);
            var createdAt = ParseTime(l.CreatedAt, $"bookmark '{l.Title ?? l.Url}' created_at", badTimestamps) ?? DateTime.UtcNow;
            var updatedAt = ParseTime(l.UpdatedAt, $"bookmark '{l.Title ?? l.Url}' updated_at", badTimestamps) ?? DateTime.UtcNow;
            var deletedAt = ParseTime(l.DeletedAt, $"bookmark '{l.Title ?? l.Url}' deleted_at", badTimestamps) ?? DateTime.UtcNow;
            linkTimes[l.Id] = (lastVisited, createdAt, updatedAt, deletedAt);
        }
        if (badTimestamps.Count > 0)
            throw Refuse(ctx, "the trash file contains invalid timestamps and cannot be imported:"
                              + string.Join("; ", badTimestamps.Take(5)));

        // —— 目标非空 → 拒绝（附指引）——
        var existingUnits = await uow.Trash.CountFoldersAsync(ct);
        var existingLinks = await uow.Trash.ListAllLinksAsync(ct);
        if (existingUnits > 0 || existingLinks.Count > 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.TargetNotEmpty,
                $"the target recycle bin is not empty ({existingUnits} units, {existingLinks.Count} bookmarks); "
                + "trash.import only imports into an empty recycle bin — restore or permanently delete its entries first",
                correlationId: ctx.CorrelationId));

        // —— origin 三级解析（对新活树）——
        var liveFolders = await uow.Folders.ListAllAsync(ct);
        var liveFolderIds = liveFolders.Select(f => f.FolderId).ToHashSet(StringComparer.Ordinal);

        // 单元 ID 撞活树 = 将来还原必冲突 → 提前拒绝（包来自同一库的"同 ID"再导入也会在此被挡住）
        var liveClash = units.Where(u => liveFolderIds.Contains(u.Id)).Select(u => u.Name).ToList();
        if (liveClash.Count > 0)
            throw Refuse(ctx, "these trash units collide with existing folders in the live tree and cannot be imported:"
                              + string.Join("; ", liveClash.Take(5)));

        // 活树按"父键 → 子名 → id"索引（同层唯一名由 NOCASE 硬约束保证不歧义）
        var childrenByName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var f in liveFolders)
        {
            var key = f.ParentId ?? string.Empty;
            if (!childrenByName.TryGetValue(key, out var level))
                level = childrenByName[key] = new Dictionary<string, string>(StringComparer.Ordinal);
            level[f.Name] = f.FolderId;
        }

        var byId = 0;
        var byPath = 0;
        var fallback = 0;
        var fellBackNames = new List<string>();
        var unitLanding = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var u in units)
        {
            var (landing, source) = ResolveOrigin(
                u.OriginParentFolderId, u.OriginPath, stripLastSegment: true, liveFolderIds, childrenByName);
            unitLanding[u.Id] = landing;
            switch (source)
            {
                case OriginSource.Id: byId++; break;
                case OriginSource.Path: byPath++; break;
                default: fallback++; fellBackNames.Add(u.Name); break;
            }
        }

        var linkLanding = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            var (landing, source) = ResolveOrigin(
                l.OriginFolderId, l.OriginPath, stripLastSegment: false, liveFolderIds, childrenByName);
            linkLanding[l.Id] = landing;
            switch (source)
            {
                case OriginSource.Id: byId++; break;
                case OriginSource.Path: byPath++; break;
                default: fallback++; fellBackNames.Add(l.Title ?? l.Url); break;
            }

            // 链接 ID 撞活链接 = 将来还原必冲突 → 提前拒绝
            if (await uow.Links.FindAsync(new LinkId(l.Id), ct) != null)
                throw Refuse(ctx, $"trash bookmark {l.Id} collides with an existing bookmark in the live tree and cannot be imported");
        }

        // 干跑零副作用：校验 + 解析全部完成（上面任何一步失败都已抛）→ 只如实预告，不写库
        if (ctx.DryRun)
            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    dry_run = true,
                    file_path = Path.GetFullPath(filePath),
                    format_version = TrashIO.FormatVersion,
                    total_units = units.Count,
                    total_links = links.Count,
                    origins = new { resolved_by_id = byId, resolved_by_path = byPath, root_fallback = fallback },
                    fell_back_entries = fellBackNames,
                }),
                ChangeSet.Of(
                    new EntityRef("file", Path.GetFullPath(filePath)),
                    LinkPocket.Contracts.DomainEventNames.TrashChanged,
                    $"Would import the recycle bin ({units.Count} units, {links.Count} bookmarks)"));

        // —— 写库（引擎单事务：全成或全滚）——
        foreach (var u in units)
        {
            ct.ThrowIfCancellationRequested();
            var (createdAt, lastVisitedAt, deletedAt) = unitTimes[u.Id];
            _ = await uow.Trash.AddFolderAsync(new TrashedFolder
            {
                TrashFolderId = u.Id,
                ParentTrashFolderId = u.ParentId,
                Name = u.Name,
                OriginFolderId = u.OriginFolderId,
                OriginParentFolderId = unitLanding[u.Id],
                OriginPath = u.OriginPath,
                Description = u.Description,
                SortOrder = u.SortOrder,
                CreatedAt = createdAt,
                LastVisitedAt = lastVisitedAt,
                VisitCount = u.VisitCount,
                DeletedAt = deletedAt,
            }, ct);
        }

        foreach (var l in links)
        {
            ct.ThrowIfCancellationRequested();
            var (lastVisitedAt, createdAt, updatedAt, deletedAt) = linkTimes[l.Id];
            _ = await uow.Trash.AddLinkAsync(new TrashedLink
            {
                LinkId = l.Id,
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = l.FaviconUrl,
                TrashFolderId = l.TrashFolderId,
                OriginListId = linkLanding[l.Id],
                OriginPath = l.OriginPath,
                LastVisitedAt = lastVisitedAt,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                DeletedAt = deletedAt,
            }, ct);
        }

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                file_path = Path.GetFullPath(filePath),
                format_version = TrashIO.FormatVersion,
                total_units = units.Count,
                total_links = links.Count,
                origins = new { resolved_by_id = byId, resolved_by_path = byPath, root_fallback = fallback },
                fell_back_entries = fellBackNames,
            }),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: [LinkPocket.Contracts.DomainEventNames.TrashChanged],
                HumanSummary: $"Recycle bin imported ({units.Count} units, {links.Count} bookmarks)"
                               + (fallback > 0 ? $"({fallback} origins fell back to root)" : "")));
    }

    private enum OriginSource { Id, Path, Fallback }

    /// <summary>
    /// 三级解析（见类型注释）：① 原 ID 在现活树存在（null = 原在根，直接算已解析）→ 直接用；
    /// ② canonical 路径对新活树解析；③ 都失败 → null + <see cref="OriginSource.Fallback"/>（计数如实暴露）。
    /// </summary>
    /// <param name="stripLastSegment">单元传 true（它的 origin_path 是**自身**路径，父路径要去掉最后一段）；
    /// 链接传 false（它的 origin_path 就是所在文件夹的路径）。</param>
    private static (string? Landing, OriginSource Source) ResolveOrigin(
        string? originId, string? originPath, bool stripLastSegment,
        HashSet<string> liveFolderIds, Dictionary<string, Dictionary<string, string>> childrenByName)
    {
        if (originId is null) return (null, OriginSource.Id);                       // 原在根：无需解析
        if (liveFolderIds.Contains(originId)) return (originId, OriginSource.Id);   // ① 直接用

        if (string.IsNullOrWhiteSpace(originPath)) return (null, OriginSource.Fallback);   // ② 无路径快照 → 判失败
        var segments = BookmarkPath.Split(originPath);
        var count = stripLastSegment ? segments.Count - 1 : segments.Count;
        if (TryWalk(segments, count, childrenByName, out var resolved))
            return (resolved, OriginSource.Path);                                   // ② 按路径解析（可能是根）
        return (null, OriginSource.Fallback);                                       // ③ 记 NULL（还原时落根）
    }

    /// <summary>沿活树走 canonical 路径的前 <paramref name="count"/> 段；<c>@unknown</c> / 段缺失 = 失败。</summary>
    private static bool TryWalk(
        IReadOnlyList<string> segments, int count,
        Dictionary<string, Dictionary<string, string>> childrenByName, out string? folderId)
    {
        folderId = null;
        if (count <= 0 || segments.Count == 0) return false;
        if (!BookmarkPath.MatchesRoot(BookmarkPath.RootToken, segments[0])) return false;

        var parentKey = string.Empty;
        for (var i = 1; i < count; i++)
        {
            if (!childrenByName.TryGetValue(parentKey, out var level) || !level.TryGetValue(segments[i], out var id))
                return false;
            folderId = id;
            parentKey = id;
        }
        return true;   // 走到根（count == 1）也是"解析成功，落根"
    }

    /// <summary>解析时间戳：null = 字段缺失（调用方给当时时间）；坏值进 <paramref name="bad"/> 列表（整包拒绝）。</summary>
    private static DateTime? ParseTime(string? text, string where, List<string> bad)
    {
        if (TrashIO.TryParseUtc(text, out var utc)) return utc;
        bad.Add(where);
        return null;
    }

    private static EngineException Refuse(ICommandContext ctx, string message)
        => new(EngineErrors.Of(EngineErrors.InvalidPath, message, correlationId: ctx.CorrelationId));
}

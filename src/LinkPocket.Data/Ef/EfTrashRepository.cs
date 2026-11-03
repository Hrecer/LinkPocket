using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>回收站两表仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfTrashRepository(LinkPocketDbContext db) : ITrashRepository
{
    // —— 按 ID 点查 = 读己之写（同事务合并 change tracker，ENGINE-API §5 / TRASH §4.4）——
    // 回收站的读全是 AsNoTracking（结果不进跟踪器），所以**先看 Local**才能表达
    // "本单元待删 → 不可见 / 本单元待增 → 可见"；Local 未命中再落 SQL（Local 只含本上下文
    // 显式 Add/Remove 过的实例，集合恒小）。列表查询不合并（仍只见提交边界一致快照）。

    /// <summary>按 ID 点查快照：本单元已 Remove → null；本单元已 Add（未落库）→ 可见。</summary>
    public async Task<TrashedLink?> FindLinkAsync(LinkId id, CancellationToken ct)
    {
        var pending = db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == id.Value);
        if (pending is not null)
            return db.Entry(pending).State == EntityState.Deleted ? null : pending;
        return await db.TrashedLinks.AsNoTracking().FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
    }

    public async Task<IReadOnlyList<TrashedLink>> ListStandaloneLinksAsync(CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .Where(l => l.TrashFolderId == null)
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(ct);

    public Task<int> CountStandaloneLinksAsync(CancellationToken ct)
        => db.TrashedLinks.AsNoTracking().CountAsync(l => l.TrashFolderId == null, ct);

    public async Task<IReadOnlyList<string>> ListFaviconUrlsAsync(CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .Where(l => l.FaviconUrl != null && l.FaviconUrl != "")
            .Select(l => l.FaviconUrl!)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TrashedLink>> ListLinksByUnitAsync(TrashFolderId unit, CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .Where(l => l.TrashFolderId == unit.Value)
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TrashedLink>> ListLinksByUnitsAsync(
        IReadOnlyList<TrashFolderId> units, CancellationToken ct)
    {
        if (units.Count == 0) return [];
        var wanted = units.Select(u => u.Value).ToHashSet(StringComparer.Ordinal);
        var rows = new List<TrashedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(500))
        {
            var names = string.Join(", ", chunk.Select((_, i) => "@u" + i));
            using var cmd = await RawSql.CreateAsync(db,
                SnapshotSelect + " WHERE trash_folder_id IN (" + names + ") ORDER BY deleted_at DESC", ct);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@u" + i, chunk[i]);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = ReadTrashedLink(reader);
                if (seen.Add(row.LinkId)) rows.Add(row);
            }
        }
        MergeLocalLinksByUnits(rows, seen, wanted);
        return rows;
    }

    public async Task<IReadOnlyList<TrashedLink>> ListLinksByIdsAsync(
        IReadOnlyList<LinkId> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
        var rows = new List<TrashedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(500))
        {
            // reader 逐列直读（批量撤销/重做的预检热路径）：不为几百行付 EF 实体物化/映射成本
            var names = string.Join(", ", chunk.Select((_, i) => "@id" + i));
            using var cmd = await RawSql.CreateAsync(db,
                SnapshotSelect + " WHERE id IN (" + names + ")", ct);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@id" + i, chunk[i]);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = ReadTrashedLink(reader);
                if (seen.Add(row.LinkId)) rows.Add(row);
            }
        }
        MergeLocalLinks(rows, seen, wanted);
        return rows;
    }

    private const string SnapshotSelect =
        "SELECT id, url, title, description, favicon_url, trash_folder_id, origin_folder_id, origin_path, "
        + "last_visited_at, visit_count, is_important, deleted_at, created_at, updated_at FROM trash_links";

    private static TrashedLink ReadTrashedLink(SqliteDataReader reader) => new()
    {
        LinkId = reader.GetString(0),
        Url = reader.GetString(1),
        Title = reader.IsDBNull(2) ? null : reader.GetString(2),
        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
        FaviconUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
        TrashFolderId = reader.IsDBNull(5) ? null : reader.GetString(5),
        OriginListId = reader.IsDBNull(6) ? null : reader.GetString(6),
        OriginPath = reader.IsDBNull(7) ? null : reader.GetString(7),
        LastVisitedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        VisitCount = reader.GetInt32(9),
        IsImportant = reader.GetBoolean(10),
        DeletedAt = reader.GetDateTime(11),
        CreatedAt = reader.GetDateTime(12),
        UpdatedAt = reader.GetDateTime(13),
    };

    /// <summary>按单元集合的读己之写合并（未提交新增可见、已删不可见）。</summary>
    private void MergeLocalLinksByUnits(List<TrashedLink> rows, HashSet<string> seen, HashSet<string> wantedUnits)
    {
        foreach (var local in db.TrashedLinks.Local)
        {
            if (local.TrashFolderId == null || !wantedUnits.Contains(local.TrashFolderId)) continue;
            var state = db.Entry(local).State;
            if (state == EntityState.Deleted)
            {
                rows.RemoveAll(r => r.LinkId == local.LinkId);
                seen.Remove(local.LinkId);
            }
            else if (state == EntityState.Added && seen.Add(local.LinkId))
            {
                rows.Add(local);
            }
        }
    }

    /// <summary>读己之写合并（列表批量读专用）：本单元新增可见、已删不可见。</summary>
    private void MergeLocalLinks(List<TrashedLink> rows, HashSet<string> seen, HashSet<string> wanted)
    {
        foreach (var local in db.TrashedLinks.Local)
        {
            if (!wanted.Contains(local.LinkId)) continue;
            var state = db.Entry(local).State;
            if (state == EntityState.Deleted)
            {
                rows.RemoveAll(r => r.LinkId == local.LinkId);
                seen.Remove(local.LinkId);
            }
            else if (state == EntityState.Added && seen.Add(local.LinkId))
            {
                rows.Add(local);
            }
        }
    }

    public async Task<IReadOnlyList<TrashedLink>> ListAllLinksAsync(CancellationToken ct)
        => await db.TrashedLinks.AsNoTracking()
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<TrashFolderId, int>> CountLinksByUnitAsync(CancellationToken ct)
        => (await db.TrashedLinks.AsNoTracking()
                .Where(l => l.TrashFolderId != null)
                .GroupBy(l => l.TrashFolderId!)
                .Select(g => new { UnitId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => new TrashFolderId(x.UnitId), x => x.Count);

    public Task<TrashedLink> AddLinkAsync(TrashedLink snapshot, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync。
        // 同批"先还原（Remove 本快照）、后又移入回收站（同 ID 重建）"：跟踪器里同 key 待删实例 =
        // 行仍在库 → 新实例接管（待删取消 + 按 UPDATE 提交；直接 Add 会撞 EF 身份冲突）。
        // ⚠️ 接管扫描**只在本上下文走过 Remove 后才做**（_removedLinks）：每加必扫是 O(n²)——子树级联
        //（一整棵含万条链接的树进回收站）实测因此要跑几十秒，万行时 90% 时间花在这条扫描上（见 WARNINGS）。
        if (_removedLinks)
        {
            var tracked = db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == snapshot.LinkId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.TrashedLinks.Attach(snapshot);
                db.Entry(snapshot).State = EntityState.Modified;
                return Task.FromResult(snapshot);
            }
        }
        db.TrashedLinks.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    /// <summary>本上下文是否走过 SQL 命中的 RemoveLink（语义同 <c>EfLinkRepository._removed</c>）。</summary>
    private bool _removedLinks;
    /// <summary>本上下文是否走过 SQL 命中的 RemoveFolder（语义同上）。</summary>
    private bool _removedFolders;

    public async Task RemoveLinkAsync(LinkId id, CancellationToken ct)
    {
        // SQL 优先（身份解析复用跟踪实例；已删再 Remove = no-op）；SQL 未命中 →
        // 本单元未提交的新增快照（Remove 在 Added 实体上 = 取消暂存：同批"入站又出站"净效果为零）。
        var snapshot = await db.TrashedLinks.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct)
                       ?? db.TrashedLinks.Local.FirstOrDefault(l => l.LinkId == id.Value);
        if (snapshot != null)
        {
            db.TrashedLinks.Remove(snapshot);
            _removedLinks = true;   // 出现同 key 待删实例 → 后续 AddLinkAsync 才需要接管扫描
        }
    }

    /// <summary>按 ID 点查单元：本单元已 Remove → null；本单元已 Add（未落库）→ 可见。</summary>
    public async Task<TrashedFolder?> FindFolderAsync(TrashFolderId id, CancellationToken ct)
    {
        var pending = db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == id.Value);
        if (pending is not null)
            return db.Entry(pending).State == EntityState.Deleted ? null : pending;
        return await db.TrashedFolders.AsNoTracking().FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct);
    }

    public async Task<IReadOnlyList<TrashedFolder>> ListFoldersAsync(CancellationToken ct)
    {
        // reader 直读（同 ListAllLinks 口径）：单元还原的入口查询，不为恢复整树付 EF 物化
        using var cmd = await RawSql.CreateAsync(db,
            "SELECT id, parent_id, name, origin_folder_id, origin_parent_folder_id, origin_path, description, "
            + "sort_order, created_at, last_visited_at, visit_count, deleted_at "
            + "FROM trash_folders ORDER BY deleted_at DESC", ct);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<TrashedFolder>();
        while (await reader.ReadAsync(ct))
            rows.Add(new TrashedFolder
            {
                TrashFolderId = reader.GetString(0),
                ParentTrashFolderId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Name = reader.GetString(2),
                OriginFolderId = reader.IsDBNull(3) ? null : reader.GetString(3),
                OriginParentFolderId = reader.IsDBNull(4) ? null : reader.GetString(4),
                OriginPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                SortOrder = reader.GetInt32(7),
                CreatedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                LastVisitedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                VisitCount = reader.GetInt32(10),
                DeletedAt = reader.GetDateTime(11),
            });
        return rows;
    }

    public Task<int> CountFoldersAsync(CancellationToken ct)
        => db.TrashedFolders.AsNoTracking().CountAsync(ct);

    public Task<TrashedFolder> AddFolderAsync(TrashedFolder unit, CancellationToken ct)
    {
        // 只登记不保存：同 key 待删实例由新实例接管（语义同 AddLinkAsync；扫描同样只在走过 Remove 后开启）。
        if (_removedFolders)
        {
            var tracked = db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == unit.TrashFolderId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.TrashedFolders.Attach(unit);
                db.Entry(unit).State = EntityState.Modified;
                return Task.FromResult(unit);
            }
        }
        db.TrashedFolders.Add(unit);
        return Task.FromResult(unit);
    }

    public async Task RemoveFolderAsync(TrashFolderId id, CancellationToken ct)
    {
        // SQL 优先 + 未提交新增回退（语义同 RemoveLinkAsync）。
        var unit = await db.TrashedFolders.FirstOrDefaultAsync(f => f.TrashFolderId == id.Value, ct)
                   ?? db.TrashedFolders.Local.FirstOrDefault(f => f.TrashFolderId == id.Value);
        if (unit != null)
        {
            db.TrashedFolders.Remove(unit);
            _removedFolders = true;   // 出现同 key 待删实例 → 后续 AddFolderAsync 才需要接管扫描
        }
    }

    public async Task<int> TrashLinksOfFolderSelectionAsync(string? folderId, string originPath,
        IReadOnlyList<string> linkIds, DateTime deletedAt, CancellationToken ct)
    {
        if (linkIds.Count == 0) return 0;
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        var deleted = deletedAt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        var moved = 0;
        foreach (var chunk in linkIds.Chunk(500))
        {
            var names = string.Join(", ", chunk.Select((_, i) => "@id" + i));
            var parameters = new List<object>
            {
                new SqliteParameter("@folder_id", (object?)folderId ?? DBNull.Value),
                new SqliteParameter("@origin_path", originPath),
                new SqliteParameter("@deleted_at", deleted),
                new SqliteParameter("@updated_at", now),
            };
            for (var i = 0; i < chunk.Length; i++)
                parameters.Add(new SqliteParameter("@id" + i, chunk[i]));

            // 占位符表由代码生成（@id0..@idN），值全部走 SqliteParameter——不存在用户输入拼接
            var insertSql =
                """
                INSERT INTO trash_links (id, url, title, description, favicon_url, trash_folder_id, origin_folder_id,
                                         origin_path, last_visited_at, visit_count, is_important, deleted_at, created_at, updated_at)
                SELECT id, url, title, description, favicon_url, NULL, folder_id,
                       @origin_path, last_visited_at, visit_count, is_important, @deleted_at, created_at, @updated_at
                FROM links
                WHERE folder_id IS @folder_id AND id IN (
                """ + names + ")";
            moved += await db.Database.ExecuteSqlRawAsync(insertSql, parameters, ct);

            var deleteSql = "DELETE FROM links WHERE folder_id IS @folder_id AND id IN (" + names + ")";
            await db.Database.ExecuteSqlRawAsync(deleteSql, parameters, ct);
        }
        return moved;
    }

    public async Task<int> RestoreLinksAsync(
        IReadOnlyList<(string LinkId, string? Landing)> links, CancellationToken ct)
    {
        if (links.Count == 0) return 0;
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        var inserted = 0;
        foreach (var chunk in links.Chunk(500))
        {
            var payload = JsonSerializer.Serialize(chunk.Select(l => new { id = l.LinkId, landing = l.Landing }));
            inserted += await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO links (id, folder_id, url, title, description, favicon_url,
                                   is_important, visit_count, last_visited_at, created_at, updated_at)
                SELECT t.id, json_extract(j.value, '$.landing'), t.url, t.title, t.description, t.favicon_url,
                       t.is_important, t.visit_count, t.last_visited_at, t.created_at, @updated_at
                FROM trash_links t
                JOIN json_each(@map) j ON json_extract(j.value, '$.id') = t.id
                """,
                new object[] { new SqliteParameter("@map", payload), new SqliteParameter("@updated_at", now) },
                ct);

            // 回收站行同样按 JSON 映射批量删（避免 EF ExecuteDelete 的每次查询编译/参数化开销）
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM trash_links WHERE id IN (SELECT json_extract(j.value, '$.id') FROM json_each(@map) j)",
                new object[] { new SqliteParameter("@map", payload) }, ct);
        }
        return inserted;
    }

    public async Task<int> RestoreFoldersAsync(IReadOnlyList<Folder> folders, CancellationToken ct)
    {
        if (folders.Count == 0) return 0;
        // 自引用外键（父行先于子行）在事务内推迟到提交点校验——json_each 的顺序不必承担拓扑保证
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);
        var inserted = 0;
        foreach (var chunk in folders.Chunk(200))
        {
            var payload = JsonSerializer.Serialize(chunk.Select(f => new
            {
                id = f.FolderId,
                parent_id = f.ParentId,
                name = f.Name,
                description = f.Description,
                sort_order = f.SortOrder,
                created_at = f.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                updated_at = f.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                last_visited_at = f.LastVisitedAt?.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                visit_count = f.VisitCount,
            }));
            inserted += await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO folders (id, parent_id, name, description, sort_order, created_at, updated_at,
                                     last_visited_at, visit_count)
                SELECT json_extract(j.value, '$.id'), json_extract(j.value, '$.parent_id'),
                       json_extract(j.value, '$.name'), json_extract(j.value, '$.description'),
                       json_extract(j.value, '$.sort_order'), json_extract(j.value, '$.created_at'),
                       json_extract(j.value, '$.updated_at'), json_extract(j.value, '$.last_visited_at'),
                       json_extract(j.value, '$.visit_count')
                FROM json_each(@json) j
                """,
                new object[] { new SqliteParameter("@json", payload) },
                ct);

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM trash_folders WHERE id IN (SELECT json_extract(j.value, '$.id') FROM json_each(@json) j)",
                new object[] { new SqliteParameter("@json", payload) }, ct);
        }
        return inserted;
    }

    public async Task<int> TrashLinksOfFoldersAsync(IReadOnlyList<(string FolderId, string OriginPath)> folders,
        DateTime deletedAt, CancellationToken ct)
    {
        // set-based 整目录搬运（删除文件夹的整树级联专用）：一个目录 = 一条 INSERT..SELECT + 一条 DELETE。
        // 快照列与 TrashedLink 的 EF 写入口径**逐列一致**（含 UpdatedAt = 本次删除时刻、OriginPath = 所在目录路径）；
        // 调用方必须已开事务（本方法不做事务管理——见 IUnitOfWork.HasActiveTransaction 的约定）。
        var moved = 0;
        var now = DateTime.UtcNow;
        foreach (var (folderId, originPath) in folders)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO trash_links (id, url, title, description, favicon_url, trash_folder_id, origin_folder_id,
                                         origin_path, last_visited_at, visit_count, is_important, deleted_at, created_at, updated_at)
                SELECT id, url, title, description, favicon_url, folder_id, folder_id,
                       @origin_path, last_visited_at, visit_count, is_important, @deleted_at, created_at, @updated_at
                FROM links WHERE folder_id = @folder_id
                """,
                [
                    new SqliteParameter("@origin_path", (object?)originPath ?? DBNull.Value),
                    new SqliteParameter("@deleted_at", deletedAt),
                    new SqliteParameter("@updated_at", now),
                    new SqliteParameter("@folder_id", folderId),
                ]);
            moved += await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM links WHERE folder_id = @folder_id",
                [new SqliteParameter("@folder_id", folderId)]);
        }
        return moved;
    }
}

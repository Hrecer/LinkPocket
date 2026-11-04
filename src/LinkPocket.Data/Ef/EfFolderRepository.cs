using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>文件夹仓储 EF 实现（internal——仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfFolderRepository(LinkPocketDbContext db) : IFolderRepository
{
    /// <summary>
    /// 本上下文是否走过 SQL 命中的 Remove（语义同 <c>EfLinkRepository._removed</c>，见 WARNINGS 135）：
    /// 只有走过删除才可能存在同 key 待删实例，Add 的接管扫描才需要打开；正常新增路径 O(1)。
    /// </summary>
    private bool _removed;

    /// <summary>
    /// 按 ID 点查 = **读己之写**（同事务合并 change tracker，ENGINE-API §5 / TRASH §4.4）：
    /// ① SQL 命中且跟踪态为 Deleted → null（本单元已删不可见）；② SQL 未命中 → 只认跟踪器里
    /// **Added**（本单元未提交的新增，SQL 看不到）；其余状态（行已越轨消失的幽灵）如实 null。
    /// SQL 优先 = 命中走 PK 索引 + 身份解析 O(1)，Local 扫描只发生在 SQL 未命中时；
    /// 列表/范围查询与读池**不合并**（仍只见提交边界一致快照）——批内集合级判定由各流水线自记账。
    /// </summary>
    public async Task<Folder?> FindAsync(FolderId id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);
        if (folder is not null)
            return db.Entry(folder).State == EntityState.Deleted ? null : folder;   // 身份解析复用跟踪实例 → 状态判定 O(1)
        var pending = db.Folders.Local.FirstOrDefault(f => f.FolderId == id.Value);  // SQL 未命中 → 只可能是未提交的新增
        return pending is not null && db.Entry(pending).State == EntityState.Added ? pending : null;
    }

    public async Task<IReadOnlyList<Folder>> ListAllAsync(CancellationToken ct)
    {
        // 直读（B 路线，2026-10-15）：刷新固定成本的主力查（folders.overview 每次都取全量文件夹）。
        // reader 逐列映射代替 EF 实体物化；返回**分离实例**——与原 AsNoTracking 语义一致
        //（调用方要写必须显式 UpdateAsync / RemoveRangeAsync 登记，批内跟踪态接管逻辑照旧）。
        using var cmd = await RawSql.CreateAsync(db,
            "SELECT id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at FROM folders", ct);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Folder>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Folder
            {
                FolderId = reader.GetString(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                SortOrder = reader.GetInt32(4),
                LastVisitedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                VisitCount = reader.GetInt32(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8),
            });
        }
        return rows;
    }

    /// <summary>
    /// 目录名搜索：名称命中 + （可选）全部后代，全在 SQL 里算。
    /// </summary>
    /// <remarks>
    /// ① 名称命中 = `name LIKE @q ESCAPE '\'`（与链接搜索同口径）；
    /// ② 后代 = 递归 CTE 从命中节点向下走，`UNION`（不是 ALL）去重——坏数据成环时靠行重复自然终止；
    /// ③ 只回命中行：十万目录时全量物化 ≈410ms，这里 2 000 个命中 ≈20ms、5 万后代 ≈100–200ms。
    /// </remarks>
    public async Task<FolderNameSearch> SearchByNameAsync(
        string query, bool includeDescendants, string? sortBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return FolderNameSearch.Empty;

        // 只多带**排序真正用到的那一列**：完整展示字段（描述/父目录/三个日期）等取页时按 ID 补水。
        // 5 万后代 × 全列实测 ≈190ms，id+name ≈140ms；多带的列随排序键变化（见 WARNINGS 247）。
        var sortExpr = sortBy switch
        {
            "created_at" => "f.created_at",
            "updated_at" => "f.updated_at",
            "last_visited_at" => "f.last_visited_at",
            "visit_count" => "f.visit_count",
            _ => null,
        };
        var columns = sortExpr is null ? "f.id, f.name" : "f.id, f.name, " + sortExpr;
        var sql = includeDescendants
            ? "WITH RECURSIVE hit(id, is_self) AS (" +
              "SELECT id, 1 FROM folders WHERE name LIKE @q ESCAPE '\\' " +
              "UNION SELECT f.id, 0 FROM folders f JOIN hit h ON f.parent_id = h.id) " +
              "SELECT k.is_self, " + columns + " FROM hit k JOIN folders f ON f.id = k.id"
            : "SELECT 1, " + columns + " FROM folders f WHERE f.name LIKE @q ESCAPE '\\'";

        var matches = new List<FolderSearchHit>();
        var descendants = new List<FolderSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using var cmd = await RawSql.CreateAsync(db, sql, ct);
        cmd.Parameters.AddWithValue("@q", $"%{EscapeLike(query)}%");
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var isSelf = reader.GetInt64(0) == 1;
            string? sortText = null;
            var sortNumber = 0;
            if (sortExpr is not null)
            {
                if (sortExpr == "f.visit_count") sortNumber = reader.GetInt32(3);
                else sortText = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
            var hit = new FolderSearchHit(reader.GetString(1), reader.GetString(2), sortText, sortNumber);

            // 一个目录既可能是名称命中、又可能是某个命中目录的后代：只按"名称命中"收一次。
            if (isSelf)
            {
                if (seen.Add(hit.Id)) matches.Add(hit);
            }
            else if (!seen.Contains(hit.Id))
            {
                descendants.Add(hit);
            }
        }
        return new FolderNameSearch(matches, descendants);
    }

    /// <summary>LIKE 通配符转义（与 <c>EfLinkRepository</c> 同口径）。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<Folder>> FindManyAsync(IReadOnlyList<FolderId> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var result = new List<Folder>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        // 分块（500/批）：SQLite 参数上限之内，库再大也不会把 SQL 撑爆。
        // reader 直读（与 ListAllAsync 同口径）：避免批量预检每次付 EF 查询编译 + 实体物化。
        foreach (var chunk in ids.Chunk(500))
        {
            var names = string.Join(", ", chunk.Select((_, i) => "@id" + i));
            using var cmd = await RawSql.CreateAsync(db,
                "SELECT id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at "
                + "FROM folders WHERE id IN (" + names + ")", ct);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@id" + i, chunk[i].Value);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var folder = new Folder
                {
                    FolderId = reader.GetString(0),
                    ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Name = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SortOrder = reader.GetInt32(4),
                    LastVisitedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    VisitCount = reader.GetInt32(6),
                    CreatedAt = reader.GetDateTime(7),
                    UpdatedAt = reader.GetDateTime(8),
                };
                if (seenIds.Add(folder.FolderId)) result.Add(folder);
            }
        }

        // 读己之写（与 FindAsync 对齐）：本单元未提交的新增要算存在、已删的不算
        foreach (var tracked in db.Folders.Local)
        {
            var state = db.Entry(tracked).State;
            if (state == EntityState.Added && seenIds.Add(tracked.FolderId)) result.Add(tracked);
            else if (state == EntityState.Deleted) result.RemoveAll(f => f.FolderId == tracked.FolderId);
        }
        return result;
    }

    public Task<int> CountAsync(CancellationToken ct)
        => db.Folders.AsNoTracking().CountAsync(ct);

    public async Task<IReadOnlyList<Folder>> ChildrenOfAsync(FolderId? parent, CancellationToken ct)
    {
        // reader 直读（同 ListAllAsync）：命名服务在还原/导入热路径上反复取同层兄弟
        using var cmd = await RawSql.CreateAsync(db,
            "SELECT id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at "
            + "FROM folders WHERE parent_id IS @p", ct);
        cmd.Parameters.AddWithValue("@p", parent is { } p ? p.Value : (object)DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Folder>();
        while (await reader.ReadAsync(ct))
            rows.Add(new Folder
            {
                FolderId = reader.GetString(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                SortOrder = reader.GetInt32(4),
                LastVisitedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                VisitCount = reader.GetInt32(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8),
            });
        return rows;
    }

    public Task<Folder> AddAsync(Folder folder, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）。
        // 同批"先软删、后按原 ID 还原/重建"：跟踪器里同 key 的待删实例 = 行仍在库 → 交给新实例接管
        //（待删取消 + 新值按 UPDATE 提交；直接 Add 会撞 EF 身份冲突"already being tracked"）。
        // 接管扫描只在本上下文走过 Remove 后才做（_removed）：每加必扫 = O(n²)（见 WARNINGS 135）。
        if (_removed)
        {
            var tracked = db.Folders.Local.FirstOrDefault(f => f.FolderId == folder.FolderId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.Folders.Attach(folder);
                db.Entry(folder).State = EntityState.Modified;
                return Task.FromResult(folder);
            }
        }
        db.Folders.Add(folder);
        return Task.FromResult(folder);
    }

    public Task UpdateAsync(Folder folder, CancellationToken ct)
    {
        db.Folders.Update(folder);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(FolderId id, CancellationToken ct)
    {
        // SQL 优先（身份解析复用跟踪实例；已删实例再 Remove = no-op）；
        // SQL 未命中且跟踪器里是 Added → Remove = 取消暂存（同批"建了又删"净效果为零）；
        // 其余状态不动（行已越轨消失时不制造 DELETE 0 行的并发异常）。
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.FolderId == id.Value, ct);
        if (folder is null)
        {
            var pending = db.Folders.Local.FirstOrDefault(f => f.FolderId == id.Value);
            if (pending is not null && db.Entry(pending).State == EntityState.Added) db.Folders.Remove(pending);
            return;
        }
        db.Folders.Remove(folder);
        _removed = true;   // 出现同 key 待删实例 → 后续 Add 才需要接管扫描（AddAsync）
    }

    public Task RemoveRangeAsync(IReadOnlyList<Folder> folders, CancellationToken ct)
    {
        // 已读出的实体按实体登记删除（Entry() 走身份映射，O(1)/行；不发 SELECT）——
        // 与 RemoveAsync 的关系：同语义，只是跳过"按 ID 重新点查"这一步（调用方手里就是刚读出的那一批）。
        // ⚠️ 批/宏的嵌套步骤可能已把**同一文件夹**读成跟踪态（先移动再删除）：那种情况下必须拿
        // **已被跟踪的那个实例**登记删除——附加手里这份分离实例会撞 EF 身份映射（实测 IdentityConflict）。
        // 跟踪表**惰性**建（全部分离 = 常见热路径，一次都不用枚举；建表本身也只付一次 O(n)）。
        Dictionary<string, Folder>? tracked = null;
        foreach (var folder in folders)
        {
            if (db.Entry(folder).State != EntityState.Detached)
            {
                db.Entry(folder).State = EntityState.Deleted;   // 本实例已在跟踪 → 直接改状态
                continue;
            }
            tracked ??= db.ChangeTracker.Entries<Folder>()
                .ToDictionary(e => e.Entity.FolderId, e => e.Entity, StringComparer.Ordinal);
            if (tracked.TryGetValue(folder.FolderId, out var existing))
                db.Entry(existing).State = EntityState.Deleted;  // 同 key 已跟踪（含已删除 → 空操作）
            else
                db.Entry(folder).State = EntityState.Deleted;
        }
        if (folders.Count > 0) _removed = true;   // 接管扫描的开启条件与 RemoveAsync 一致
        return Task.CompletedTask;
    }
}

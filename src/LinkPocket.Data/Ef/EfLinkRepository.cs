using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>链接仓储 EF 实现（internal——黑盒封装，仅经 IUnitOfWork 暴露）。</summary>
internal sealed class EfLinkRepository(LinkPocketDbContext db) : ILinkRepository
{
    /// <summary>
    /// 本上下文是否走过 SQL 命中的 Remove（走过才可能存在同 key 的**待删**实例，Add 才需要接管扫描）。
    /// 无条件扫描 = 每次 Add 都 O(本上下文实体数) → 10k 导入 O(n²)（实测 34s 顶穿 5s 门槛，见 WARNINGS 135）；
    /// 正常新增热路径必须保持 O(1)。恢复/还原本就先走 Remove → 扫描照做，语义不变。
    /// </summary>
    private bool _removed;

    /// <summary>
    /// 按 ID 点查 = **读己之写**（语义同 <c>EfFolderRepository.FindAsync</c>，ENGINE-API §5）：
    /// SQL 命中且跟踪态 Deleted → null；SQL 未命中只认跟踪器 Added（未提交的新增）。
    /// </summary>
    public async Task<Link?> FindAsync(LinkId id, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (link is not null)
            return db.Entry(link).State == EntityState.Deleted ? null : link;
        var pending = db.Links.Local.FirstOrDefault(l => l.LinkId == id.Value);
        return pending is not null && db.Entry(pending).State == EntityState.Added ? pending : null;
    }

    /// <summary>过滤 + 排序（含 ID 兜底）+ 分页 的统一构建（ListAsync / ListDtosAsync 共用，规则只此一份）。</summary>
    private IQueryable<Link> BuildPageQuery(LinkQuerySpec spec)
    {
        IQueryable<Link> q = db.Links.AsNoTracking();
        q = ApplyFilter(q, spec.Filter);

        // 排序经 ISortEngine 白名单下推；缺省 = 名称升序 + ID 兜底
        q = EfSortEngine.Instance.Apply(q, spec.Sort, EfSortEngine.LinkFields);
        // ID 兜底恒追加、**方向随最后一条排序**：任意非唯一排序字段下 Skip/Take 分页仍稳定（翻页不重不漏），
        // 同时让 `(folder_id, 列, id)` 复合索引**倒序扫**也整段命中排序——
        // 混合方向（列 DESC, id ASC）没有索引可依，会退化成"取回整个目录再排序"（100K 实测 43ms/页）。
        var lastDir = spec.Sort.Count > 0 ? spec.Sort[^1].Dir : SortDir.Asc;
        q = lastDir == SortDir.Desc
            ? ((IOrderedQueryable<Link>)q).ThenByDescending(l => l.LinkId)
            : ((IOrderedQueryable<Link>)q).ThenBy(l => l.LinkId);

        if (spec.Page.Size > 0)
            q = q.Skip(spec.Page.Skip).Take(spec.Page.Size);
        return q;
    }

    /// <summary>分页**直出 DTO**：目录页每帧 2000 行的取数形态。
    /// <b>直读路径</b>（2026-10-15 A 路线）：单一归属谓词 + 白名单排序（目录页的全部现实形态）
    /// 走原生 SQL reader —— 与 EF 投影逐字段同口径，但省掉查询管线与物化/投影两段开销
    ///（100K 实测 2000 行从 20–30ms 压到 ~1–2ms）。
    /// 其余形态（搜索/多条件过滤）语义面太大，仍走 EF（<see cref="BuildPageQuery"/>，过滤规则只此一份）。</summary>
    public async Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDtosAsync(LinkQuerySpec spec, CancellationToken ct)
    {
        if (TryBuildRawSearchSql(spec, out var searchSql, out var searchParameters))
            return await ReadDtosRawAsync(searchSql, searchParameters, ct);
        if (TryBuildRawSmartListSql(spec, out var smartSql, out var smartParameters))
            return await ReadDtosRawAsync(smartSql, smartParameters, ct);
        if (TryBuildRawPageSql(spec, out var sql, out var parameters))
            return await ReadDtosRawAsync(sql, parameters, ct);
        return await ListDtosEfAsync(spec, ct);
    }

    /// <summary>
    /// 智能列表过滤（时间窗 / 访问过）的**唯一谓词构造**：计数与取页共用，字段集合严格受限
    /// （恰好一个时间/访问条件，其余过滤全空）——多一个条件即返回 false，回 EF 的通用语义。
    /// </summary>
    private static bool TryBuildSmartListWhereSql(
        LinkFilter f, out string where, out (string Name, object? Value)[] parameters)
    {
        where = string.Empty;
        parameters = [];

        var conditions = 0;
        string? clause = null;
        (string Name, object? Value)? bound = null;
        if (f.CreatedFrom is { } created)
        {
            conditions++;
            clause = "created_at >= @created_from";
            bound = ("@created_from", created);
        }
        if (f.UpdatedFrom is { } updated)
        {
            conditions++;
            clause = "updated_at >= @updated_from";
            bound = ("@updated_from", updated);
        }
        if (f.LastVisitedFrom is { } visited)
        {
            conditions++;
            clause = "last_visited_at >= @last_visited_from";
            bound = ("@last_visited_from", visited);
        }
        if (f.VisitCountMin is { } min)
        {
            conditions++;
            clause = "visit_count >= @visit_min";
            bound = ("@visit_min", min);
        }
        if (conditions != 1 || bound is null) return false;

        // 归属谓词也不能有：IsAreaFilter 只判"除归属外"的条件，FolderId/Unfiled 由调用方判——
        // 这里若漏判，带 FolderId + CreatedFrom 的查询会静默丢掉目录条件。
        if (f.FolderId is not null || f.Unfiled is not null) return false;

        // 其余条件必须全空：智能列表的四种形态都不带别的过滤（多条件查询走 EF 的正确性路径）。
        if (!IsAreaFilter(f with
            {
                CreatedFrom = null,
                UpdatedFrom = null,
                LastVisitedFrom = null,
                VisitCountMin = null,
            }))
            return false;

        where = clause!;
        // 访问次数索引是部分索引（目录恒真谓词）：这里必须带上同一谓词，计划器才允许使用；
        // 谓词对每一行都成立，不改变结果。搜索页的同列排序不带它，因此不会误选该索引。
        if (f.VisitCountMin is not null)
            where += " AND (folder_id IS NOT NULL OR folder_id IS NULL)";
        parameters = [bound.Value];
        return true;
    }

    /// <summary>智能列表过滤列对应的紧凑索引（与 <see cref="SchemaMigrator.HotIndexDdl"/> 同名同义）。</summary>
    private static string? SmartListFilterIndex(LinkFilter f)
    {
        if (f.CreatedFrom is not null) return "idx_links_created";
        if (f.UpdatedFrom is not null) return "idx_links_updated";
        if (f.LastVisitedFrom is not null) return "idx_links_last_visited";
        if (f.VisitCountMin is not null) return "idx_links_visits";
        return null;
    }

    /// <summary>
    /// 智能列表直读：谓词 = 时间窗/访问次数，排序 = 白名单 1–2 列（与 EF 的 ID 兜底、NULL 恒排最后同口径）。
    /// 十万行全量取数走原生 reader：省掉 EF 物化/投影管线，首屏的「计数 + 首页」也不必等整个查询编译。
    /// </summary>
    private static bool TryBuildRawSmartListSql(
        LinkQuerySpec spec, out string sql, out (string Name, object? Value)[] parameters)
    {
        sql = string.Empty;
        parameters = [];
        if (!TryBuildSmartListWhereSql(spec.Filter, out var where, out var whereParameters)) return false;
        if (spec.Sort.Count is < 1 or > 2) return false;

        var order = new List<string>(3);
        foreach (var clause in spec.Sort)
        {
            var expr = clause.Field switch
            {
                "title" => "title COLLATE NOCASE",
                "url" => "url COLLATE NOCASE",
                "created_at" => "created_at",
                "updated_at" => "updated_at",
                "last_visited_at" => "last_visited_at",
                "visit_count" => "visit_count",
                "is_important" => "is_important",
                _ => null,
            };
            if (expr is null) return false;

            var dir = clause.Dir == SortDir.Desc ? "DESC" : "ASC";
            if (clause.Field == "last_visited_at")
                order.Add($"(last_visited_at IS NULL) ASC, last_visited_at {dir}");
            else
                order.Add($"{expr} {dir}");
        }
        // ID 兜底方向随最后一条排序（与 BuildPageQuery 一致）：倒序扫也整段命中复合索引。
        order.Add($"id {(spec.Sort[^1].Dir == SortDir.Desc ? "DESC" : "ASC")}");

        var sb = new StringBuilder(
            "SELECT id, url, title, description, favicon_url, folder_id, last_visited_at, visit_count, is_important, created_at, updated_at FROM links");
        // 选择性过滤：强制走过滤列索引收候选，再按标题排（否则 SQLite 会沿标题索引扫十万行回表）。
        // 其余标题序（最近添加/编辑等）固定走老的搜索覆盖索引：新索引会改变计划器对“取全量”
        // 的估算，曾把 1.0s 的标题序全量拖到 2.0s（见 WARNINGS 251）。
        if ((spec.PreferFilterIndex || spec.Page.Size == 0)
            && SmartListFilterIndex(spec.Filter) is { } index)
            sb.Append(" INDEXED BY ").Append(index);
        else if (spec.Page.Size > 0 && spec.Sort.Count == 1 && spec.Sort[0].Field == "title")
            sb.Append(" INDEXED BY idx_links_search");
        sb.Append(" WHERE ").Append(where);
        sb.Append(" ORDER BY ").Append(string.Join(", ", order));

        var list = new List<(string Name, object? Value)>(whereParameters.Length + 2);
        list.AddRange(whereParameters);
        if (spec.Page.Size > 0)
        {
            sb.Append(" LIMIT @take OFFSET @skip");
            list.Add(("@take", spec.Page.Size));
            list.Add(("@skip", spec.Page.Skip));
        }
        sql = sb.ToString();
        parameters = [.. list];
        return true;
    }

    /// <summary>EF 投影路径（复杂形态兜底；正确性基准 = <c>ListAsync().ToDto()</c> 实体路径，测试逐字段对照）。</summary>
    private async Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDtosEfAsync(LinkQuerySpec spec, CancellationToken ct)
        => await BuildPageQuery(spec)
            .Select(l => new LinkPocket.Contracts.LinkDto
            {
                LinkId = l.LinkId,
                Url = l.Url,
                Title = l.Title ?? string.Empty,
                Description = l.Description ?? string.Empty,
                FaviconUrl = l.FaviconUrl ?? string.Empty,
                ListId = l.ListId,
                LastVisitedAt = l.LastVisitedAt,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt,
            })
            .ToListAsync(ct);

    /// <summary>
    /// 搜索页直读：谓词 = <see cref="LinkSearchScope"/>（title/url/description LIKE ∪ folder_id IN），
    /// 排序 = title COLLATE NOCASE [DESC], id [DESC]，只服务 UI/explain 的缺省排序形态；
    /// 其它排序字段或混入其它过滤 → false（回 EF，语义面不做第二套实现）。
    /// 原生 reader 省掉 EF 查询编译/投影；覆盖索引 `idx_links_search` 让排序沿索引走。
    /// </summary>
    private static bool TryBuildRawSearchSql(
        LinkQuerySpec spec, out string sql, out (string Name, object? Value)[] parameters)
    {
        sql = string.Empty;
        parameters = [];

        var f = spec.Filter;
        if (f.SearchScope is not { } scope) return false;
        if (!IsAreaFilter(f with { SearchScope = null })) return false;
        if (spec.Page.Size <= 0) return false;

        if (spec.Sort.Count > 1) return false;
        var clause = spec.Sort.Count == 1 ? spec.Sort[0] : new SortSpec("title", SortDir.Asc);
        if (clause.Field != "title") return false;

        if (!TryBuildSearchWhereSql(scope, out var whereSql, out var searchParameters)) return false;

        var dir = clause.Dir == SortDir.Desc ? "DESC" : "ASC";
        var sb = new StringBuilder(
            "SELECT id, url, title, description, favicon_url, folder_id, last_visited_at, visit_count, is_important, created_at, updated_at FROM links WHERE ");
        sb.Append('(').Append(whereSql).Append(')');
        sb.Append($" ORDER BY title COLLATE NOCASE {dir}, id {dir} LIMIT @take OFFSET @skip");

        var list = new List<(string Name, object? Value)>(searchParameters.Length + 2);
        list.AddRange(searchParameters);
        list.Add(("@take", spec.Page.Size));
        list.Add(("@skip", spec.Page.Skip));
        sql = sb.ToString();
        parameters = [.. list];
        return true;
    }

    /// <summary>目录集合超过这个数就改走 `json_each`，不逐个绑定参数（SQLite 变量上限 32766）。</summary>
    private const int MaxInlineFolderIds = 512;

    /// <summary>搜索谓词的唯一 SQL 构造（计数与取页共用；用户输入只经参数绑定）。</summary>
    private static bool TryBuildSearchWhereSql(
        LinkSearchScope scope, out string where, out (string Name, object? Value)[] parameters)
    {
        where = string.Empty;
        parameters = [];
        var conditions = new List<string>(4);
        if (scope.Title) conditions.Add("title LIKE @q ESCAPE '\\'");
        if (scope.Url) conditions.Add("url LIKE @q ESCAPE '\\'");
        if (scope.Description) conditions.Add("description LIKE @q ESCAPE '\\'");
        var folders = scope.FolderIdValues;
        var list = new List<(string Name, object? Value)>(Math.Min(folders.Length, MaxInlineFolderIds) + 2)
        {
            ("@q", $"%{EscapeLike(scope.Query)}%"),
        };
        if (folders.Length > 0)
        {
            // ⚠️ 目录集合可能到几万个（深树 + 祖先名命中会展开整棵子树）：逐个绑定会撞 SQLite 的
            //    变量上限（SQLITE_MAX_VARIABLE_NUMBER = 32766），实测 5 万目录直接
            //    "too many SQL variables"。大集合改走 `json_each(单个 JSON 参数)`：
            //    一个参数、一条语句，`folder_id IN (SELECT value FROM json_each(@fjson))` 与
            //    `IN (...)` 语义一致（字符串完全相等）。小集合仍用占位符，不引入函数调用。
            if (folders.Length <= MaxInlineFolderIds)
            {
                conditions.Add("folder_id IN (" + string.Join(",", folders.Select((_, i) => "@f" + i)) + ")");
                for (var i = 0; i < folders.Length; i++) list.Add(("@f" + i, folders[i]));
            }
            else
            {
                conditions.Add("folder_id IN (SELECT value FROM json_each(@fjson))");
                list.Add(("@fjson", scope.FolderIdJson));
            }
        }
        if (conditions.Count == 0) return false;

        where = string.Join(" OR ", conditions);
        parameters = [.. list];
        return true;
    }

    /// <summary>
    /// 直读 SQL 生成（只服务"目录页形态"）：谓词 = 恰好一个归属条件（<c>folder_id = @folder</c> | <c>folder_id IS NULL</c>），
    /// 其余过滤字段必须全空；排序 = 0/1 个子句且列在白名单内。任一不满足 → false（回 EF）。
    /// ORDER BY 与 <c>EfSortEngine.Apply</c> + BuildPageQuery 的 ID 兜底逐字对齐：
    /// 名称/地址用 <c>COLLATE NOCASE</c>；<c>last_visited_at</c> 前置判空子句（为空恒排最后）；
    /// ID 兜底方向随排序列（倒序扫也能整段命中 <c>idx_links_folder_*</c> 复合索引）。
    /// </summary>
    private static bool TryBuildRawPageSql(LinkQuerySpec spec, out string sql, out (string Name, object? Value)[] parameters)
    {
        sql = string.Empty;
        parameters = [];

        var f = spec.Filter;
        var folderKey = f.FolderId?.Value;   // FolderId → 内层字符串（SQL 参数绑不了包装类型）
        var byFolder = folderKey is not null && f.Unfiled is null && IsAreaFilter(f);
        var byRoot = folderKey is null && f.Unfiled == true && IsAreaFilter(f);
        if (!byFolder && !byRoot) return false;

        if (spec.Sort.Count > 1) return false;
        var clause = spec.Sort.Count == 1 ? spec.Sort[0] : new SortSpec("title", SortDir.Asc);
        var expr = clause.Field switch
        {
            "title" => "title COLLATE NOCASE",
            "url" => "url COLLATE NOCASE",
            "created_at" => "created_at",
            "updated_at" => "updated_at",
            "last_visited_at" => "last_visited_at",
            "visit_count" => "visit_count",
            "is_important" => "is_important",
            _ => null,
        };
        if (expr is null) return false;

        var dir = clause.Dir == SortDir.Desc ? "DESC" : "ASC";
        var order = clause.Field == "last_visited_at"
            ? $"(last_visited_at IS NULL) ASC, last_visited_at {dir}, id {dir}"
            : $"{expr} {dir}, id {dir}";

        var sb = new StringBuilder(
            "SELECT id, url, title, description, favicon_url, folder_id, last_visited_at, visit_count, is_important, created_at, updated_at FROM links");
        sb.Append(" WHERE ").Append(byFolder ? "folder_id = @folder" : "folder_id IS NULL");
        sb.Append(" ORDER BY ").Append(order);

        var list = new List<(string Name, object? Value)>(3);
        if (byFolder) list.Add(("@folder", folderKey));
        if (spec.Page.Size > 0)
        {
            sb.Append(" LIMIT @take OFFSET @skip");
            list.Add(("@take", spec.Page.Size));
            list.Add(("@skip", spec.Page.Skip));
        }
        sql = sb.ToString();
        parameters = [.. list];
        return true;
    }

    /// <summary>归属谓词之外无任何其它过滤（直读的形态前提）——多一个条件即回 EF，语义面不做第二种实现。
    /// 归属条件本身（folder_id / unfiled）由调用方判定，这里不重复管。</summary>
    private static bool IsAreaFilter(LinkFilter f)
        => f.Search is null
        && f.SearchScope is null
        && f.IsImportant is null
        && f.CreatedFrom is null && f.CreatedTo is null
        && f.IdIn is null && f.FolderIdsIn is null
        && string.IsNullOrEmpty(f.TitleContains) && string.IsNullOrEmpty(f.UrlContains)
        && string.IsNullOrEmpty(f.UrlStarts) && string.IsNullOrEmpty(f.DescriptionContains)
        && f.UpdatedFrom is null && f.UpdatedTo is null
        && f.LastVisitedFrom is null && f.LastVisitedTo is null
        && f.NeverVisited is null
        && f.VisitCountMin is null && f.VisitCountMax is null;

    private async Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ReadDtosRawAsync(
        string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        using var cmd = await RawSql.CreateAsync(db, sql, ct);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<LinkPocket.Contracts.LinkDto>();
        while (await reader.ReadAsync(ct))
            rows.Add(ReadDto(reader));
        return rows;
    }

    private static LinkPocket.Contracts.LinkDto ReadDto(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new()
        {
            LinkId = reader.GetString(0),
            Url = reader.GetString(1),
            Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Description = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            FaviconUrl = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            ListId = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastVisitedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            VisitCount = reader.GetInt32(7),
            IsImportant = reader.GetBoolean(8),
            CreatedAt = reader.GetDateTime(9),
            UpdatedAt = reader.GetDateTime(10),
        };

    public async Task<IReadOnlyList<string>> ListFaviconUrlsAsync(CancellationToken ct)
        => await db.Links.AsNoTracking()
            .Where(l => l.FaviconUrl != null && l.FaviconUrl != "")
            .Select(l => l.FaviconUrl!)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Link>> ListAsync(LinkQuerySpec spec, CancellationToken ct)
    {
        var q = BuildPageQuery(spec);

        return await q.ToListAsync(ct);
    }

    /// <summary>
    /// 首屏快路径：
    /// <list type="bullet">
    /// <item>纯文本范围：**先取首页**——不满一页说明已经拿全（总数 = 首页行数，稀疏命中只扫一遍）；
    /// 满页再补一次 count（密集命中首页沿索引只走几行，count 是唯一必付的全扫）。</item>
    /// <item>path 范围（目录子树）：沿有序索引**一次扫描**同时计数 + 收集首页 ID；
    /// 稀疏子树不必为 count 和首页各扫一遍。</item>
    /// </list>
    /// 两种路径都在同一读事务内完成。
    /// </summary>
    public async Task<(int Total, IReadOnlyList<LinkPocket.Contracts.LinkDto> FirstPage)> CountAndFirstPageAsync(
        LinkSearchScope scope, string sortBy, string sortOrder, int pageSize, int skip, CancellationToken ct)
    {
        if (!scope.Any) return (0, []);
        var size = Math.Max(0, pageSize);
        var offset = Math.Max(0, skip);
        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);

        // 只有 title 排序有对应的原生有序索引；其它排序沿用既有 count + page 语义。
        if (!string.IsNullOrEmpty(sortBy) && !string.Equals(sortBy, "title", StringComparison.OrdinalIgnoreCase))
            return await CountAndPageAsync(scope, sortBy, sortOrder, size, offset, ct);
        if (!TryBuildSearchWhereSql(scope, out var where, out var parameters))
            return (0, []);

        if (size == 0)
            return (await CountAsync(new LinkFilter { SearchScope = scope }, ct), []);
        if (scope.Folders.Count == 0 && offset == 0)
        {
            var page = await ListDtosAsync(new LinkQuerySpec
            {
                Filter = new LinkFilter { SearchScope = scope },
                Sort = [new SortSpec("title", desc ? SortDir.Desc : SortDir.Asc)],
                Page = new PageSpec(1, size),
            }, ct);
            if (page.Count < size) return (page.Count, page);   // 不满一页 = 没有下一页
            var total = await CountAsync(new LinkFilter { SearchScope = scope }, ct);
            return (total, page);
        }

        // 一次有序扫描：读到全部命中以计数，只保留 [offset, offset+size) 的 ID。
        // path 范围与「文件夹块之后的链接段」共用这条单扫（后者不再 count + 取页各扫一遍）。
        var dir = desc ? "DESC" : "ASC";
        var scanSql = "SELECT id FROM links WHERE (" + where + ")"
            + $" ORDER BY title COLLATE NOCASE {dir}, id {dir}";
        var wanted = new List<string>(Math.Min(size, 4096));
        var count = 0;
        using (var scan = await RawSql.CreateAsync(db, scanSql, ct))
        {
            foreach (var (name, value) in parameters) scan.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = await scan.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (count >= offset && wanted.Count < size) wanted.Add(reader.GetString(0));
                count++;
            }
        }

        if (count == 0 || wanted.Count == 0) return (count, []);
        var names = string.Join(",", wanted.Select((_, i) => "@id" + i));
        var pageRows = await ReadDtosRawAsync(
            "SELECT id, url, title, description, favicon_url, folder_id, last_visited_at, visit_count,"
            + " is_important, created_at, updated_at FROM links WHERE id IN (" + names + ")",
            wanted.Select((id, i) => ("@id" + i, (object?)id)).ToArray(), ct);

        var byId = pageRows.ToDictionary(r => r.LinkId, StringComparer.Ordinal);
        var ordered = new List<LinkPocket.Contracts.LinkDto>(wanted.Count);
        foreach (var id in wanted)
            if (byId.TryGetValue(id, out var dto)) ordered.Add(dto);
        return (count, ordered);
    }

    private async Task<(int Total, IReadOnlyList<LinkPocket.Contracts.LinkDto> FirstPage)> CountAndPageAsync(
        LinkSearchScope scope, string sortBy, string sortOrder, int pageSize, int skip, CancellationToken ct)
    {
        var filter = new LinkFilter { SearchScope = scope };
        var total = await CountAsync(filter, ct);
        if (total == 0 || pageSize <= 0) return (total, []);
        var page = await ListDtosAsync(new LinkQuerySpec
        {
            Filter = filter,
            Sort = [new SortSpec(sortBy, string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase)
                ? SortDir.Desc : SortDir.Asc)],
            Page = new PageSpec(Size: pageSize, SkipOverride: skip),
        }, ct);
        return (total, page);
    }

    public async Task<int> CountAsync(LinkFilter filter, CancellationToken ct)
    {        // 直读（B 路线）：目录页的计数形态（folder = X / 根级）——单条 COUNT 直发，省掉 EF 查询管线；
        // 其余形态仍走 EF（过滤规则单一出处）。
        // 搜索计数也直读：与取页共用同一谓词构造，避免 EF COUNT 的查询管线开销。
        if (filter.SearchScope is { } scope
            && IsAreaFilter(filter with { SearchScope = null })
            && TryBuildSearchWhereSql(scope, out var searchWhere, out var searchParameters))
        {
            // 位置范围**独占**且目录集合很大（深树 5 万个）：`JOIN json_each` 比
            // `folder_id IN (SELECT value FROM json_each(...))` 快一倍（实测 45ms → 22ms）。
            // 混合范围仍走 OR 谓词，不能改写成 JOIN（同一条链接可能同时命中文本与目录）。
            if (!scope.Title && !scope.Url && !scope.Description
                && scope.Folders.Count > MaxInlineFolderIds)
            {
                using var joinCmd = await RawSql.CreateAsync(db,
                    "SELECT COUNT(*) FROM links l JOIN json_each(@fjson) j ON l.folder_id = j.value", ct);
                joinCmd.Parameters.AddWithValue("@fjson", scope.FolderIdJson);
                return Convert.ToInt32(await joinCmd.ExecuteScalarAsync(ct),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            using var searchCmd = await RawSql.CreateAsync(db,
                "SELECT COUNT(*) FROM links WHERE (" + searchWhere + ")", ct);
            foreach (var (name, value) in searchParameters)
                searchCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Convert.ToInt32(await searchCmd.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // 智能列表计数也直读：与取页共用同一谓词（时间窗走 idx_links_created/updated/last_visited，
        // 访问次数过滤走表扫但只数不取列），十万行 COUNT 不经过 EF 查询管线。
        if (TryBuildSmartListWhereSql(filter, out var smartWhere, out var smartParameters))
        {
            // 计数固定走最小的过滤列索引；十万吨级时别让计划器去扫带投影列的宽索引。
            var smartIndex = SmartListFilterIndex(filter);
            using var smartCmd = await RawSql.CreateAsync(db,
                "SELECT COUNT(*) FROM links"
                + (smartIndex is null ? string.Empty : " INDEXED BY " + smartIndex)
                + " WHERE (" + smartWhere + ")", ct);
            foreach (var (name, value) in smartParameters)
                smartCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Convert.ToInt32(await smartCmd.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var folderKey = filter.FolderId?.Value;
        var byFolder = folderKey is not null && filter.Unfiled is null && IsAreaFilter(filter);
        var byRoot = folderKey is null && filter.Unfiled == true && IsAreaFilter(filter);
        if (byFolder || byRoot)
        {
            var sql = byFolder
                ? "SELECT COUNT(*) FROM links WHERE folder_id = @folder"
                : "SELECT COUNT(*) FROM links WHERE folder_id IS NULL";
            using var cmd = await RawSql.CreateAsync(db, sql, ct);
            if (byFolder) cmd.Parameters.AddWithValue("@folder", folderKey);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        return await ApplyFilter(db.Links.AsNoTracking(), filter).CountAsync(ct);
    }

    public async Task<IReadOnlyDictionary<FolderId, int>> CountByFolderAsync(CancellationToken ct)
        => (await db.Links.AsNoTracking()
                .Where(l => l.ListId != null)
                .GroupBy(l => l.ListId!)
                .Select(g => new { FolderId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => new FolderId(x.FolderId), x => x.Count);

    public Task<Link> AddAsync(Link link, CancellationToken ct)
    {
        // 只登记不保存：提交权归工作单元 CommitAsync（失败零副作用、干跑可回滚的前提）。
        // 同批"先移入回收站、后按原 ID 还原/重建"：跟踪器里同 key 的待删实例 = 行仍在库 →
        // 交给新实例接管（待删取消 + 新值按 UPDATE 提交；直接 Add 会撞 EF 身份冲突）。
        // 接管扫描只在本上下文走过 Remove 后才做（_removed）：同批删除→重建的场景才需要它，
        // 而每加必扫会把 O(1) 的新增变成 O(n²)（10k 导入实测 34s，见 _removed 注释）。
        if (_removed)
        {
            var tracked = db.Links.Local.FirstOrDefault(l => l.LinkId == link.LinkId);
            if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted)
            {
                db.Entry(tracked).State = EntityState.Detached;
                db.Links.Attach(link);
                db.Entry(link).State = EntityState.Modified;
                return Task.FromResult(link);
            }
        }
        db.Links.Add(link);
        return Task.FromResult(link);
    }

    public Task UpdateAsync(Link link, CancellationToken ct)
    {
        db.Links.Update(link);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(LinkId id, CancellationToken ct)
    {
        // 语义同 EfFolderRepository.RemoveAsync：SQL 优先；未命中且跟踪器 Added = 取消暂存。
        var link = await db.Links.FirstOrDefaultAsync(l => l.LinkId == id.Value, ct);
        if (link is null)
        {
            var pending = db.Links.Local.FirstOrDefault(l => l.LinkId == id.Value);
            if (pending is not null && db.Entry(pending).State == EntityState.Added) db.Links.Remove(pending);
            return;
        }
        db.Links.Remove(link);
        _removed = true;   // 出现同 key 待删实例 → 后续 Add 才需要接管扫描（AddAsync）
    }

    public Task RemoveRangeAsync(IReadOnlyList<Link> links, CancellationToken ct)
    {
        // 已读出的实体按实体登记删除（Entry() 走身份映射，O(1)/行；不发 SELECT）——
        // 逐条 RemoveAsync 会为每行发一条点查（万行子树 = 万条 SELECT + 万次身份解析），
        // 那是"删一个大文件夹"实测卡顿的主因之一（子树级联统一走本方法）。
        // ⚠️ 同 EfFolderRepository.RemoveRangeAsync：批/宏内同一实体可能已被跟踪（先移动再删除），
        // 必须拿被跟踪的实例登记删除；跟踪表惰性建，分离热路径零额外开销。
        Dictionary<string, Link>? tracked = null;
        foreach (var link in links)
        {
            if (db.Entry(link).State != EntityState.Detached)
            {
                db.Entry(link).State = EntityState.Deleted;
                continue;
            }
            tracked ??= db.ChangeTracker.Entries<Link>()
                .ToDictionary(e => e.Entity.LinkId, e => e.Entity, StringComparer.Ordinal);
            if (tracked.TryGetValue(link.LinkId, out var existing))
                db.Entry(existing).State = EntityState.Deleted;  // 同 key 已跟踪（含已删除 → 空操作）
            else
                db.Entry(link).State = EntityState.Deleted;
        }
        if (links.Count > 0) _removed = true;   // 接管扫描的开启条件与 RemoveAsync 一致（见该字段说明）
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListExistingIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var found = new List<string>();
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var names = string.Join(", ", chunk.Select((_, i) => "@id" + i));
            using var cmd = await RawSql.CreateAsync(db, "SELECT id FROM links WHERE id IN (" + names + ")", ct);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@id" + i, chunk[i]);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
        }
        return found;
    }

    public async Task<IReadOnlyList<(string? ListId, string Url)>> ListLandingUrlPairsAsync(
        IReadOnlyList<string> urls, CancellationToken ct)
    {
        if (urls.Count == 0) return [];
        var pairs = new List<(string? ListId, string Url)>();
        foreach (var chunk in urls.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var names = string.Join(", ", chunk.Select((_, i) => "@u" + i));
            using var cmd = await RawSql.CreateAsync(db,
                "SELECT folder_id, url FROM links WHERE url IN (" + names + ")", ct);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@u" + i, chunk[i]);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                pairs.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1)));
        }
        return pairs;
    }

    public async Task<IReadOnlyList<Link>> FindByUrlAsync(string url, CancellationToken ct)
        => await db.Links.AsNoTracking().Where(l => l.Url == url).ToListAsync(ct);

    public async Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDuplicatedDtosAsync(CancellationToken ct)
    {
        const string columns = "id, url, title, description, favicon_url, folder_id, last_visited_at, " +
                               "visit_count, is_important, created_at, updated_at";

        // 先只把「重复 URL」取出来（GROUP BY 走 idx_links_url 覆盖索引，100k 约 10ms）。
        // 再把对应链接取回：
        // · 重复行数超过全库一半 → **顺序扫一遍全表 + 内存过滤**：此时逐 URL 回表是随机读。
        // · 否则按批 IN 取回（每批 400 个 URL，不会撞 SQLite 参数上限）。
        // 两条路都走原生 reader **直出 DTO**，不物化 Link 实体再逐条 ToDto（省掉实体对象与属性赋值）。
        // 顺序不保证：分组/组内排序由调用方（DedupScanHandler）统一做。
        var duplicateGroups = await db.Links.AsNoTracking()
            .GroupBy(l => l.Url)
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (duplicateGroups.Count == 0) return [];

        var duplicateRows = duplicateGroups.Sum(g => g.Count);
        var total = await db.Links.AsNoTracking().CountAsync(ct);
        if ((long)duplicateRows * 2 >= total)
        {
            var keep = new HashSet<string>(duplicateGroups.Select(g => g.Key), StringComparer.Ordinal);
            var all = await ReadDtosRawAsync($"SELECT {columns} FROM links", [], ct);
            return all.Where(l => keep.Contains(l.Url)).ToList();
        }

        var rows = new List<LinkPocket.Contracts.LinkDto>(duplicateRows);
        foreach (var chunk in duplicateGroups.Chunk(900))
        {
            var urls = chunk.Select(g => g.Key).ToArray();
            var names = string.Join(", ", urls.Select((_, i) => "@u" + i));
            var parameters = urls.Select((u, i) => ("@u" + i, (object?)u)).ToArray();
            rows.AddRange(await ReadDtosRawAsync(
                $"SELECT {columns} FROM links WHERE url IN ({names})", parameters, ct));
        }
        return rows;
    }

    public async Task<IReadOnlyList<(string Url, int Count, string? FirstListId)>> ListDuplicateGroupSummariesAsync(
        CancellationToken ct)
    {
        var groups = await db.Links.AsNoTracking()
            .GroupBy(l => l.Url)
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (groups.Count == 0) return [];

        var keep = new HashSet<string>(groups.Select(g => g.Key), StringComparer.Ordinal);
        var duplicateRows = groups.Sum(g => g.Count);
        var total = await db.Links.AsNoTracking().CountAsync(ct);

        // 只取四列（url / folder_id / created_at / id）找每组首条的目录：摘要模式不需要链接正文。
        var first = new Dictionary<string, (DateTime CreatedAt, string LinkId, string? ListId)>(
            keep.Count, StringComparer.Ordinal);
        void Consider(string url, string? listId, DateTime createdAt, string linkId)
        {
            if (!keep.Contains(url)) return;
            if (!first.TryGetValue(url, out var current)
                || createdAt < current.CreatedAt
                || (createdAt == current.CreatedAt && string.CompareOrdinal(linkId, current.LinkId) < 0))
                first[url] = (createdAt, linkId, listId);
        }

        async Task ReadAsync(string sql, (string Name, object? Value)[] parameters)
        {
            using var cmd = await RawSql.CreateAsync(db, sql, ct);
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var url = reader.GetString(0);
                if (!keep.Contains(url)) continue;   // 全表扫时先按 URL 过滤，唯一行不再解析时间/ID
                Consider(
                    url,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetDateTime(2),
                    reader.GetString(3));
            }
        }

        // 重复面大或组数很多时，一次顺序扫全表（四列）更便宜；组数少时才按 URL 批量取回。
        // 组数阈值避免 2 万个组被拆成几十条 IN 查询（每条查询的固定开销会反超全表扫）。
        if ((long)duplicateRows * 2 >= total || groups.Count >= 5000)
        {
            await ReadAsync("SELECT url, folder_id, created_at, id FROM links", []);
        }
        else
        {
            foreach (var chunk in groups.Chunk(900))
            {
                var urls = chunk.Select(g => g.Key).ToArray();
                var names = string.Join(", ", urls.Select((_, i) => "@u" + i));
                var parameters = urls.Select((u, i) => ("@u" + i, (object?)u)).ToArray();
                await ReadAsync(
                    $"SELECT url, folder_id, created_at, id FROM links WHERE url IN ({names})", parameters);
            }
        }

        return groups
            .Select(g => (g.Key, g.Count, first.TryGetValue(g.Key, out var f) ? f.ListId : null))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static IQueryable<Link> ApplyFilter(IQueryable<Link> q, LinkFilter f)
    {
        if (!string.IsNullOrEmpty(f.Search))
        {
            var like = $"%{EscapeLike(f.Search)}%";
            q = q.Where(l => EF.Functions.Like(l.Title, like, LikeEscape)
                          || EF.Functions.Like(l.Url, like, LikeEscape)
                          || (l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape)));
        }
        if (f.SearchScope is { } scope) q = ApplySearchScope(q, scope);
        if (f.FolderId is { } folder) q = q.Where(l => l.ListId == folder.Value);
        if (f.IsImportant is { } imp) q = q.Where(l => l.IsImportant == imp);
        if (f.CreatedFrom is { } from) q = q.Where(l => l.CreatedAt >= from);
        if (f.CreatedTo is { } to) q = q.Where(l => l.CreatedAt <= to);

        // —— links.query 结构化字段（每条独立下推；字段互斥由模块层保证）——
        if (f.IdIn is { } ids) q = q.Where(l => ids.Contains(l.LinkId));
        if (f.FolderIdsIn is { } folderIds) q = q.Where(l => l.ListId != null && folderIds.Contains(l.ListId));
        if (f.Unfiled is { } unfiled) q = unfiled ? q.Where(l => l.ListId == null) : q.Where(l => l.ListId != null);
        if (!string.IsNullOrEmpty(f.TitleContains))
        {
            var like = $"%{EscapeLike(f.TitleContains)}%";
            q = q.Where(l => l.Title != null && EF.Functions.Like(l.Title, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.UrlContains))
        {
            var like = $"%{EscapeLike(f.UrlContains)}%";
            q = q.Where(l => EF.Functions.Like(l.Url, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.UrlStarts))
        {
            var like = $"{EscapeLike(f.UrlStarts)}%";
            q = q.Where(l => EF.Functions.Like(l.Url, like, LikeEscape));
        }
        if (!string.IsNullOrEmpty(f.DescriptionContains))
        {
            var like = $"%{EscapeLike(f.DescriptionContains)}%";
            q = q.Where(l => l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape));
        }
        if (f.UpdatedFrom is { } uFrom) q = q.Where(l => l.UpdatedAt >= uFrom);
        if (f.UpdatedTo is { } uTo) q = q.Where(l => l.UpdatedAt <= uTo);
        if (f.LastVisitedFrom is { } lvFrom) q = q.Where(l => l.LastVisitedAt >= lvFrom);
        if (f.LastVisitedTo is { } lvTo) q = q.Where(l => l.LastVisitedAt <= lvTo);
        if (f.NeverVisited is { } never) q = never ? q.Where(l => l.LastVisitedAt == null) : q.Where(l => l.LastVisitedAt != null);
        if (f.VisitCountMin is { } vcMin) q = q.Where(l => l.VisitCount >= vcMin);
        if (f.VisitCountMax is { } vcMax) q = q.Where(l => l.VisitCount <= vcMax);
        return q;
    }

    /// <summary>
    /// search.links 多范围搜索的 SQL 下推：三字段 OR 包含 ∪ 目录集合，两段之间亦为 OR。
    /// 中缀 LIKE 注定全表扫描（已知且接受，见 IndexPlanTests 负向断言）——但过滤发生在 SQL 端，
    /// 不再把全库读进内存逐条比对。
    /// </summary>
    private static IQueryable<Link> ApplySearchScope(IQueryable<Link> q, LinkSearchScope scope)
    {
        var like = $"%{EscapeLike(scope.Query)}%";
        var folderIds = scope.FolderIdValues;
        var hasFolderRange = folderIds.Length > 0;

        if (!scope.Title && !scope.Url && !scope.Description && !hasFolderRange)
            return q.Where(_ => false);   // 全范围未启用 = 无命中

        return q.Where(l =>
            (scope.Title && EF.Functions.Like(l.Title, like, LikeEscape))
            || (scope.Url && EF.Functions.Like(l.Url, like, LikeEscape))
            || (scope.Description && l.Description != null && EF.Functions.Like(l.Description, like, LikeEscape))
            || (hasFolderRange && l.ListId != null && folderIds.Contains(l.ListId)));
    }

    private const string LikeEscape = "\\";

    /// <summary>LIKE 通配符转义：关键词里的 % / _ / \ 按字面匹配，不做通配（用户输入不得改变匹配语义）。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

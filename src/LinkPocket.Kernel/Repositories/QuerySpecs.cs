namespace LinkPocket.Kernel;

/// <summary>排序方向。</summary>
public enum SortDir
{
    Asc,
    Desc,
}

/// <summary>排序子句：白名单字段（由 ISortEngine 的字段映射校验，防注入）+ 方向。</summary>
public sealed record SortSpec(string Field, SortDir Dir);

/// <summary>分页：size = 0 表示全量（保留现状语义）。</summary>
public sealed record PageSpec(int Index = 1, int Size = 0)
{
    public int Skip => (Math.Max(1, Index) - 1) * Math.Max(0, Size);
}

/// <summary>
/// search.links 的多范围搜索谓词（SQL 下推）：三字段 OR 包含（各范围可开关）
/// ∪ 目录集合（path 范围命中目录名 → 子树展开结果）。
/// 两类范围之间亦为 OR（任一命中即算命中）；与 <see cref="LinkFilter"/> 的其它条件为 AND。
/// </summary>
public sealed record LinkSearchScope
{
    public string Query { get; init; } = string.Empty;

    public bool Title { get; init; }
    public bool Url { get; init; }
    public bool Description { get; init; }

    /// <summary>path 范围：归属目录 ∈ 集合（启用但空集合 = 该范围无命中）。</summary>
    public IReadOnlyList<FolderId> Folders { get; init; } = [];

    /// <summary>是否至少启用了一个范围（全未启用 = 无命中，由调用方决定是否直接短路）。</summary>
    public bool Any => Title || Url || Description || Folders.Count > 0;
}

/// <summary>
/// 链接过滤（纯过滤条件；null = 不过滤）。
/// 基础字段服务既有查询（links.list 等）；后段结构化字段服务 links.query：字段名/操作符白名单在 Links 模块校验，这里只是强类型数据形态，EF 实现逐条 SQL 下推。
/// 同一字段的重复条件由模块层拒绝（一条过滤一个值）。
/// </summary>
public sealed record LinkFilter
{
    public string? Search { get; init; }
    public FolderId? FolderId { get; init; }
    public bool? IsImportant { get; init; }
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo { get; init; }

    /// <summary>search.links 多范围搜索（SQL 下推；null = 不过滤）。</summary>
    public LinkSearchScope? SearchScope { get; init; }

    // —— links.query 结构化字段（字段名见 Links 模块白名单）——

    /// <summary>id eq/in：按 ID 取（启用但空集合 = 无命中；null = 不过滤）。</summary>
    public IReadOnlyList<string>? IdIn { get; init; }

    /// <summary>folder_id isnull：根级（无归属）书签。</summary>
    public bool? Unfiled { get; init; }

    /// <summary>title contains。</summary>
    public string? TitleContains { get; init; }

    /// <summary>url contains。</summary>
    public string? UrlContains { get; init; }

    /// <summary>url starts。</summary>
    public string? UrlStarts { get; init; }

    /// <summary>description contains。</summary>
    public string? DescriptionContains { get; init; }

    /// <summary>updated_at 范围（between = From+To，单边 = gte/lte）。</summary>
    public DateTime? UpdatedFrom { get; init; }
    public DateTime? UpdatedTo { get; init; }

    /// <summary>last_visited_at 范围。</summary>
    public DateTime? LastVisitedFrom { get; init; }
    public DateTime? LastVisitedTo { get; init; }

    /// <summary>last_visited_at isnull：从未查看。</summary>
    public bool? NeverVisited { get; init; }

    /// <summary>visit_count 范围（gte/lte）。</summary>
    public int? VisitCountMin { get; init; }
    public int? VisitCountMax { get; init; }
}

/// <summary>链接查询规格 = 过滤 + 排序 + 分页（标准参数的强类型形态）。</summary>
public sealed record LinkQuerySpec
{
    public LinkFilter Filter { get; init; } = new();
    public IReadOnlyList<SortSpec> Sort { get; init; } = [];
    public PageSpec Page { get; init; } = new();
}

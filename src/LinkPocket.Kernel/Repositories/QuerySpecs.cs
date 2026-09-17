namespace LinkPocket.Kernel;

/// <summary>排序方向。</summary>
public enum SortDir
{
    Asc,
    Desc,
}

/// <summary>排序子句：白名单字段（由 ISortEngine 的字段映射校验，防注入）+ 方向。</summary>
public sealed record SortSpec(string Field, SortDir Dir);

/// <summary>分页：size = 0 表示全量（保留现状语义，方案 3.3）。</summary>
public sealed record PageSpec(int Index = 1, int Size = 0)
{
    public int Skip => (Math.Max(1, Index) - 1) * Math.Max(0, Size);
}

/// <summary>链接过滤（纯过滤条件；null = 不过滤）。</summary>
public sealed record LinkFilter
{
    public string? Search { get; init; }
    public FolderId? FolderId { get; init; }
    public bool? IsImportant { get; init; }
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo { get; init; }
}

/// <summary>链接查询规格 = 过滤 + 排序 + 分页（方案 3.3 标准参数的强类型形态）。</summary>
public sealed record LinkQuerySpec
{
    public LinkFilter Filter { get; init; } = new();
    public IReadOnlyList<SortSpec> Sort { get; init; } = [];
    public PageSpec Page { get; init; } = new();
}

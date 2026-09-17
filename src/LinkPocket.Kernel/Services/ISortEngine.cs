using System.Linq.Expressions;

namespace LinkPocket.Kernel;

/// <summary>排序字段映射：白名单字段名 → 实体属性表达式（防注入的唯一闸口；表达式形态可被 EF 翻译为 SQL）。</summary>
public sealed class SortFieldMap<T>
{
    private readonly Dictionary<string, Expression<Func<T, object?>>> _selectors;

    public SortFieldMap(IReadOnlyDictionary<string, Expression<Func<T, object?>>> selectors)
        => _selectors = new Dictionary<string, Expression<Func<T, object?>>>(selectors, StringComparer.Ordinal);

    public bool TryGetSelector(string field, out Expression<Func<T, object?>> selector)
        => _selectors.TryGetValue(field, out selector!);

    /// <summary>默认排序字段（当前定稿：名称升序 + ID 兜底）。</summary>
    public string DefaultField { get; init; } = "title";
}

/// <summary>
/// 排序唯一出处（方案 4.1）：消灭 LinkPocketApi.SortLinks 与 LinkService 两套排序。
/// Queryable 输入 → SQL 下推；白名单字段映射防注入。实现于 Data。
/// </summary>
public interface ISortEngine
{
    /// <summary>应用排序；sort 为空 = 默认「名称升序 + ID 次序兜底」（行为等价项，方案 3.3）。</summary>
    IOrderedQueryable<T> Apply<T>(IQueryable<T> source, IReadOnlyList<SortSpec> sort, SortFieldMap<T> fieldMap);
}

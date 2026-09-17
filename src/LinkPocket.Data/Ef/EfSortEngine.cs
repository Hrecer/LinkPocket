using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>排序引擎实现（方案 4.1）：白名单表达式映射 → EF 翻译 SQL 下推，消灭两套排序实现。</summary>
internal sealed class EfSortEngine : ISortEngine
{
    internal static readonly EfSortEngine Instance = new();

    /// <summary>链接排序白名单（字段名 = 机器可读稳定名）。</summary>
    internal static readonly SortFieldMap<Link> LinkFields = new(new Dictionary<string, System.Linq.Expressions.Expression<Func<Link, object?>>>
    {
        ["title"] = l => l.Title,
        ["url"] = l => l.Url,
        ["created_at"] = l => l.CreatedAt,
        ["updated_at"] = l => l.UpdatedAt,
        ["last_visited_at"] = l => l.LastVisitedAt,
        ["visit_count"] = l => l.VisitCount,
        ["is_important"] = l => l.IsImportant,
    })
    {
        DefaultField = "title",
    };

    /// <summary>文件夹排序白名单。</summary>
    internal static readonly SortFieldMap<Folder> FolderFields = new(new Dictionary<string, System.Linq.Expressions.Expression<Func<Folder, object?>>>
    {
        ["name"] = f => f.Name,
        ["created_at"] = f => f.CreatedAt,
        ["updated_at"] = f => f.UpdatedAt,
        ["visit_count"] = f => f.VisitCount,
        ["sort_order"] = f => f.SortOrder,
    })
    {
        DefaultField = "name",
    };

    public IOrderedQueryable<T> Apply<T>(IQueryable<T> source, IReadOnlyList<SortSpec> sort, SortFieldMap<T> fieldMap)
    {
        var clauses = sort.Count > 0 ? sort : [new SortSpec(fieldMap.DefaultField, SortDir.Asc)];

        IOrderedQueryable<T>? ordered = null;
        foreach (var clause in clauses)
        {
            if (!fieldMap.TryGetSelector(clause.Field, out var selector))
                throw new EngineException(
                    EngineErrors.Of("LP.VAL.003", $"未知排序字段「{clause.Field}」"));

            ordered = ordered switch
            {
                null when clause.Dir == SortDir.Asc => source.OrderBy(selector),
                null => source.OrderByDescending(selector),
                _ when clause.Dir == SortDir.Asc => ordered.ThenBy(selector),
                _ => ordered.ThenByDescending(selector),
            };
        }
        return ordered!;
    }
}

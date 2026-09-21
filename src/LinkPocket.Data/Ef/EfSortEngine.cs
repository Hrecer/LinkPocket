using LinkPocket.Contracts;
using LinkPocket.Kernel;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>排序引擎实现：白名单表达式映射 → EF 翻译 SQL 下推，消灭两套排序实现。</summary>
internal sealed class EfSortEngine : ISortEngine
{
    internal static readonly EfSortEngine Instance = new();

    /// <summary>
    /// 名称列的 SQL 排序规则：<c>NOCASE</c> ≈ .NET <c>StringComparer.CurrentCulture</c> 的
    /// 「大小写不敏感」显示口径（「Apple」与「apple」相邻）——BINARY 会把大写全排到小写之前，
    /// 与「按名称升序」的用户直觉不符。单一出处，全库名称排序口径一致。
    /// </summary>
    private const string NameCollation = "NOCASE";

    /// <summary>链接排序白名单（字段名 = 机器可读稳定名）。</summary>
    internal static readonly SortFieldMap<Link> LinkFields = new(new Dictionary<string, System.Linq.Expressions.Expression<Func<Link, object?>>>
    {
        ["title"] = l => EF.Functions.Collate(l.Title, NameCollation),
        ["url"] = l => EF.Functions.Collate(l.Url, NameCollation),
        ["created_at"] = l => l.CreatedAt,
        ["updated_at"] = l => l.UpdatedAt,
        ["last_visited_at"] = l => l.LastVisitedAt,
        ["visit_count"] = l => l.VisitCount,
        ["is_important"] = l => l.IsImportant,
    })
    {
        DefaultField = "title",
        NullLastField = "last_visited_at",
        NullLastSelector = l => l.LastVisitedAt == null,
    };

    /// <summary>文件夹排序白名单。</summary>
    internal static readonly SortFieldMap<Folder> FolderFields = new(new Dictionary<string, System.Linq.Expressions.Expression<Func<Folder, object?>>>
    {
        ["name"] = f => EF.Functions.Collate(f.Name, NameCollation),
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
                    EngineErrors.Of("LP.VAL.003", $"unknown sort field '{clause.Field}'"));

            // 「为空恒排最后」（行为契约 §9）：该列是 NullLastField 时前置判空子句
            // （ORDER BY (col IS NULL) ASC, col）。先前只在【首列】时前置，复合排序里
            // 非首列的 NullLastField 会退化为 SQLite 默认（ASC 时 NULL 排最前）——现在对每个
            // 声明了 NullLastField 的 clause 都生效；单列排序（现状全部调用方）输出不变。
            if (clause.Field == fieldMap.NullLastField && fieldMap.NullLastSelector is { } nullLast)
                ordered = ordered is null ? source.OrderBy(nullLast) : ordered.ThenBy(nullLast);

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

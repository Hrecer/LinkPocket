namespace LinkPocket.Modules.Search;

/// <summary>搜索模块结果 DTO 组。</summary>
/// <summary>单条命中解释（matched ∈ title | url | description | path；
/// 空列表 = SQL 端 LIKE 命中但内存谓词未匹配的 Unicode 大小写边界，属已知边界，见 MODULE.md）。</summary>
public sealed record SearchHit(string LinkId, IReadOnlyList<string> Matched);

/// <summary>search.explain 的逐条命中解释（AI 自校验用）。</summary>
public sealed record SearchExplanation(IReadOnlyList<SearchHit> Hits);
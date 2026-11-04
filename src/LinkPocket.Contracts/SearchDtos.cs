namespace LinkPocket.Contracts;

/// <summary>单条命中解释（matched ∈ title | url | description | path；
/// 空列表 = SQL 端 LIKE 命中但内存谓词未匹配的 Unicode 大小写边界，属已知边界，见搜索模块 MODULE.md）。</summary>
public sealed record SearchHit(string LinkId, IReadOnlyList<string> Matched);

/// <summary>search.explain 的逐条命中解释（AI 自校验用）。
/// Hits 只含当前页；Total/Truncated/Page/PerPage 让调用方知道还有多少、怎么翻。</summary>
public sealed record SearchExplanation(
    IReadOnlyList<SearchHit> Hits,
    int Total,
    bool Truncated,
    int Page,
    int PerPage);

/// <summary>
/// search.session_open 的结果：会话 ID + 本次搜索的命中总数。
/// 会话持有**同一 SQLite 读快照**，后续 search.session_page 的每一页都来自这个快照
/// （count 与所有分页不会跨快照拼接）；用完必须 search.session_close。
/// </summary>
public sealed record SearchSessionOpenResult(
    string SessionId, int Total, IReadOnlyList<LinkDto> FirstPage);

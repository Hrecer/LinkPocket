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

/// <summary>
/// search.reconcile 的结果：把一批变更链接按**当前查询谓词**重新对账。
/// <para><see cref="Matched"/> 只含当前仍命中且仍在库中的链接（最新 DTO）；调用方对
/// 请求里未返回的 ID 按“已删除 / 已不命中”处理。这样增量刷新既不重放操作、也不依赖
/// 变更类型——重复事件、乱序、删了又恢复都幂等。</para>
/// <para><see cref="NeedsFullRefresh"/> = 变更集过大或遇到无法安全局部处理的情况；
/// 调用方必须退回一次全量快照（正确性不依赖增量逻辑）。</para>
/// </summary>
public sealed record SearchReconcileResult(
    IReadOnlyList<LinkDto> Matched,
    bool NeedsFullRefresh);

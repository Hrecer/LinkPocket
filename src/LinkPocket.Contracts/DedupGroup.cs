namespace LinkPocket.Contracts;

/// <summary>一个同址重复组（<c>dedup.scan</c> 的结果单元）。</summary>
/// <param name="Url">组键：完全相同的 URL（Ordinal，与引擎的 <c>Url == Url</c> 判定同口径）。</param>
/// <param name="Count">组内链接数（&gt; 1）。</param>
/// <param name="Links">组内链接（按创建时间升序，平局按 LinkId）。</param>
public sealed record DedupGroup(
    string Url,
    int Count,
    List<LinkDto> Links);

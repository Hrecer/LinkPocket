namespace LinkPocket.Contracts;

/// <summary>页面声明的一条图标候选（<c>&lt;link rel="icon"&gt;</c> 的 href / type / sizes）。</summary>
/// <param name="Url">已解析成绝对地址的 href。</param>
/// <param name="Type">声明的 MIME（可能是空）。</param>
/// <param name="Sizes">声明的 <c>sizes</c> 属性（可能是空，或 <c>any</c>）。</param>
public sealed record FaviconCandidate(string Url, string? Type = null, string? Sizes = null);

/// <summary>
/// 在一页声明的多条图标里排出"该先取哪一张"的顺序——按浏览器的挑法，而不是按声明顺序。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能"取第一个声明"</b>：实测某站点同时声明了
/// <c>&lt;link rel="icon" type="image/svg+xml" href="/images/favicon.svg"&gt;</c>
/// 与 <c>&lt;link rel="icon" href="/images/favicon.ico"&gt;</c>，前者的内容是一个**纯圆占位**，
/// 后者才是真正的品牌标志。按声明顺序取会把那个圆当成站点图标；而浏览器标签页上显示的是后者
/// （浏览器自己的图标库里存的正是 <c>/images/favicon.ico</c> 的 16/32 位图）。
/// </para>
/// <para>
/// <b>排序口径</b>（从高到低）：
/// ① 声明了标签页尺寸（16–64）的位图 —— 浏览器标签页要的就是这一档；
/// ② 更大的位图（128/180 之类）—— 清晰，但不是标签页那张；
/// ③ 没说尺寸的 <c>.ico</c> —— 容器里通常就带 16/32；
/// ④ 没说尺寸的其他位图；
/// ⑤ 矢量（SVG / <c>sizes="any"</c>）—— 任何尺寸都画得出来，但常常是简化版或占位图。
/// 同分保持声明顺序（浏览器对同规格的多条也是按文档顺序）。
/// </para>
/// </remarks>
public static class FaviconCandidatePicker
{
    /// <summary>排出候选顺序（不含站点根兜底——调用方自己会补那一条时用这个）。</summary>
    public static IReadOnlyList<string> Rank(IEnumerable<FaviconCandidate> declared)
        => RankCore(declared, pageUrl: null);

    /// <summary>
    /// 排出候选顺序，并把站点根 <c>/favicon.ico</c> 作为最后一档兜底（去重）。
    /// </summary>
    public static IReadOnlyList<string> Rank(IEnumerable<FaviconCandidate> declared, string pageUrl)
        => RankCore(declared, pageUrl);

    private static IReadOnlyList<string> RankCore(IEnumerable<FaviconCandidate> declared, string? pageUrl)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in declared
                     .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                     .Where(c => !c.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                     .Select((c, index) => (Candidate: c, Index: index))
                     .OrderByDescending(x => Score(x.Candidate))
                     .ThenBy(x => x.Index))
        {
            if (seen.Add(candidate.Candidate.Url)) ordered.Add(candidate.Candidate.Url);
        }

        var root = pageUrl is null ? null : BuildRootFallback(pageUrl);
        if (root is not null && seen.Add(root)) ordered.Add(root);

        return ordered;
    }

    /// <summary>单条候选的优先级分值（只在内部排序用，数值本身没有对外含义）。</summary>
    private static int Score(FaviconCandidate candidate)
    {
        var sizes = (candidate.Sizes ?? string.Empty).ToLowerInvariant();
        var type = (candidate.Type ?? string.Empty).ToLowerInvariant();

        var svg = type.Contains("svg", StringComparison.Ordinal)
                  || sizes.Contains("any", StringComparison.Ordinal)
                  || candidate.Url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        if (svg) return 60;

        var declared = LargestSize(sizes);
        if (declared is >= 16 and <= 64) return 100 + declared;
        if (declared > 64) return 90;
        if (candidate.Url.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return 80;

        return 70;
    }

    /// <summary>从 <c>sizes</c> 里取最大的那条边长（形如 <c>16x16 32x32</c>；没有则 0）。</summary>
    private static int LargestSize(string sizes)
    {
        var largest = 0;
        foreach (var token in sizes.Split(' ', ',', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = token.IndexOf('x', StringComparison.Ordinal);
            var number = x > 0 ? token[..x] : token;
            if (int.TryParse(number, out var value) && value > largest) largest = value;
        }

        return largest;
    }

    private static string? BuildRootFallback(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        return new Uri(uri, "/favicon.ico").AbsoluteUri;
    }
}

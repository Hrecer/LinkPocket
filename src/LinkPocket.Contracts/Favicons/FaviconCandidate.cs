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
/// <b>排序口径照抄 Chromium</b>（<c>favicon_handler.cc</c> 的 <c>FaviconCandidate::FromFaviconURL</c> +
/// <c>select_favicon_frames.cc</c> 的 <c>GetCandidateIndexWithBestScore</c>）：
/// ① 声明了标签页尺寸（16–64）的位图 —— 精确命中，最高；
/// ② <c>.ico</c> —— 多尺寸容器，能直接供 16/32（实测浏览器标签页用的常是它）；
/// ③ 矢量（SVG / <c>sizes="any"</c>）与没写 sizes 的位图 —— 源码里都是满分 1.0
/// （"桌面端：无尺寸信息的往高了算"）；
/// ④ 声明了更大但 &lt; 8×16=128 的位图；
/// ⑤ <b>声明 ≥ 128 的位图垫底</b> —— 源码原话："大尺寸的 favicon 位图往往有完全不同的视觉风格，
/// 要避开它们"（<c>kHugeEdgeSize = desired_size * 8</c>）。
/// 实测某站同时声明 512 的 SVG 占位、192 的方形图与 <c>.ico</c>，浏览器标签页用的是 <c>.ico</c> ——
/// 与这条规则一致。同分保持声明顺序。
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

        var declared = LargestSize(sizes);

        if (!svg && declared is >= 16 and <= 64) return 120 + declared;   // 精确命中标签页尺寸
        if (candidate.Url.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return 110;

        if (svg || declared == 0) return 100;                             // "any" / 没写 sizes：源码给满分
        if (declared >= 128) return 40;                                   // kHugeEdgeSize：刻意避开的大图
        if (declared > 64) return 90;

        return 80;
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

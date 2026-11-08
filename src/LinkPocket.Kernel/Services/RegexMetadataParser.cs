using System.Net;
using System.Text.RegularExpressions;
using LinkPocket.Contracts;

namespace LinkPocket.Kernel;

/// <summary>
/// 页面元数据解析（自 LinkService 正则组下沉为 L0 纯函数）：
/// HTML → 标题 / 描述 / favicon 地址。解析规则与既有实现逐条等价（行为等价项）：
/// <list type="bullet">
/// <item>标题：&lt;title&gt;，被 og:title / twitter:title 覆盖（**空白按 HTML 口径折叠**，跨行书写的标题不留换行）；**&lt;title&gt; 为空时依次回落
/// og:site_name / application-name / apple-mobile-web-app-title / 页面描述**——SPA 常把 &lt;title&gt;
/// 留给前端脚本稍后写入，HTML 里只有站点名或描述，不能因此返回空标题；</item>
/// <item>描述：meta[name=description]（name/content 两种属性序），被 og:description 覆盖；</item>
/// <item>favicon：link[rel=icon]（含 shortcut icon；**属性值可加引号也可裸写**，且**属性顺序任意**——
/// 相对地址按页面根解析）；**声明即采用**（地址可以没有图标扩展名，如头像 / 图片服务的无扩展名 URL），
/// 只有完全没有声明时才回落 {host}/favicon.ico。</item>
/// </list>
/// 网络抓取不在本契约（模块层闸外执行）；本类型只做纯解析、可快照单测。
/// </summary>
public sealed class RegexMetadataParser : IMetadataParser
{
    public static readonly RegexMetadataParser Instance = new();

    public PageMetadata Parse(string html, Uri pageUri)
    {
        var title = ParseTitle(html);
        var description = ParseDescription(html);
        var faviconUrl = ParseFavicon(html, pageUri);
        return new PageMetadata(title, description, faviconUrl);
    }

    private static string? ParseTitle(string html)
    {
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()) : null;
        if (string.IsNullOrEmpty(title)) title = null;

        // og:title / twitter:title 覆盖（twitter:title 也常用 name= 书写，property / name 两种都认）
        var ogTitle = FirstMetaContentAnyOrder(html, "og:title")
                      ?? FirstMetaContentAnyOrder(html, "twitter:title", isName: true);
        if (!string.IsNullOrEmpty(ogTitle)) return CollapseWhitespace(ogTitle);

        return CollapseWhitespace(title ?? SiteNameOrDescription(html));
    }

    /// <summary>
    /// 标题里的空白按 HTML 口径折叠：换行 / 制表 / 连续空格压成一个空格。
    /// 标题元素允许跨行书写（ziglang.org 即此例），原样返回会在界面里画出换行与缩进。
    /// 只折叠 ASCII 空白，<c>&amp;nbsp;</c> 等有意留白保持不动。
    /// </summary>
    private static string? CollapseWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, @"[ \t\r\n\f]+", " ").Trim();

    /// <summary>
    /// 标题缺位时的回落：站点名优先，其次页面描述。
    /// SPA（如 work.n.cn）把 &lt;title&gt; 留给前端脚本写入，HTML 里只有站点名/描述是唯一可用文字。
    /// </summary>
    private static string? SiteNameOrDescription(string html)
        => FirstMetaContentAnyOrder(html, "og:site_name")
           ?? FirstMetaContentAnyOrder(html, "application-name", isName: true)
           ?? FirstMetaContentAnyOrder(html, "apple-mobile-web-app-title", isName: true)
           ?? ParseDescription(html);

    private static string? ParseDescription(string html)
    {
        var desc = FirstMetaContent(html, "description", isName: true)
                   ?? FirstMetaContent(html, "description", isName: false, contentFirst: true);
        var ogDesc = FirstMetaContent(html, "og:description");
        return !string.IsNullOrEmpty(ogDesc) ? ogDesc : desc;
    }

    /// <summary>读 meta 标签内容值。兼容 property/name 前置与 content 前置两种属性序。</summary>
    private static string? FirstMetaContent(string html, string key, bool isName = false, bool contentFirst = false)
    {
        var kind = isName ? "name" : "property";
        var pattern = contentFirst
            ? $@"<meta\s+[^>]*content=[""'](.*?)[""'][^>]*(?:{kind})=[""']{key}[""']"
            : $@"<meta\s+[^>]*(?:{kind})=[""']{key}[""'][^>]*content=[""'](.*?)[""']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        return value.Length == 0 ? null : value;
    }

    /// <summary>读 meta 标签内容值：键与 content 两种属性顺序都认。</summary>
    private static string? FirstMetaContentAnyOrder(string html, string key, bool isName = false)
        => FirstMetaContent(html, key, isName)
           ?? FirstMetaContent(html, key, isName, contentFirst: true);

    private static readonly Regex LinkTagPattern = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase);

    private static string? ParseFavicon(string html, Uri pageUri)
    {
        var baseUrl = $"{pageUri.Scheme}://{pageUri.Host}";

        var faviconPath = FindIconHref(html, pageUri);
        if (!string.IsNullOrWhiteSpace(faviconPath))
        {
            // 相对/根路径按页面 URL 解析（HTML 规范）：子目录页面的相对 favicon 落在该目录下，而非站点根
            var resolvedUrl = faviconPath switch
            {
                _ when faviconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || faviconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => faviconPath,
                _ when faviconPath.StartsWith("//") => $"https:{faviconPath}",
                _ when faviconPath.StartsWith("/") => new Uri(pageUri, faviconPath).ToString(),
                _ => new Uri(pageUri, faviconPath).ToString(),
            };

            // 声明即采用：浏览器不会因为地址没有 .ico/.png 就忽略它。图片服务的头像地址
            // （如 .../avatar/<hash>?d=blank）常无扩展名，按扩展名白名单筛会误回落站点根，
            // 而多数站点根 /favicon.ico 是 404（blog.siitake.cn 即此例）。
            return resolvedUrl;
        }

        return $"{baseUrl}/favicon.ico";
    }

    /// <summary>
    /// 从 &lt;link&gt; 标签里取出图标地址。
    /// </summary>
    /// <remarks>
    /// 两条硬性兼容（曾导致真实站点解析不出图标）：
    /// <list type="number">
    /// <item><b>属性可以不加引号</b>：HTML5 允许裸属性值，真实站点大量这么写
    /// （如 <c>&lt;link rel=icon href=//assets.cdn/x/favicon.png&gt;</c>，chat.qwen.ai 即是）；
    /// 旧实现要求值必须被引号包住，于是整站回落到站点根 <c>/favicon.ico</c>（多数站点该路径 404）。</item>
    /// <item><b>属性顺序任意</b>：<c>href</c> 写在 <c>rel</c> 前面同样是合法 HTML，
    /// 旧实现的正则强制 <c>rel</c> 在前。</item>
    /// </list>
    /// 判据：rel 含 icon 且不是 apple-touch-icon；优先标准形态（<c>rel=icon</c> / <c>rel="shortcut icon"</c>），
    /// 其余含 icon 的写法（<c>mask-icon</c> / <c>fluid-icon</c>）仅在没有标准形态时兜底。
    /// </remarks>
    private static string? FindIconHref(string html, Uri pageUri)
    {
        var standard = new List<FaviconCandidate>();
        var loose = new List<FaviconCandidate>();

        foreach (Match link in LinkTagPattern.Matches(html))
        {
            var tag = link.Value;
            var rel = AttributeValue(tag, "rel");
            if (string.IsNullOrEmpty(rel) || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;

            // 触屏图标（apple-touch-icon）不是标签页图标，也不参与这里的选择（既有约定，见 MetadataParseTests）
            if (rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) continue;

            var href = AttributeValue(tag, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var candidate = new FaviconCandidate(
                Resolve(href, pageUri),
                AttributeValue(tag, "type"),
                AttributeValue(tag, "sizes"));

            // rel=icon / rel="shortcut icon" 是标准形态；mask-icon、apple-touch-icon 之类只在没有标准形态时兜底
            var tokens = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (Array.Exists(tokens, t => t.Equals("icon", StringComparison.OrdinalIgnoreCase)))
                standard.Add(candidate);
            else
                loose.Add(candidate);
        }

        // 标准形态里按浏览器口径排序（标签页尺寸的位图优先于矢量占位图），同分保持文档顺序
        var ranked = FaviconCandidatePicker.Rank(standard);
        if (ranked.Count > 0) return ranked[0];

        var fallback = FaviconCandidatePicker.Rank(loose);
        return fallback.Count > 0 ? fallback[0] : null;
    }

    /// <summary>把声明里的地址解析成绝对地址（HTML 规范：相对地址按页面 URL 解析）。</summary>
    private static string Resolve(string href, Uri pageUri) => href switch
    {
        _ when href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => href,
        _ when href.StartsWith("//") => $"https:{href}",
        _ => new Uri(pageUri, href).ToString(),
    };

    /// <summary>读标签属性值：双引号 / 单引号 / **裸值**（HTML5 无引号）三种都认；与属性顺序无关。</summary>
    private static string? AttributeValue(string tag, string name)
    {
        var match = Regex.Match(tag,
            $@"(?<![-\w]){name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (match.Groups[1].Success) return match.Groups[1].Value;
        if (match.Groups[2].Success) return match.Groups[2].Value;
        return match.Groups[3].Value;
    }
}

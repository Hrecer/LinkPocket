using System.Net;
using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// 页面元数据解析（自 LinkService 正则组下沉为 L0 纯函数）：
/// HTML → 标题 / 描述 / favicon 地址。解析规则与既有实现逐条等价（行为等价项）：
/// <list type="bullet">
/// <item>标题：&lt;title&gt;，被 og:title / twitter:title 覆盖；</item>
/// <item>描述：meta[name=description]（name/content 两种属性序），被 og:description 覆盖；</item>
/// <item>favicon：link[rel=icon]（含 shortcut icon；**属性值可加引号也可裸写**，且**属性顺序任意**——
/// 相对地址按页面根解析）；解析出的地址不含常见图标扩展名时回落 {host}/favicon.ico；无 link 标签同样回落。</item>
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

        // og:title / twitter:title 覆盖（与既有四种属性序尝试的最终效果等价）
        var ogTitle = FirstMetaContent(html, "(?:og:title|twitter:title)");
        if (!string.IsNullOrEmpty(ogTitle)) title = ogTitle;

        return title;
    }

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

    private static readonly Regex LinkTagPattern = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase);

    private static string? ParseFavicon(string html, Uri pageUri)
    {
        var baseUrl = $"{pageUri.Scheme}://{pageUri.Host}";

        var faviconPath = FindIconHref(html);
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

            var lowerUrl = resolvedUrl.ToLower();
            if (lowerUrl.Contains("favicon") || lowerUrl.Contains(".ico") || lowerUrl.Contains(".png") ||
                lowerUrl.Contains(".jpg") || lowerUrl.Contains(".jpeg") || lowerUrl.Contains(".gif") ||
                lowerUrl.Contains(".svg") || lowerUrl.Contains(".webp"))
            {
                return resolvedUrl;
            }

            return $"{baseUrl}/favicon.ico";
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
    private static string? FindIconHref(string html)
    {
        string? fallback = null;
        foreach (Match link in LinkTagPattern.Matches(html))
        {
            var tag = link.Value;
            var rel = AttributeValue(tag, "rel");
            if (string.IsNullOrEmpty(rel) || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) continue;

            var href = AttributeValue(tag, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var tokens = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (Array.Exists(tokens, t => t.Equals("icon", StringComparison.OrdinalIgnoreCase)))
                return href;          // rel=icon / rel="shortcut icon"：最标准，命中即用
            fallback ??= href;        // mask-icon / fluid-icon 之类：仅作兜底
        }
        return fallback;
    }

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

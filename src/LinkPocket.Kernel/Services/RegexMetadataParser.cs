using System.Net;
using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// 页面元数据解析（方案 4.1，自 LinkService 正则组下沉为 L0 纯函数）：
/// HTML → 标题 / 描述 / favicon 地址。解析规则与既有实现逐条等价（行为等价项）：
/// <list type="bullet">
/// <item>标题：&lt;title&gt;，被 og:title / twitter:title 覆盖；</item>
/// <item>描述：meta[name=description]（name/content 两种属性序），被 og:description 覆盖；</item>
/// <item>favicon：link[rel=icon]（含 shortcut icon），相对地址按页面根解析；
/// 解析出的地址不含常见图标扩展名时回落 {host}/favicon.ico；无 link 标签同样回落。</item>
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

    private static string? ParseFavicon(string html, Uri pageUri)
    {
        var baseUrl = $"{pageUri.Scheme}://{pageUri.Host}";

        var faviconMatch = Regex.Match(html,
            @"<link\s+[^>]*rel=[""'](?:shortcut\s+icon|icon)[""'][^>]*href=[""'](.*?)[""']",
            RegexOptions.IgnoreCase);
        if (!faviconMatch.Success)
        {
            faviconMatch = Regex.Match(html,
                @"<link\s+[^>]*rel=[""'](?!apple-touch-icon)[^""']*icon[^""']*[""'][^>]*href=[""'](.*?)[""']",
                RegexOptions.IgnoreCase);
        }

        if (faviconMatch.Success)
        {
            var faviconPath = faviconMatch.Groups[1].Value;
            // 相对/根路径按页面 URL 解析（HTML 规范）：子目录页面的相对 favicon 落在该目录下，而非站点根
            var resolvedUrl = faviconPath switch
            {
                _ when faviconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || faviconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => faviconPath,
                _ when faviconPath.StartsWith("//") => $"https:{faviconPath}",
                _ when faviconPath.StartsWith("/") => new Uri(pageUri, faviconPath).ToString(),
                _ => string.IsNullOrWhiteSpace(faviconPath)
                    ? $"{baseUrl}/favicon.ico"
                    : new Uri(pageUri, faviconPath).ToString(),
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
}

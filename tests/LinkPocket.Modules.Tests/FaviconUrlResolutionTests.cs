using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 图标地址的解析与缓存命名：**SVG 不再被降级成站点根**，缓存位子上落的也必须是能解码的位图。
/// <para>
/// 为什么钉这两条：现代文档站/组件库站常常只声明一个 <c>type="image/svg+xml"</c> 的图标，
/// 而站点根 <c>/favicon.ico</c> 是 404。旧口径「SVG 一律回落站点根」在那种站上等于**把图标丢掉**；
/// 新口径保留 SVG 地址、由宿主用浏览器内核画成 PNG，所以缓存文件名的扩展名必须跟着内容走（<c>.png</c>），
/// 否则读方按 <c>.svg</c> 找一个 PNG 会落空。
/// </para>
/// </summary>
public class FaviconUrlResolutionTests
{
    [Fact]
    public void SVG图标_保留原地址_不降级成站点根()
    {
        const string svg = "https://example.com/assets/logo-abc123.svg";

        Assert.Equal(svg, FaviconCache.ResolveFaviconUrl(svg));
    }

    [Fact]
    public void SVG图标_缓存文件名按PNG记名()
    {
        var path = FaviconCache.GetCacheFilePath("https://example.com/assets/logo-abc123.svg");

        Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 普通图标_扩展名原样保留()
    {
        Assert.EndsWith(".ico", FaviconCache.GetCacheFilePath("https://example.com/favicon.ico"), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", FaviconCache.GetCacheFilePath("https://example.com/i.png"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 内嵌图标_原样返回且不参与缓存()
    {
        const string inline = "data:image/png;base64,iVBORw0KGgo=";

        Assert.Equal(inline, FaviconCache.ResolveFaviconUrl(inline));
        Assert.True(FaviconCache.IsInlineData(inline));
    }

    [Fact]
    public void 空地址_返回空串()
    {
        Assert.Equal(string.Empty, FaviconCache.ResolveFaviconUrl(null));
        Assert.Equal(string.Empty, FaviconCache.ResolveFaviconUrl("   "));
    }
}

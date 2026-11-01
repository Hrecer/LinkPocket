using LinkPocket.Contracts;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 图标候选的挑选口径：**不是"取第一个声明"**，而是按浏览器标签页实际用的那张来排。
/// </summary>
/// <remarks>
/// 事故背景：某站同时声明了 <c>favicon.svg</c>（内容是一个纯圆占位）与 <c>favicon.ico</c>（真正的品牌标志），
/// 按声明顺序取会把那个圆当成站点图标；而浏览器自己存的是 <c>.ico</c> 的 16/32 位图。
/// </remarks>
public class FaviconCandidateTests
{
    private static string? FaviconOf(string html, string pageUrl = "https://example.com/some/page")
        => RegexMetadataParser.Instance.Parse(html, new Uri(pageUrl)).FaviconUrl;

    [Fact]
    public void 同时声明矢量与标签页位图时_位图优先()
    {
        var html = """
            <head>
              <link rel="icon" type="image/svg+xml" href="/images/favicon.svg">
              <link rel="icon" sizes="32x32" href="/images/favicon.ico">
            </head>
            """;

        Assert.Equal("https://example.com/images/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 未声明尺寸的ico也排在矢量之前()
    {
        var html = """
            <link rel="icon" type="image/svg+xml" href="/a.svg">
            <link rel="icon" href="/favicon.ico">
            """;

        Assert.Equal("https://example.com/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 只有矢量声明时_用矢量而不是站点根()
    {
        var html = """<link rel="icon" type="image/svg+xml" href="/assets/logo.svg">""";

        // 站点根的 /favicon.ico 只有在"没有任何可用声明"时才作为兜底，
        // 所以这里必须是声明的那张：很多站点的根目录根本没有这个文件（实测 404）
        Assert.Contains("/assets/logo.svg", FaviconOf(html));
    }

    [Fact]
    public void 大图不压过ico_浏览器标签页用的是ico()
    {
        // 同一站点的实测：同时声明 svg（纯圆占位）、192×192 方形图 与 .ico（真正的标志），
        // 浏览器图标库里存的是 .ico 的 16/32 —— 所以 192 的大图不能排在 .ico 前面
        var html = """
            <link rel="icon" type="image/svg+xml" href="/images/favicon.svg">
            <link rel="icon" type="image/png" sizes="192x192" href="/images/android-chrome-192x192.png">
            <link rel="icon" href="/images/favicon.ico">
            """;

        Assert.Equal("https://example.com/images/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 超大声明垫底_与源码的kHugeEdgeSize一致()
    {
        // select_favicon_frames.cc：大尺寸位图"往往有完全不同的视觉风格"，刻意避开（kHugeEdgeSize = 期望*8）
        var html = """
            <link rel="icon" sizes="512x512" href="/icon-512.png">
            <link rel="icon" href="/plain.png">
            """;

        Assert.Equal("https://example.com/plain.png", FaviconOf(html));
    }

    [Fact]
    public void 同为标签页尺寸时取声明更大的那条()
    {
        var html = """
            <link rel="icon" sizes="16x16" href="/icon-16.png">
            <link rel="icon" sizes="48x48" href="/icon-48.png">
            """;

        Assert.Equal("https://example.com/icon-48.png", FaviconOf(html));
    }

    [Fact]
    public void 触屏图标不算标签页图标()
    {
        var html = """<link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">""";

        Assert.Equal("https://example.com/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 空占位与重复声明都被跳过()
    {
        var html = """
            <link rel="icon" href="data:image/x-icon;">
            <link rel="icon" sizes="32x32" href="/icon-32.png">
            <link rel="icon" sizes="32x32" href="/icon-32.png">
            """;

        Assert.Equal("https://example.com/icon-32.png", FaviconOf(html));
    }

    [Fact]
    public void 排序结果末尾补站点根兜底_且去重()
    {
        var ranked = FaviconCandidatePicker.Rank(
            [new FaviconCandidate("https://example.com/favicon.ico", "image/x-icon", "16x16")],
            "https://example.com/page");

        Assert.Equal(["https://example.com/favicon.ico"], ranked);
    }

    [Fact]
    public void 没有任何声明时_排行只剩站点根兜底()
    {
        var ranked = FaviconCandidatePicker.Rank([], "https://example.com/deep/page");

        Assert.Equal(["https://example.com/favicon.ico"], ranked);
    }

    [Fact]
    public void 非http页面的排行不补站点根()
    {
        var ranked = FaviconCandidatePicker.Rank([], "about:blank");

        Assert.Empty(ranked);
    }
}

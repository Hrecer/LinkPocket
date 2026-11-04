using System.Text;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 图标落盘的两种输入：位图直接存；**SVG 先交给宿主光栅化**再存（WPF 解不了 SVG）。
/// <para>
/// 这条钉的是真实故障：反爬站点只给 SVG 图标，页面内取回来后因为"不是位图"被丢弃，
/// 界面上就没有图标；而普通 HTTP 又拿不到那张图（被一起拦）。
/// </para>
/// </summary>
[Collection("favicon-cache")]
public class FaviconStoreTests
{
    /// <summary>1×1 透明 PNG（最小合法位图）。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static string UniqueUrl(string ext)
        => $"https://example.com/icon-{Guid.NewGuid():N}{ext}";

    [Fact]
    public async Task 位图字节_直接入库()
    {
        var url = UniqueUrl(".png");
        Assert.True(await FaviconCache.StoreBytesAsync(url, OnePixelPng, CancellationToken.None));

        var path = FaviconCache.TryGetCacheFilePath(url);
        Assert.NotNull(path);
        Assert.Equal(OnePixelPng, await File.ReadAllBytesAsync(path!));
    }

    [Fact]
    public async Task SVG字节_先光栅化再入库()
    {
        var url = UniqueUrl(".svg");
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"></svg>");
        var rasterized = 0;

        FaviconCache.SvgRasterizer = (_, _) =>
        {
            rasterized++;
            return Task.FromResult<byte[]?>(OnePixelPng);
        };

        try
        {
            Assert.True(await FaviconCache.StoreBytesAsync(url, svg, CancellationToken.None));
            Assert.Equal(1, rasterized);   // 只光栅化一次

            // SVG 位子上落的是 PNG：文件名扩展名跟着内容走
            var path = FaviconCache.TryGetCacheFilePath(url);
            Assert.NotNull(path);
            Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(OnePixelPng, await File.ReadAllBytesAsync(path!));
        }
        finally
        {
            FaviconCache.SvgRasterizer = null;
        }
    }

    [Fact]
    public async Task SVG字节_无光栅化器时先按需装配()
    {
        var url = UniqueUrl(".svg");
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"></svg>");
        var rasterized = 0;

        PageRenderHook.Renderer = null;
        PageRenderHook.InteractiveRenderer = null;
        FaviconCache.SvgRasterizer = null;
        PageRenderHook.EnsureProvider = _ =>
        {
            FaviconCache.SvgRasterizer = (_, _) =>
            {
                rasterized++;
                return Task.FromResult<byte[]?>(OnePixelPng);
            };
            return Task.FromResult(true);
        };

        try
        {
            Assert.True(await FaviconCache.StoreBytesAsync(url, svg, CancellationToken.None));
            Assert.Equal(1, rasterized);

            var path = FaviconCache.TryGetCacheFilePath(url);
            Assert.NotNull(path);
            Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(OnePixelPng, await File.ReadAllBytesAsync(path!));
        }
        finally
        {
            PageRenderHook.EnsureProvider = null;
            FaviconCache.SvgRasterizer = null;
        }
    }

    [Fact]
    public async Task 非图片字节_一律不入库()
    {
        var url = UniqueUrl(".png");
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><body>403 challenge</body></html>");

        Assert.False(await FaviconCache.StoreBytesAsync(url, html, CancellationToken.None));
        Assert.Null(FaviconCache.TryGetCacheFilePath(url));
    }

    [Fact]
    public async Task 光栅化失败_不入库()
    {
        var url = UniqueUrl(".svg");
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        FaviconCache.SvgRasterizer = (_, _) => Task.FromResult<byte[]?>(null);
        try
        {
            Assert.False(await FaviconCache.StoreBytesAsync(url, svg, CancellationToken.None));
            Assert.Null(FaviconCache.TryGetCacheFilePath(url));
        }
        finally
        {
            FaviconCache.SvgRasterizer = null;
        }
    }
}

using LinkPocket.Contracts;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 元数据抓取的浏览器渲染兜底：**能否兜底由判据决定，不由"渲染器在不在"决定**。
/// <para>
/// 这两条钉的是安全边界，不是功能：渲染器是一个真浏览器，它会把请求打到目标地址上，
/// 所以"环回 / 链路本地拒绝"必须在进渲染之前再判一次，否则兜底就成了绕过 SSRF 守护的后门。
/// 判据（渲染器**一次都没被调用**）比"命令抛没抛异常"更硬——抛异常也可能是别的分支给的。
/// </para>
/// <para>
/// 兜底成功的正向路径需要真实网络（要一个"对脚本客户端回 403、对浏览器放行"的目标），
/// 由交付前的端到端手验承担，这里不造网络依赖。
/// </para>
/// </summary>
public class MetadataRenderFallbackTests
{
    [Fact]
    public async Task 环回地址_渲染兜底不放行_渲染器一次都不调()
    {
        var (engine, _, _) = TestHost.Create();
        var calls = 0;
        try
        {
            PageRenderHook.Renderer = (_, _) =>
            {
                calls++;
                return Task.FromResult<RenderedPageMetadata?>(new RenderedPageMetadata("不该出现的标题", null));
            };

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<MetadataDto>("links.metadata_fetch", new { url = "http://127.0.0.1:9/" }));

            Assert.Equal(EngineErrors.NetworkError, ex.Error.Code);
            Assert.Equal(0, calls);
        }
        finally
        {
            PageRenderHook.Renderer = null;
        }
    }

    [Fact]
    public async Task 非http地址_按非法地址拒绝_不走渲染兜底()
    {
        var (engine, _, _) = TestHost.Create();
        var calls = 0;
        try
        {
            PageRenderHook.Renderer = (_, _) =>
            {
                calls++;
                return Task.FromResult<RenderedPageMetadata?>(new RenderedPageMetadata("不该出现的标题", null));
            };

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<MetadataDto>("links.metadata_fetch", new { url = "file:///c:/windows/win.ini" }));

            Assert.Equal(EngineErrors.InvalidUrl, ex.Error.Code);   // 判据拒绝 ≠ 网络拒绝：换谁来访问都不该放行
            Assert.Equal(0, calls);
        }
        finally
        {
            PageRenderHook.Renderer = null;
        }
    }
}

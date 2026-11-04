using LinkPocket.Contracts;
using System.Net;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 批量解析与图标抓取的外部口径：**逐项结果、逐项原因、单项失败不影响整批**。
/// 这里不造真实网络（成功路径由交付前手验），只钉住"批处理不会被一条坏地址打断"这条硬约束。
/// </summary>
public class MetadataBatchAndFaviconFetchTests
{
    [Fact]
    public async Task 批量元数据_单项失败不影响整批并逐项报错()
    {
        var (engine, _, _) = TestHost.Create();
        var batch = await engine.QueryAsync<MetadataBatchDto>("links.metadata_fetch_batch", new
        {
            urls = new[] { "http://127.0.0.1:9/", "file:///c:/windows/win.ini" },
            concurrency = 2,
            render = false,
        });

        Assert.Equal(2, batch.Items.Count);
        Assert.Equal(2, batch.Summary.Total);
        Assert.Equal(0, batch.Summary.Ok);
        Assert.Equal(2, batch.Summary.Failed);
        Assert.Equal(0, batch.Summary.NeedsHumanVerification);
        Assert.Contains(batch.Items, i => i.ErrorCode == EngineErrors.NetworkError);
        Assert.Contains(batch.Items, i => i.ErrorCode == EngineErrors.InvalidUrl);
    }

    [Fact]
    public async Task 批量元数据_重复地址只抓一次()
    {
        var (engine, _, _) = TestHost.Create();
        var batch = await engine.QueryAsync<MetadataBatchDto>("links.metadata_fetch_batch", new
        {
            urls = new[] { "http://127.0.0.1:9/", "http://127.0.0.1:9/" },
            render = false,
        });

        Assert.Single(batch.Items);
        Assert.Equal(1, batch.Summary.Total);
    }

    [Fact]
    public async Task 批量元数据_空列表硬失败()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<MetadataBatchDto>("links.metadata_fetch_batch", new { urls = Array.Empty<string>() }));
        Assert.Equal(EngineErrors.RequiredParam, ex.Error.Code);
    }

    [Fact]
    public async Task 解析命令_interactive类型错_按VAL002拒绝()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<MetadataDto>("links.metadata_fetch",
                new { url = "http://127.0.0.1:9/", interactive = "yes" }));
        Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public async Task 图标抓取_内嵌与非法地址逐项给出原因()
    {
        var (engine, _, _) = TestHost.Create();
        var batch = await engine.QueryAsync<FaviconFetchDto>("favicon.fetch", new
        {
            favicon_urls = new[]
            {
                "data:image/png;base64,AAAA",
                "not-a-url",
                "file:///c:/windows/win.ini",
            },
            timeout_ms = 1_000,
        });

        Assert.Equal(3, batch.Items.Count);
        Assert.All(batch.Items, i => Assert.False(i.Cached));
        Assert.Contains(batch.Items, i => i.Reason == FaviconFetchReasons.InlineData);
        Assert.Contains(batch.Items, i => i.Reason == FaviconFetchReasons.InvalidUrl);
        Assert.Equal(3, batch.Summary.Failed);
        Assert.Equal(0, batch.Summary.Cached);
    }

    [Fact]
    public void 目标判据_环回与链路本地拒绝_内网放行()
    {
        Assert.False(TargetGuard.IsAllowed(IPAddress.Loopback));
        Assert.False(TargetGuard.IsAllowed(IPAddress.Parse("169.254.169.254")));
        Assert.True(TargetGuard.IsAllowed(IPAddress.Parse("192.168.1.5")));   // NAS / 路由器图标照常抓
        Assert.True(TargetGuard.IsAllowed(IPAddress.Parse("203.0.113.10")));
    }

    [Fact]
    public async Task 图标抓取_重复地址只算一次()
    {
        var (engine, _, _) = TestHost.Create();
        var batch = await engine.QueryAsync<FaviconFetchDto>("favicon.fetch", new
        {
            favicon_urls = new[] { "not-a-url", "not-a-url" },
        });

        Assert.Single(batch.Items);
        Assert.Equal(1, batch.Summary.Total);
    }

    [Fact]
    public async Task 图标抓取_空列表硬失败()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<FaviconFetchDto>("favicon.fetch", new { favicon_urls = Array.Empty<string>() }));
        Assert.Equal(EngineErrors.RequiredParam, ex.Error.Code);
    }

    [Fact]
    public void 能力提供者锁_同库同名_不同库隔离()
    {
        var a = HostCapabilityBridge.ProviderMutexNameFor(@"C:\data\linkpocket.db");
        var b = HostCapabilityBridge.ProviderMutexNameFor(@"c:\DATA\LINKPOCKET.DB");
        var c = HostCapabilityBridge.ProviderMutexNameFor(@"C:\data\other.db");

        Assert.StartsWith(@"Local\", a);
        Assert.Equal(a, b);        // 路径大小写不影响同一把锁
        Assert.NotEqual(a, c);     // 不同库互不占用
    }
}

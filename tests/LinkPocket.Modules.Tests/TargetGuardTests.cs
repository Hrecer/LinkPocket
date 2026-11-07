using System.Net;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>连接前判据：环回/链路本地默认拒绝；仅 HTTPS 域名解析到本机回环时例外（证书仍校验）。</summary>
public class TargetGuardTests
{
    [Fact]
    public void 只有HTTPS域名才允许本机映射()
    {
        Assert.True(TargetGuard.AllowsLocalMapping(new Uri("https://example.com/")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("http://example.com/")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://127.0.0.1/")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://127.0.0.1./")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://[::1]/")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://localhost/")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://localhost./")));
        Assert.False(TargetGuard.AllowsLocalMapping(new Uri("https://dev.localhost/")));
    }

    [Fact]
    public void 解析结果必须全部为本机回环才算本机映射()
    {
        Assert.True(TargetGuard.IsLoopbackOnly(new[] { IPAddress.Loopback }));
        Assert.False(TargetGuard.IsLoopbackOnly(Array.Empty<IPAddress>()));
        Assert.False(TargetGuard.IsLoopbackOnly(new[] { IPAddress.Loopback, IPAddress.Parse("203.0.113.10") }));
        Assert.False(TargetGuard.IsLoopbackOnly(new[] { IPAddress.Parse("169.254.169.254") }));
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LinkPocket.Contracts;

/// <summary>
/// 抓取目标的连接前判据（**唯一实现**）：只挡环回与链路本地（含云环境元数据端点 169.254.169.254），
/// 内网地址（10/172.16/192.168）照常放行——书签管理器本来就会收藏自家路由器、NAS、内部站点。
/// <para>**唯一例外**：HTTPS 域名解析结果全部为本机回环时放行（证书仍按原域名校验；
/// 字面 IP、localhost 与 HTTP 不使用该例外）。本机映射环境据此可用，同时不是访问本机服务的通用后门。</para>
/// <para>元数据抓取、favicon 下载、桌面能力桥（外部宿主的 render / verify / rasterize）共用这一份；
/// 任何一条新网络入口都必须先过这里，别让兜底路径成为访问本机服务的后门。</para>
/// </summary>
public static class TargetGuard
{
    /// <summary>系统代理地址；<c>null</c> = 未配代理。与元数据抓取同口径：配了代理时目标解析交给代理，
    /// 本机不做 DNS 预判（否则代理唯一出口的环境会被误拒）。</summary>
    private static readonly Uri? SystemProxy = ResolveSystemProxy();

    private static Uri? ResolveSystemProxy()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy is null) return null;
            var probe = new Uri("http://linkpocket-guard-probe.invalid/");
            var resolved = proxy.GetProxy(probe);
            return resolved is not null && resolved.Host != probe.Host ? resolved : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsAllowed(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast) return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // 169.254.0.0/16（链路本地：含 169.254.169.254 元数据端点）、224.0.0.0/4（组播）
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] >= 224) return false;
        }

        return true;
    }

    /// <summary>解析目标主机并要求存在至少一个放行地址；字面 IP 直接判定。</summary>
    public static async Task<bool> IsAllowedAsync(Uri target, CancellationToken ct)
    {
        if (SystemProxy is not null) return true;   // 目标解析交给代理，与 MetadataTransport 的 toProxy 口径一致
        if (IPAddress.TryParse(NormalizeHost(target.Host), out var literal)) return IsAllowed(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
            return Array.Find(addresses, IsAllowed) is not null
                   || AllowsLocalMapping(target) && IsLoopbackOnly(addresses);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;   // 解析不了 = 不能证明目标是安全的，按拒绝处理
        }
    }

    /// <summary>
    /// HTTPS 域名是否允许本机映射：字面 IP、localhost 与 HTTP 永不使用；
    /// 证书校验仍按原域名由传输层完成。
    /// </summary>
    public static bool AllowsLocalMapping(Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttps) return false;

        var host = NormalizeHost(target.Host);
        return !IPAddress.TryParse(host, out _) && !IsLocalhostName(host);
    }

    /// <summary>解析结果是否全部为本机回环（空集合不算）。</summary>
    public static bool IsLoopbackOnly(IReadOnlyList<IPAddress> addresses)
        => addresses.Count > 0 && addresses.All(IPAddress.IsLoopback);

    /// <summary>去掉 IPv6 方括号与末尾的点，按主机名本身判定。</summary>
    private static string NormalizeHost(string host)
    {
        if (host.Length > 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        return host.EndsWith('.') ? host[..^1] : host;
    }

    private static bool IsLocalhostName(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

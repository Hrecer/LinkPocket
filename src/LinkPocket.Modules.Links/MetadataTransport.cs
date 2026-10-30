using System.Net;
using System.Net.Sockets;
using System.Text;
using CurlImpersonate.Enums;
using CurlImpersonate.Http;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 元数据抓取的<b>网络层</b>（internal）：HttpClient 池化 + 10s 超时 + 手动跟随重定向（每跳校验）+ 浏览器 UA；
/// 外加两条兜底通道——<b>浏览器指纹</b>（curl-impersonate，过 TLS/HTTP2 指纹墙）与<b>浏览器内核渲染</b>
/// （宿主的 <see cref="PageRenderHook"/>，过 JS 人机校验墙）。编排（先试谁、何时算成功）在
/// <see cref="MetadataFetcher"/>；本类型只负责"把字节/HTML 取回来"。
/// </summary>
/// <remarks>
/// <para>
/// <b>抓取是"替用户访问一个地址"</b>，所以每跳都要过同一套判据：只许 http/https（挡 <c>file://</c> 这类
/// 本地协议）、连接前把目标地址解出来检查（挡**环回**与**链路本地** —— 前者是本机服务，后者含云环境的
/// 169.254.169.254 元数据端点）。<b>内网地址（10/172.16/192.168）照常放行</b>：书签管理器本来就会收藏
/// 自家路由器、NAS、内部站点，一刀切会把用户自己的页面挡在门外。
/// </para>
/// <para>
/// 正文有上限（只解析 &lt;head&gt; 元数据，2 MB 足够）：挡住"服务器把大文件或无限流当页面返回"。
/// </para>
/// </remarks>
internal static class MetadataTransport
{
    /// <summary>正文上限（字节）。</summary>
    internal const int MaxHtmlBytes = 2 * 1024 * 1024;

    /// <summary>重定向跳数上限（手动跟随：每跳都要重新校验）。</summary>
    private const int MaxRedirects = 5;

    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 反爬兜底客户端（curl-impersonate：真实浏览器 TLS/HTTP2 指纹），懒加载。
    /// <c>null</c> = 原生库初始化失败（提取/加载失败）——兜底缺位但主路径照常，抓取整体不因兜底陪葬。
    /// </summary>
    private static readonly Lazy<HttpClient?> ImpersonatingClient = new(CreateImpersonatingClient);

    /// <summary>
    /// 系统代理地址；<c>null</c> = 未配代理。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须单独认出来</b>：本机代理天然是 <c>127.0.0.1</c> / <c>localhost</c>，而下面
    /// <see cref="IsAllowed"/> 的 SSRF 守护又正是要挡环回地址 —— 两者撞在一起，结果就是
    /// <b>只要系统装了代理，元数据抓取就 100% 失败</b>（<c>refused to connect (loopback/link-local)</c>）。
    /// 浏览器走系统代理一切正常、工具却抓不到，就是这一条。
    /// </remarks>
    private static readonly Uri? SystemProxy = ResolveSystemProxy();

    private static Uri? ResolveSystemProxy()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy is null) return null;

            // 探测用一个不可能被直连的名字：有代理时返回代理地址，无代理时返回探测目标本身。
            var probe = new Uri("http://linkpocket-proxy-probe.invalid/");
            var resolved = proxy.GetProxy(probe);
            return resolved is not null && resolved.Host != probe.Host ? resolved : null;
        }
        catch
        {
            return null;   // 探测不出代理就当没代理：守护保持最严口径
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // 重定向由 DownloadAsync 手动跟随并逐跳校验
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectPublicAsync,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // 默认是 HTTP/1.1；真实浏览器走 h2。部分站点（Cloudflare 前置的）只对 h2 放行。
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        // 补齐浏览器常规请求头：只带 UA 的"裸请求"会被不少站点（含部分 CDN）直接判为机器人。
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        return client;
    }

    /// <summary>
    /// 指纹兜底：部分站点（阿里云 WAF / Cloudflare 前置）按客户端指纹拦截脚本类请求——
    /// 同为浏览器 UA + 完整请求头，Chrome 能过、.NET / curl 一律 503/403（火绒官网即此例，
    /// 实测 curl-impersonate 的 Chrome 指纹直接 200）。配置与主路径同强度：10s 超时、
    /// 不自动跟随重定向（仍由 <see cref="DownloadAsync"/> 逐跳校验）。
    /// </summary>
    private static HttpClient? CreateImpersonatingClient()
    {
        try
        {
            var options = new CurlHandlerOptions
            {
                // 固定为已实测通过的口径；跟进更新由升级 CurlImpersonate 包完成
                BrowserProfile = BrowserProfile.Chrome142,
                AllowAutoRedirect = false,
                FollowRedirects = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseBodyBytes = MaxHtmlBytes,
            };

            if (SystemProxy is { } proxy)
            {
                options.UseProxy = true;
                options.ProxyUri = proxy;
            }
            else
            {
                // 与主路径对齐：探测不到系统代理就直连，不引入第三套"环境变量代理"口径
                options.UseProxy = false;
            }

            return new HttpClient(new CurlHandler(options)) { Timeout = TimeSpan.FromSeconds(10) };
        }
        catch (Exception ex)
        {
            // 兜底原生资产缺失/加载失败：如实记录后降级为"无兜底"，主路径与整体抓取不受影响
            LpLog.Warn("curl-impersonate failed to initialize; metadata anti-bot fallback disabled",
                ex, category: "links.metadata");
            return null;
        }
    }

    /// <summary>URL 是否为合法的 http/https 绝对地址（LP.VAL.005 判定口径）。</summary>
    public static bool IsValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>抓取 HTML（逐跳校验重定向）；非 2xx / 超限 / 网络错误一律抛 <see cref="EngineException"/>。</summary>
    internal static async Task<(string Html, Uri FinalUri)> DownloadAsync(string url, CancellationToken ct)
    {
        var current = new Uri(url);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await SendAsync(current, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new EngineException(EngineErrors.Of(
                        EngineErrors.NetworkError, $"redirect without Location header: {current}"));

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsValidUrl(next.AbsoluteUri))
                {
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.InvalidUrl, $"redirect left http/https: {next.AbsoluteUri}"));
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.NetworkError,
                    $"page metadata fetch failed [{(int)response.StatusCode}]: {current}",
                    retryable: true));
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxHtmlBytes)
            {
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.NetworkError,
                    $"page body too large (declared {declared} bytes): {current}",
                    retryable: false));
            }

            var html = await ReadHtmlAsync(response.Content, ct).ConfigureAwait(false);
            return (html, current);
        }

        throw new EngineException(EngineErrors.Of(
            EngineErrors.NetworkError, $"too many redirects (>{MaxRedirects}): {url}", retryable: false));
    }

    /// <summary>
    /// 发一跳 GET。主路径被反爬墙拦下（403/503）时，同一地址改用浏览器指纹客户端重试<b>一次</b>——
    /// 其余响应（含重试后的 4xx/5xx）原样交给调用方按既有口径处理。
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(Uri target, CancellationToken ct)
    {
        var response = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!IsBotWall(response.StatusCode))
            return response;

        var fallback = ImpersonatingClient.Value;
        if (fallback is null)
            return response;   // 兜底不可用：维持主路径结果，按原口径如实报错

        var blocked = (int)response.StatusCode;
        response.Dispose();

        // 兜底与主路径同一套 SSRF 判据；配了系统代理时由代理解析（等同主路径 ConnectPublicAsync 的 toProxy 口径）
        if (SystemProxy is null)
            await EnsurePublicHostAsync(target, ct).ConfigureAwait(false);

        LpLog.Info($"metadata fetch blocked [{blocked}] (non-browser client?); retrying with browser fingerprint: {target.Host}",
            category: "links.metadata");

        return await fallback.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 同一地址改用浏览器指纹客户端再读一次 HTML；失败一律返回 <c>null</c>（兜底不成不影响主路径结论）。
    /// 与主路径同一套判据：连接前环回 / 链路本地校验、2 MB 上限、10 s 超时。
    /// </summary>
    internal static async Task<string?> TryGetImpersonatedHtmlAsync(Uri target, CancellationToken ct)
    {
        var fallback = ImpersonatingClient.Value;
        if (fallback is null) return null;

        try
        {
            // 配了系统代理时由代理解析（与 SendAsync 的 toProxy 口径一致）
            if (SystemProxy is null)
                await EnsurePublicHostAsync(target, ct).ConfigureAwait(false);

            using var response = await fallback
                .GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await ReadHtmlAsync(response.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LpLog.Warn($"browser fingerprint retry failed: {target.Host}", ex, category: "links.metadata");
            return null;
        }
    }

    /// <summary>
    /// favicon 下载的浏览器指纹兜底（装配给 <c>FaviconCache.ImpersonatedDownloader</c>，由 Links 模块在装配时接上）：
    /// 手动跟随重定向（每跳过环回/链路本地判据）、2 MB 上限、10s 超时；失败返回 <c>null</c>（调用方按原口径处理）。
    /// </summary>
    internal static async Task<byte[]?> TryGetBytesImpersonatedAsync(string url, CancellationToken ct)
    {
        var fallback = ImpersonatingClient.Value;
        if (fallback is null || !IsValidUrl(url)) return null;

        var current = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (SystemProxy is null)
                await EnsurePublicHostAsync(current, ct).ConfigureAwait(false);

            using var response = await fallback.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null) return null;
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsValidUrl(next.AbsoluteUri)) return null;
                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is long declared && declared > MaxHtmlBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxHtmlBytes) return null;   // 图标超限：不落半截
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }

        return null;
    }

    /// <summary>读取正文（带上限）；按响应头里的 charset 解码，与托管实现的取字符口径一致。</summary>
    private static async Task<string> ReadHtmlAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxHtmlBytes) break;   // 只解析 <head>：超限就按已有内容解析
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var bounded = new StreamContent(buffer);
        if (content.Headers.ContentType is { } contentType)
            bounded.Headers.ContentType = contentType;
        return await bounded.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>反爬墙的两种常见形态：阿里云 WAF 的 503（acw 系，火绒官网即此例）与 Cloudflare / 腾讯云的 403。</summary>
    private static bool IsBotWall(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// 兜底路径的连接前判据：解析目标主机、要求存在放行地址（与 <see cref="ConnectPublicAsync"/> 同一口径；
    /// 字面 IP 直接判定）。
    /// <para><b>已知差异</b>：curl 不暴露"自选连接地址"的钩子，这里只能"请求前校验"，与 curl 自己的解析
    /// 之间存在极小的 TOCTOU 窗口（DNS 重绑定需先以公网地址通过主路径、再于毫秒级翻转到内网地址——
    /// 触发条件苛刻，且兜底仅在主路径已被反爬拦截后启动。作为已知代价记录，见 WARNINGS）。</para>
    /// </summary>
    private static async Task EnsurePublicHostAsync(Uri target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target.Host, out var literal))
        {
            if (!IsAllowed(literal))
                throw new IOException($"refused to connect (loopback/link-local address): {target.Host}");
            return;
        }

        var addresses = await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
        if (Array.Find(addresses, IsAllowed) is null)
            throw new IOException($"refused to connect (loopback/link-local address): {target.Host}");
    }

    /// <summary>
    /// 连接前解析目标地址并挡掉环回 / 链路本地（含 IPv6 fe80::、以及 v4-mapped 形式）。
    /// 放在连接回调里而不是"先解析再请求"：校验与实际连接用的是同一次解析结果，中间没有可乘之机。
    /// </summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;

        // 配了系统代理时，握手端点就是代理本身：它是用户自己选的出口，不是被抓取的目标，
        // 不能拿环回判据去挡（挡了等于断网）。目标主机的解析交给代理去做。
        var toProxy = IsSystemProxy(endpoint);
        if (!toProxy)
        {
            var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
            var allowed = Array.Find(addresses, IsAllowed)
                ?? throw new IOException($"refused to connect (loopback/link-local address): {endpoint.Host}");
            endpoint = new DnsEndPoint(allowed.ToString(), endpoint.Port);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>该端点是不是系统代理本身（是则免于环回/链路本地判定）。</summary>
    private static bool IsSystemProxy(DnsEndPoint endpoint)
    {
        if (SystemProxy is null) return false;
        if (endpoint.Port != SystemProxy.Port) return false;

        if (string.Equals(endpoint.Host, SystemProxy.Host, StringComparison.OrdinalIgnoreCase)) return true;

        // 代理写的是 127.0.0.1、端点拿到 localhost（或反之）：同为环回即视为同一个出口。
        return IPAddress.TryParse(endpoint.Host, out var a) && IPAddress.IsLoopback(a)
               && IPAddress.TryParse(SystemProxy.Host, out var b) && IPAddress.IsLoopback(b);
    }

    private static bool IsAllowed(IPAddress address)
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
}

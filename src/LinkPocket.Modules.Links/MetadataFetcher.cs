using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 元数据网络抓取（internal）：HttpClient 池化 + 10s 超时 + 手动跟随重定向（每跳校验）+ 浏览器 UA。
/// 解析交给 <see cref="RegexMetadataParser"/>（L0 纯函数）；网络命令在引擎里走读池（免写闸）——
/// 慢站点不阻塞任何数据操作（「网络在闸外」的落点）。
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
internal static class MetadataFetcher
{
    /// <summary>正文上限（字节）。</summary>
    private const int MaxHtmlBytes = 2 * 1024 * 1024;

    /// <summary>重定向跳数上限（手动跟随：每跳都要重新校验）。</summary>
    private const int MaxRedirects = 5;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // 重定向由 DownloadAsync 手动跟随并逐跳校验
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectPublicAsync,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>URL 是否为合法的 http/https 绝对地址（LP.VAL.005 判定口径）。</summary>
    public static bool IsValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>抓取并解析页面元数据；网络/解析失败抛 <see cref="EngineException"/>（NETWORK_ERROR，可重试）。</summary>
    public static async Task<PageMetadata> FetchAsync(string url, CancellationToken ct)
    {
        if (!IsValidUrl(url))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidUrl, $"URL is not a valid absolute http/https address: {url}"));

        try
        {
            var (html, finalUri) = await DownloadAsync(url, ct).ConfigureAwait(false);
            return RegexMetadataParser.Instance.Parse(html, finalUri);
        }
        catch (EngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.NetworkError,
                $"page metadata fetch failed: {ex.Message}",
                retryable: true));
        }
    }

    private static async Task<(string Html, Uri FinalUri)> DownloadAsync(string url, CancellationToken ct)
    {
        var current = new Uri(url);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

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

    /// <summary>
    /// 连接前解析目标地址并挡掉环回 / 链路本地（含 IPv6 fe80::、以及 v4-mapped 形式）。
    /// 放在连接回调里而不是"先解析再请求"：校验与实际连接用的是同一次解析结果，中间没有可乘之机。
    /// </summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        var target = Array.Find(addresses, IsAllowed)
            ?? throw new IOException($"refused to connect (loopback/link-local address): {context.DnsEndPoint.Host}");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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

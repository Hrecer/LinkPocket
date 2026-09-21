using LinkPocket.Contracts;
using System.Net;
using System.Text.RegularExpressions;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 元数据网络抓取（internal）：HttpClient 池化 + 10s 超时 + 5 次重定向 + 浏览器 UA。
/// 解析交给 <see cref="RegexMetadataParser"/>（L0 纯函数）；网络命令在引擎里走读池（免写闸）——
/// 慢站点不阻塞任何数据操作（「网络在闸外」的落点）。
/// </summary>
internal static class MetadataFetcher
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
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
            var html = await Client.GetStringAsync(url, ct);
            var uri = new Uri(url);
            return RegexMetadataParser.Instance.Parse(html, uri);
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
}

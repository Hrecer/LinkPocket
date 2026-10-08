using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Contracts;

/// <summary>
/// favicon 磁盘缓存的**唯一实现**：目录约定、文件名（SHA-256 + 扩展名白名单）、SVG 回落口径、
/// 下载降级链（原地址 → 站点根 <c>/favicon.ico</c> → 聚合源）全在这一处。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么落在契约层</b>：写方（引擎的预取队列）与读方（UIKit 的解码/预览、备份模块的图标搬运）
/// 分处不同程序集，而两者唯一共享的层就是这里；同一个缓存目录/命名在四处各写一份的写法
/// 只要有一处漂移，"本库链接指向的图标"与"缓存里那份文件"就会对不上，且没有任何判据能抓到。
/// </para>
/// <para>
/// <b>下载是受控的</b>：单文件大小上限（图标是 16–64px 位图，正常 ≤ 100 KB）、并发闸 4、
/// 同地址在飞去重（同目录里多行指向同一个图标时只发一次请求）、共用池化 <see cref="HttpClient"/>。
/// 早期实现是"每个调用各自 new HttpClient + 无限并发"，一次进入大目录会瞬时打满连接。
/// </para>
/// </remarks>
public static class FaviconCache
{
    /// <summary>缓存目录 = 程序目录/favicons（便携口径：删掉程序目录即清空全部数据）。</summary>
    public static readonly string CacheDirectory = Path.Combine(AppContext.BaseDirectory, "favicons");

    /// <summary>单文件上限：超过即判失败（挡住"服务器把一个大文件当图标返回"）。</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>并发闸：不限并发时，一次进入大目录会同时发出几百个请求。</summary>
    private const int MaxConcurrent = 4;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    /// <summary>同地址在飞去重：键 = 解析后地址。</summary>
    private static readonly ConcurrentDictionary<string, Task<bool>> InFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// 失败负面缓存（进程内）：键 = 解析后地址，值 = 在此之前不再重试的时刻。
    /// </summary>
    /// <remarks>
    /// 没有它时，"每次刷新都重试全部失败地址"是实测发生过的：真实库里 21 445 条链接的 favicon_url
    /// 是内嵌 <c>data:</c> URI（一条真实网址都没有），界面每次刷新都把去重后的 1 976 个地址重发一遍，
    /// 单次会话产生 3 587 条失败日志、持续 5 分钟（现场日志实证）。
    /// 失败多为"该站点没有图标 / 网络抖动"，给一段退避而不是永久封杀：TTL 过后仍会重试一次。
    /// </remarks>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> FailedUntil = new(StringComparer.Ordinal);

    /// <summary>失败退避时长。</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 内嵌图标（<c>data:</c> URI）判定。这类地址**不是可下载的网址**：
    /// 它自己就带着图标字节（`data:image/png;base64,…`），进网络只会得到
    /// `NotSupportedException: The 'data' scheme is not supported`（现场 2 394 条），
    /// 而且降级链还会把 <c>uri.Host</c> 为空的它拼成 `data:///favicon.ico` 再失败一次。
    /// 内嵌图标的显示由界面层解码（UIKit 的 `FaviconService`）。
    /// </summary>
    public static bool IsInlineData(string? url)
        => !string.IsNullOrWhiteSpace(url) && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 外部来源（浏览器书签导出等）携带的内嵌图标上限（字符）。
    /// </summary>
    /// <remarks>
    /// 真实站点图标 base64 化后通常 1–6 KB，16 KiB 有充分余量；**这不是截断线，是"认不认"的判据**
    /// （超过即按"没有图标"处理，见 <see cref="NormalizeExternalIcon"/>）。
    /// </remarks>
    public const int MaxInlineIconLength = 16 * 1024;

    /// <summary>
    /// 外部来源 favicon 值的规范化（导入/迁移的**唯一口径**）：要么原样可用、要么当它没有。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么绝不截断</b>：对 <c>data:</c> URI 截断是纯破坏 —— 前缀 <c>data:image/png;base64,</c>
    /// 占 22 字符，砍到 512 后载荷只剩 490，而 <c>490 % 4 = 2</c>，
    /// <see cref="Convert.FromBase64String"/> 必然抛 <c>FormatException</c>；解码处又是
    /// <c>catch { return null }</c>，于是"库里明明存着图标、界面永远画不出来"，且没有一条日志。
    /// 真实库里 17 745 条就是这么坏的（占全部内联图标的绝大部分）。
    /// </para>
    /// <para>
    /// 普通 URL 同理：半截地址必然 404，截断没有任何意义。所以超限一律判"没有图标"，
    /// 让界面走网络回落（站点根 <c>/favicon.ico</c> → 聚合源），也不往库里写一条永远解不开的死数据。
    /// </para>
    /// </remarks>
    public static string? NormalizeExternalIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var icon = value.Trim();
        if (icon.Length > MaxInlineIconLength) return null;
        if (IsInlineData(icon) && !IsDecodableInlineData(icon)) return null;

        return icon;
    }

    /// <summary>
    /// 内嵌图标的载荷是否真能解出字节（<c>base64</c> 长度合法且非空）。
    /// </summary>
    /// <remarks>
    /// 只做"能不能解"的判断，不产出字节（调用方拿到 true 后会各自解码）。
    /// 判定口径与 <see cref="LinkPocket.Services.FaviconService"/> 的解码口径一致：
    /// 逗号后必须是 base64，且 <see cref="Convert.FromBase64String"/> 不抛。
    /// </remarks>
    public static bool IsDecodableInlineData(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0 || comma + 1 >= dataUrl.Length) return false;

            var meta = dataUrl.Substring(5, comma - 5);
            if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase)) return false;

            return Convert.FromBase64String(dataUrl[(comma + 1)..]).Length > 0;
        }
        catch
        {
            return false;   // 截断的 base64、非法字符等：判"不可用"
        }
    }

    /// <summary>日志用的地址短写（内嵌图标与超长地址绝不整段进日志：单日 5.2 MB 日志的成因）。</summary>
    private static string ShortUrl(string url)
        => url.Length <= 96 ? url : string.Concat(url.AsSpan(0, 96), "…(", url.Length.ToString(), " chars)");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // 图标与页面同源，挡在 Cloudflare 后面的站点同样只对 h2 放行、对 HTTP/1.1 直接 403。
        // 不跟着走 h2，就会出现"页面解析成功、图标却永远下不来"（meoai.net 就是这一例）。
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        // 部分 CDN 对无 UA 请求直接拒绝或返回异常内容
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "image/avif,image/webp,image/png,image/*,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    /// <summary>SVG 不能直接当图标解码 → 回落站点根 <c>/favicon.ico</c>；地址非法时原样返回。
    /// 内嵌图标（<c>data:</c>）原样返回——它没有"站点根"，回落只会拼出非法地址。</summary>
    public static string ResolveFaviconUrl(string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;

        if (IsInlineData(originalUrl)) return originalUrl;

        if (GetExtensionFromUrl(originalUrl).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            return BuildDefaultFaviconUrl(originalUrl);

        return originalUrl;
    }

    /// <summary>站点根 <c>/favicon.ico</c>；地址非法返回空串。</summary>
    public static string BuildDefaultFaviconUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return $"{uri.Scheme}://{uri.Host}/favicon.ico";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>缓存文件路径 = 目录 + SHA256(地址) + 扩展名（同一地址恒定同名）。</summary>
    public static string GetCacheFilePath(string faviconUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(faviconUrl)));
        return Path.Combine(CacheDirectory, $"{hash}{GetExtensionFromUrl(faviconUrl)}");
    }

    /// <summary>已缓存 → 磁盘路径；未缓存 → null。读方（UI 解码、备份搬运）用这一条。</summary>
    public static string? TryGetCacheFilePath(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return null;
        var path = GetCacheFilePath(ResolveFaviconUrl(faviconUrl));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 确保已缓存：已存在 → true；否则按降级链下载并写入（成功都写"解析后地址"的缓存路径，
    /// 与读方命中口径一致）。同地址并发调用共享同一次下载。
    /// </summary>
    public static Task<bool> EnsureCachedAsync(string? faviconUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return Task.FromResult(false);

        // 内嵌图标：字节就在地址里，没有可下载的东西（界面自己解码），一个包都不发。
        if (IsInlineData(faviconUrl)) return Task.FromResult(false);

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        var cachePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(cachePath)) return Task.FromResult(true);

        // 退避窗口内不再重试（失败多为"该站点没有图标"，每次刷新重发一遍纯属白烧）。
        if (FailedUntil.TryGetValue(resolvedUrl, out var until) && until > DateTimeOffset.UtcNow)
            return Task.FromResult(false);

        return InFlight.GetOrAdd(resolvedUrl, key => DownloadAndCacheAsync(key, cachePath, ct));
    }

    private static async Task<bool> DownloadAndCacheAsync(string resolvedUrl, string cachePath, CancellationToken ct)
    {
        try
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = await TryDownloadAsync(resolvedUrl, ct).ConfigureAwait(false);

                if (bytes == null)
                {
                    var root = BuildDefaultFaviconUrl(resolvedUrl);
                    if (root.Length > 0 && root != resolvedUrl)
                        bytes = await TryDownloadAsync(root, ct).ConfigureAwait(false);
                }

                if (bytes == null)
                {
                    // 聚合源（按域名取图标）是最后的兜底：站点根也没有图标时才把域名发出去
                    if (BuildDefaultFaviconUrl(resolvedUrl) is { Length: > 0 } fallback)
                    {
                        var host = new Uri(fallback).Host;
                        bytes = await TryDownloadAsync($"https://api.iowen.cn/favicon/{host}.png", ct).ConfigureAwait(false);
                    }
                }

                if (bytes == null)
                {
                    MarkFailed(resolvedUrl);
                    return false;
                }

                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                FailedUntil.TryRemove(resolvedUrl, out _);   // 成功即解除退避
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            MarkFailed(resolvedUrl);
            LpLog.Error($"favicon cache write failed: {ShortUrl(resolvedUrl)} - {ex.Message}", ex);
            return false;
        }
        finally
        {
            InFlight.TryRemove(resolvedUrl, out _);
        }
    }

    /// <summary>记一次失败：写入退避窗口（下次刷新在窗口内不再重发同一地址）。</summary>
    private static void MarkFailed(string resolvedUrl)
        => FailedUntil[resolvedUrl] = DateTimeOffset.UtcNow + FailureBackoff;

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LpLog.Error($"favicon download failed [{(int)response.StatusCode}]: {ShortUrl(url)}", null);
                return null;
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxBytes)
            {
                LpLog.Error($"favicon download refused (declared {declared} bytes > {MaxBytes}): {ShortUrl(url)}", null);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    LpLog.Error($"favicon download refused (body over {MaxBytes} bytes): {ShortUrl(url)}", null);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (Exception ex)
        {
            LpLog.Error($"favicon download threw: {ShortUrl(url)} - {ex.Message}", ex);
            return null;
        }
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".ico" or ".gif" or ".bmp" or ".webp" or ".svg")
                return ext == ".jpeg" ? ".jpg" : ext;
        }
        catch
        {
            // 地址非法 → 按 .ico 记名
        }

        return ".ico";
    }
}

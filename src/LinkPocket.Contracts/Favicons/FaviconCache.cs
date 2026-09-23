using System.Collections.Concurrent;
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

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // 部分 CDN 对无 UA 请求直接拒绝或返回异常内容
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>SVG 不能直接当图标解码 → 回落站点根 <c>/favicon.ico</c>；地址非法时原样返回。</summary>
    public static string ResolveFaviconUrl(string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;

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

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        var cachePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(cachePath)) return Task.FromResult(true);

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

                if (bytes == null) return false;

                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            LpLog.Error($"favicon cache write failed: {resolvedUrl} - {ex.Message}", ex);
            return false;
        }
        finally
        {
            InFlight.TryRemove(resolvedUrl, out _);
        }
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LpLog.Error($"favicon download failed [{(int)response.StatusCode}]: {url}", null);
                return null;
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxBytes)
            {
                LpLog.Error($"favicon download refused (declared {declared} bytes > {MaxBytes}): {url}", null);
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
                    LpLog.Error($"favicon download refused (body over {MaxBytes} bytes): {url}", null);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (Exception ex)
        {
            LpLog.Error($"favicon download threw: {url} - {ex.Message}", ex);
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

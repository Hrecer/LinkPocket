using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Modules.Favicon;

/// <summary>
/// favicon 磁盘缓存（internal 平移既有 FaviconStore 语义）：
/// 缓存目录 = 程序目录/favicons；文件名 = SHA256(解析后地址) + 扩展名。
/// SVG 回落站点根 /favicon.ico（既有口径）。图片解码归前端（UIKit 职责），引擎不碰 WPF 类型。
/// </summary>
internal static class FaviconCache
{
    /// <summary>缓存目录（引擎进程 = 应用进程，目录约定与既有实现一致）。</summary>
    public static readonly string CacheDirectory =
        Path.Combine(AppContext.BaseDirectory, "favicons");

    /// <summary>SVG 源不可直接作图标时回落站点根 /favicon.ico（既有口径）。</summary>
    public static string ResolveFaviconUrl(string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;
        if (GetExtensionFromUrl(originalUrl).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(originalUrl);
                return $"{uri.Scheme}://{uri.Host}/favicon.ico";
            }
            catch
            {
                return originalUrl;
            }
        }

        return originalUrl;
    }

    public static string GetCacheFilePath(string faviconUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(faviconUrl)));
        var ext = GetExtensionFromUrl(faviconUrl);
        return Path.Combine(CacheDirectory, $"{hash}{ext}");
    }

    /// <summary>已缓存则返回磁盘路径；未缓存返回 null。</summary>
    public static string? TryGetCacheFilePath(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return null;
        var path = GetCacheFilePath(ResolveFaviconUrl(faviconUrl));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 确保已缓存（降级链：原地址 → 站点根 /favicon.ico → 聚合源）。
    /// 既有降级链口径：无论哪个来源成功都写原地址的缓存路径。
    /// </summary>
    public static async Task<bool> EnsureCachedAsync(string? faviconUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return false;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        var cachePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(cachePath)) return true;

        var bytes = await TryDownloadAsync(resolvedUrl, ct);

        if (bytes == null)
        {
            try
            {
                var uri = new Uri(resolvedUrl);
                var root = $"{uri.Scheme}://{uri.Host}/favicon.ico";
                if (root != resolvedUrl) bytes = await TryDownloadAsync(root, ct);
            }
            catch
            {
                // 地址非法 → 留给聚合源兜底
            }
        }

        if (bytes == null)
        {
            try
            {
                var host = new Uri(resolvedUrl).Host;
                bytes = await TryDownloadAsync($"https://api.iowen.cn/favicon/{host}.png", ct);
            }
            catch
            {
                // 聚合源失败即放弃
            }
        }

        if (bytes == null) return false;

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
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
            // 非法地址回落 .ico
        }

        return ".ico";
    }
}

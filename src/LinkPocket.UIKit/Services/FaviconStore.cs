using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Services;

/// <summary>
/// favicon 磁盘缓存（纯后端，不依赖任何 UI 框架）。
/// 图片解码与渲染由前端负责（WPF 侧的 FaviconService / 未来 Web 端的 img 标签）。
/// </summary>
public static class FaviconStore
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        // 部分 CDN（含 Bing）对无 UA 请求可能拒绝或返回异常内容
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>下载单个 URL；成功返回字节，失败返回 null（带日志）。</summary>
    private static async Task<byte[]?> TryDownloadAsync(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error($"favicon 下载失败 [{(int)response.StatusCode}]: {url}", null);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                Logger.Error($"favicon 下载为空: {url}", null);
                return null;
            }
            return bytes;
        }
        catch (Exception ex)
        {
            Logger.Error($"favicon 下载异常: {url} - {ex.Message}", ex);
            return null;
        }
    }

    public static readonly string CacheDirectory = Path.Combine(AppContext.BaseDirectory, "favicons");

    public static string ResolveFaviconUrl(string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return string.Empty;

        var ext = GetExtensionFromUrl(originalUrl).ToLower();
        if (ext == ".svg")
        {
            var fallback = GetFallbackIcoUrl(originalUrl);
            return fallback ?? originalUrl;
        }

        return originalUrl;
    }

    public static string GetCacheFilePath(string faviconUrl)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(faviconUrl)));
        var ext = GetExtensionFromUrl(faviconUrl);
        return Path.Combine(CacheDirectory, $"{hash}{ext}");
    }

    /// <summary>返回已缓存文件的磁盘路径；未缓存返回 null。</summary>
    public static string? TryGetCacheFilePath(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return null;
        var filePath = GetCacheFilePath(ResolveFaviconUrl(faviconUrl));
        return File.Exists(filePath) ? filePath : null;
    }

    /// <summary>确保 favicon 已下载到磁盘缓存；已存在或下载成功返回 true。
    /// 降级链：原 URL → {host}/favicon.ico → api.iowen.cn 聚合源（国内可达）。
    /// 无论哪个来源成功，都写入原 URL 对应的缓存路径，保证 LoadFromCache 命中。</summary>
    public static async Task<bool> EnsureCachedAsync(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return false;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        var cachePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(cachePath)) return true;

        // ① 原始 URL
        var bytes = await TryDownloadAsync(resolvedUrl);

        // ② 站点根 favicon.ico
        if (bytes == null)
        {
            var root = BuildDefaultFaviconUrl(resolvedUrl);
            if (!string.IsNullOrEmpty(root) && root != resolvedUrl)
                bytes = await TryDownloadAsync(root);
        }

        // ③ 聚合 favicon 源（国内网络可达，按域名取 64px 图标）
        if (bytes == null)
        {
            try
            {
                var host = new Uri(resolvedUrl).Host;
                bytes = await TryDownloadAsync($"https://api.iowen.cn/favicon/{host}.png");
            }
            catch { }
        }

        if (bytes == null) return false;

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"favicon 缓存写入失败: {cachePath} - {ex.Message}", ex);
            return false;
        }
    }

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

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".ico" or ".gif" or ".bmp" or ".webp" or ".svg")
                return ext == ".jpeg" ? ".jpg" : ext;
        }
        catch { }
        return ".ico";
    }

    private static string? GetFallbackIcoUrl(string faviconUrl)
    {
        try
        {
            var uri = new Uri(faviconUrl);
            return $"{uri.Scheme}://{uri.Host}/favicon.ico";
        }
        catch { }
        return null;
    }
}

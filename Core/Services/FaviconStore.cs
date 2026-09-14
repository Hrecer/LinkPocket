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
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

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

    /// <summary>确保 favicon 已下载到磁盘缓存；已存在或下载成功返回 true。</summary>
    public static async Task<bool> EnsureCachedAsync(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return false;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        if (File.Exists(GetCacheFilePath(resolvedUrl))) return true;

        try
        {
            using var response = await _httpClient.GetAsync(resolvedUrl);
            if (!response.IsSuccessStatusCode) return false;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return false;

            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(GetCacheFilePath(resolvedUrl), bytes);
            return true;
        }
        catch
        {
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

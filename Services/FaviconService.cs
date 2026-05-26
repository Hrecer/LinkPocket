using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace LinkPocket.Services;

public class FaviconService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly ConcurrentDictionary<string, BitmapImage> _memoryCache = new();
    private static BitmapImage? _defaultIcon;
    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinkPocket", "favicons");

    private static BitmapImage DefaultIcon
    {
        get
        {
            if (_defaultIcon == null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 16;
                bmp.DecodePixelHeight = 16;
                bmp.UriSource = new Uri("pack://application:,,,/Assets/default_favicon.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _defaultIcon = bmp;
            }
            return _defaultIcon;
        }
    }

    private static string GetCacheFilePath(string faviconUrl)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(faviconUrl)));
        var ext = GetExtensionFromUrl(faviconUrl);
        return Path.Combine(_cacheDir, $"{hash}{ext}");
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

    public static BitmapImage? LoadFromCache(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl))
            return null;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.TryGetValue(resolvedUrl, out var cached))
            return cached;

        var filePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(filePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 48;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                _memoryCache[resolvedUrl] = bmp;
                return bmp;
            }
            catch { return null; }
        }

        return null;
    }

    public static async Task PrefetchAndCacheAsync(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.ContainsKey(resolvedUrl)) return;

        var filePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(filePath)) return;

        try
        {
            using var response = await _httpClient.GetAsync(resolvedUrl);
            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return;

            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(filePath, bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 48;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            _memoryCache[resolvedUrl] = bmp;
        }
        catch { }
    }

    public async Task<BitmapImage?> GetFaviconAsync(string? faviconUrl, string pageUrl)
    {
        if (string.IsNullOrEmpty(faviconUrl))
            return DefaultIcon;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.TryGetValue(resolvedUrl, out var cached))
            return cached;

        var filePath = GetCacheFilePath(resolvedUrl);
        if (File.Exists(filePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 16;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                _memoryCache[resolvedUrl] = bmp;
                return bmp;
            }
            catch { }
        }

        try
        {
            using var response = await _httpClient.GetAsync(resolvedUrl);
            if (!response.IsSuccessStatusCode)
                return DefaultIcon;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
                return DefaultIcon;

            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(filePath, bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 16;
            bmp.DecodePixelHeight = 16;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            _memoryCache[resolvedUrl] = bmp;
            return bmp;
        }
        catch
        {
            return DefaultIcon;
        }
    }

    public void ClearCache()
    {
        _memoryCache.Clear();
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
}
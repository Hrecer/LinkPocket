using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace LinkPocket.Services;

public class FaviconService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
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

    public async Task<BitmapImage?> GetFaviconAsync(string? faviconUrl, string pageUrl)
    {
        if (string.IsNullOrEmpty(faviconUrl))
            return DefaultIcon;

        string cacheKey = faviconUrl;

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            using var response = await _httpClient.GetAsync(faviconUrl);
            if (!response.IsSuccessStatusCode)
                return DefaultIcon;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
                return DefaultIcon;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 16;
            bmp.DecodePixelHeight = 16;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            _cache[cacheKey] = bmp;
            return bmp;
        }
        catch
        {
            return DefaultIcon;
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
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

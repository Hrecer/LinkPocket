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
        AppContext.BaseDirectory, "favicons");

    private static BitmapImage DefaultIcon
    {
        get
        {
            if (_defaultIcon == null)
            {
                // 程序化生成默认图标：16x16 灰色圆角矩形
                var size = 16;
                var dpi = 96;
                var renderTarget = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
                var drawingVisual = new System.Windows.Media.DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180));
                    var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 140)), 0.5);
                    dc.DrawRoundedRectangle(brush, pen, new System.Windows.Rect(1, 1, size - 2, size - 2), 3, 3);
                    // 绘制简单的链接图标
                    var textBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                    var formatted = new System.Windows.Media.FormattedText("⬡", System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI Symbol"), 10, textBrush, dpi / 96.0);
                    dc.DrawText(formatted, new System.Windows.Point(2, 1));
                }
                renderTarget.Render(drawingVisual);
                renderTarget.Freeze();
                _defaultIcon = ConvertToBitmapImage(renderTarget);
            }
            return _defaultIcon;
        }
    }

    private static BitmapImage ConvertToBitmapImage(BitmapSource source)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
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
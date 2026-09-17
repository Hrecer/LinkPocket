using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace LinkPocket.Services;

/// <summary>
/// 前端 favicon 渲染层：基于后端 FaviconStore 的磁盘缓存，负责 WPF BitmapImage 解码与内存缓存。
/// 后端（LinkPocket.Core）不引用本文件。
/// </summary>
public class FaviconService
{
    private static readonly ConcurrentDictionary<string, BitmapImage> _memoryCache = new();
    private static BitmapImage? _defaultIcon;

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

    public static string ResolveFaviconUrl(string? originalUrl) => FaviconStore.ResolveFaviconUrl(originalUrl);

    public static string BuildDefaultFaviconUrl(string url) => FaviconStore.BuildDefaultFaviconUrl(url);

    public static BitmapImage? LoadFromCache(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl))
            return null;

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.TryGetValue(resolvedUrl, out var cached))
            return cached;

        var filePath = FaviconStore.GetCacheFilePath(resolvedUrl);
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

        if (!await FaviconStore.EnsureCachedAsync(resolvedUrl)) return;

        try
        {
            var filePath = FaviconStore.GetCacheFilePath(resolvedUrl);
            var bytes = await File.ReadAllBytesAsync(filePath);
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

        if (FaviconStore.TryGetCacheFilePath(resolvedUrl) is string filePath)
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

        if (!await FaviconStore.EnsureCachedAsync(resolvedUrl))
            return DefaultIcon;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 16;
            bmp.DecodePixelHeight = 16;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(await File.ReadAllBytesAsync(FaviconStore.GetCacheFilePath(resolvedUrl)));
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
}

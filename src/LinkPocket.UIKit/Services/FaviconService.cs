using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 前端 favicon 渲染层：基于 <see cref="FaviconCache"/> 的磁盘缓存，负责 WPF BitmapImage 解码与内存缓存。
/// 后端（引擎/模块）不引用本文件。
/// </summary>
public class FaviconService
{
    /// <summary>内存缓存上限（条）：解码后约 9 KB/枚，10k 库不设上限会常驻几十~几百 MB。</summary>
    private const int MemoryCacheLimit = 1024;

    private static readonly ConcurrentDictionary<string, BitmapImage> _memoryCache = new(StringComparer.Ordinal);

    /// <summary>FIFO 淘汰序（只记键；图标热度集中在少数站点，不必做精确 LRU）。</summary>
    private static readonly ConcurrentQueue<string> _cacheOrder = new();

    private static BitmapImage? _defaultIcon;

    /// <summary>
    /// 已上报过的坏内嵌图标（键 = 值的短指纹）。
    /// </summary>
    /// <remarks>
    /// 解码失败按值去重上报：真实库里存在上万条被截断的内嵌图标（见
    /// <see cref="FaviconCache.NormalizeExternalIcon"/> 的注释），列表每刷新一次就会逐个解码失败一次，
    /// 不去重会瞬间刷出上万条同因日志（历史上单日日志 5.2 MB 就是这类放大）。
    /// 只留指纹不留存原文：内嵌图标可达 16 KiB，整段进字典等于把整库图标再驻留一份。
    /// </remarks>
    private static readonly ConcurrentDictionary<string, bool> _badInlineReported = new(StringComparer.Ordinal);

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

    /// <summary>入缓存并按 FIFO 淘汰超限项（字典与顺序表都是并发容器，多线程解码也安全）。</summary>
    private static void Store(string key, BitmapImage image)
    {
        if (_memoryCache.TryAdd(key, image))
            _cacheOrder.Enqueue(key);

        while (_memoryCache.Count > MemoryCacheLimit && _cacheOrder.TryDequeue(out var oldest))
            _memoryCache.TryRemove(oldest, out _);
    }

    public static BitmapImage? LoadFromCache(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl))
            return null;

        var resolvedUrl = FaviconCache.ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.TryGetValue(resolvedUrl, out var cached))
            return cached;

        // 内嵌图标（`data:` URI）：字节就在地址里，**本地解码**即可 —— 既不进网络（那是必然失败的），
        // 也不丢图标。真实压力库里 21 445 条链接的 favicon 是这种形态（占 79%），
        // 不走这条分支的话整片列表只剩默认图标，且每次刷新会重发 1 976 个必然失败的请求。
        if (FaviconCache.IsInlineData(resolvedUrl))
            return DecodeInlineData(resolvedUrl);

        if (FaviconCache.TryGetCacheFilePath(resolvedUrl) is not string filePath)
            return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 48;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            Store(resolvedUrl, bmp);
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// 内嵌图标（<c>data:[&lt;mime&gt;][;base64],&lt;数据&gt;</c>）本地解码：支持 base64 形态，
    /// 其它形态（百分号编码的纯文本 data URI）不认、返回 null（走默认图标）。
    /// </summary>
    private static BitmapImage? DecodeInlineData(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var meta = dataUrl.Substring(5, comma - 5);           // "image/png;base64"
            if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 48;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            Store(dataUrl, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            // 坏数据（截断的 base64 等）只丢这一个图标，不抛；但**必须留痕** ——
            // 这里以前是静默 return null，结果是"库里存着图标、界面永远空白"，且没有任何日志可查，
            // 一个 512 字符截断的 bug 因此藏了整个会话。
            ReportBadInlineData(dataUrl, ex);
            return null;
        }
    }

    /// <summary>坏内嵌图标按值去重上报一次（同因不同值各报一次，刷屏由去重挡住）。</summary>
    private static void ReportBadInlineData(string dataUrl, Exception ex)
    {
        var comma = dataUrl.IndexOf(',');
        var payloadLength = comma < 0 ? 0 : dataUrl.Length - comma - 1;

        // 指纹 = 前缀 + 头 48 字符 + 长度：足以区分不同图标，又不驻留整段 16 KiB 载荷
        var fingerprint = $"{dataUrl[..Math.Min(48, dataUrl.Length)]}|{dataUrl.Length}";
        if (!_badInlineReported.TryAdd(fingerprint, true)) return;

        LpLog.Warn("inline favicon could not be decoded (dropped; the default icon is shown) - "
                   + $"value length {dataUrl.Length}, base64 payload length {payloadLength}"
                   + $"{(payloadLength % 4 != 0 ? $" (payload length % 4 = {payloadLength % 4}; not a valid base64 length - the value was very likely truncated)" : "")}"
                   + $": {dataUrl[..Math.Min(48, dataUrl.Length)]}…",
            ex, category: "favicon");
    }

    /// <summary>确保磁盘缓存就绪并解码入内存（并发/去重/大小上限统一由 <see cref="FaviconCache"/> 保证）。</summary>
    public static async Task PrefetchAndCacheAsync(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return;

        var resolvedUrl = FaviconCache.ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.ContainsKey(resolvedUrl)) return;

        // 内嵌图标没有"下载"这回事（解码已在 LoadFromCache 里完成；这里不联网）。
        if (FaviconCache.IsInlineData(resolvedUrl)) return;

        if (!await FaviconCache.EnsureCachedAsync(resolvedUrl)) return;

        if (FaviconCache.TryGetCacheFilePath(resolvedUrl) is not string filePath) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 48;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            Store(resolvedUrl, bmp);
        }
        catch { /* 字节流不是合法图像（坏图/格式不支持）：调用方回落地球占位符 */ }
    }

    public async Task<BitmapImage?> GetFaviconAsync(string? faviconUrl, string pageUrl)
    {
        if (string.IsNullOrEmpty(faviconUrl))
            return DefaultIcon;

        var resolvedUrl = FaviconCache.ResolveFaviconUrl(faviconUrl);

        if (_memoryCache.TryGetValue(resolvedUrl, out var cached))
            return cached;

        // 内嵌图标：本地解码（同 LoadFromCache 的理由），解不开才回默认图标。
        if (FaviconCache.IsInlineData(resolvedUrl))
            return DecodeInlineData(resolvedUrl) ?? DefaultIcon;

        if (FaviconCache.TryGetCacheFilePath(resolvedUrl) is string filePath)
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
                Store(resolvedUrl, bmp);
                return bmp;
            }
            catch { /* 同上：解码失败即当作没有图标 */ }
        }

        if (!await FaviconCache.EnsureCachedAsync(resolvedUrl))
            return DefaultIcon;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 16;
            bmp.DecodePixelHeight = 16;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(await File.ReadAllBytesAsync(FaviconCache.GetCacheFilePath(resolvedUrl)));
            bmp.EndInit();
            bmp.Freeze();

            Store(resolvedUrl, bmp);
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
        while (_cacheOrder.TryDequeue(out _)) { }
    }
}

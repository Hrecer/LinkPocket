using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Contracts;

/// <summary>favicon 缓存结果：<see cref="Cached"/> = 磁盘已有可用图标；否则 <see cref="Reason"/> 说明原因。</summary>
public sealed record FaviconFetchOutcome(bool Cached, string Reason);

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
    /// <summary>缓存目录 = 数据根 <c>data\favicons</c>（随安装走：删掉 <c>data\</c> 即清空全部运行数据）。</summary>
    /// <remarks>派生自 <see cref="AppPaths"/>（唯一解析实现），绝不在这里手拼 <c>AppContext.BaseDirectory</c>。</remarks>
    public static string CacheDirectory => Path.Combine(AppPaths.DataRoot, "favicons");

    /// <summary>
    /// SVG 光栅化结果的缓存版本：截图适配规则一改就 +1，让旧的 SVG 图标重新画一遍。
    /// 位图缓存不参与这个版本（同一地址仍是旧键），不会因一次光栅化改动而全量重下。
    /// </summary>
    private const string SvgRasterCacheVersion = "fit-v1|";

    /// <summary>单文件上限：超过即判失败（挡住"服务器把一个大文件当图标返回"）。</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>并发闸：不限并发时，一次进入大目录会同时发出几百个请求。</summary>
    private const int MaxConcurrent = 4;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    /// <summary>同地址在飞去重：键 = 解析后地址。</summary>
    private static readonly ConcurrentDictionary<string, Task<FaviconFetchOutcome>> InFlight = new(StringComparer.Ordinal);

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
        // 不声明 WebP / AVIF：本站图标是带 alpha 的 WebP 变体（Content-Type 仍是 image/webp，
        // 即使地址以 .png 结尾），而 WPF 的 WebP 解码器会丢掉 alpha（解码成 Bgr32）——
        // 透明区里的暗色 RGB 直接画出来就是黑块（quark.cn 即此例，深色只是表象）。
        // 只请求位图老格式与 SVG，CDN 会返回原图；万一仍返回 WebP，由 AcceptImage 拒收。
        client.DefaultRequestHeaders.Add("Accept",
            "image/png,image/x-icon,image/vnd.microsoft.icon,image/gif,image/jpeg,image/svg+xml,image/bmp;q=0.9,*/*;q=0.1");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    /// <summary>
    /// 浏览器指纹兜底下载钩子（**组合根装配**：由 Links 模块在装配时接上，与元数据抓取共用同一个 curl-impersonate 客户端）。
    /// 普通客户端被反爬墙拦下（403/503 / 连接被断）时用它再取一次——有些站点连图标文件都按客户端指纹拦
    /// （火绒官网即此例：浏览器能看图、HttpClient 拿到的是 503 拦截页）。
    /// <c>null</c> = 未装配（只有普通路径）；返回 <c>null</c> = 兜底也失败。
    /// </summary>
    public static Func<string, CancellationToken, Task<byte[]?>>? ImpersonatedDownloader { get; set; }

    /// <summary>
    /// SVG 图标光栅化钩子（**组合根装配**）：<c>url → PNG 字节</c>，失败返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要它</b>：现代文档站/组件库站大量只声明 <c>&lt;link rel="icon" type="image/svg+xml"&gt;</c>
    /// （WPF 的 <c>BitmapImage</c> 解不了 SVG）。以前的做法是"SVG 一律回落站点根 <c>/favicon.ico</c>"——
    /// 可很多站点的根目录根本没有 <c>favicon.ico</c>，于是图标直接消失。
    /// </para>
    /// <para>
    /// <b>实现在宿主侧</b>：有 UI 线程的宿主有浏览器内核（用它把 SVG 画成 PNG）；CLI / MCP 经能力桥
    /// 复用同一实例——桌面宿主开着就直连，否则真正遇到 SVG 时才按需启动能力宿主；仍不可用时钩子为
    /// <c>null</c>，退回旧口径（回落站点根）。
    /// </para>
    /// </remarks>
    public static Func<string, CancellationToken, Task<byte[]?>>? SvgRasterizer { get; set; }

    /// <summary>原样返回图标地址；地址非法时返回空串。
    /// <para>
    /// <b>SVG 不再降级成站点根</b>：以前"SVG 解不了 → 回落 <c>/favicon.ico</c>"，可不少站点根目录根本没有这个文件，
    /// 结果是图标凭空消失。现在 SVG 地址原样保留，由 <see cref="SvgRasterizer"/> 在落盘前画成 PNG；
    /// 没有渲染能力时——或光栅化失败时——再由下载那条链退回站点根。
    /// </para>
    /// 内嵌图标（<c>data:</c>）原样返回——它没有"站点根"，回落只会拼出非法地址。</summary>
    public static string ResolveFaviconUrl(string? originalUrl)
        => string.IsNullOrWhiteSpace(originalUrl) ? string.Empty : originalUrl;

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

    /// <summary>
    /// 把**已经在别处取到的**图标字节写进缓存（成功返回 <c>true</c>）。
    /// 字节是 SVG 时先交给宿主光栅化成 PNG（WPF 解不了 SVG）。
    /// <para>
    /// 用途：反爬站点把图标资源也一起拦住，普通 HTTP 拿不到，但在**已完成校验的浏览器页面**里
    /// <c>fetch(iconUrl)</c> 是通的——那边取到的是站点真正声明的那张图，比任何猜法都确定。
    /// 写入前同样过"是不是真图片"的校验，避免把错误页当图标存下来。
    /// </para>
    /// </summary>
    public static async Task<bool> StoreBytesAsync(string faviconUrl, byte[]? bytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl) || bytes is not { Length: > 0 }) return false;

        var image = AcceptImage(bytes, faviconUrl);

        // 页面声明的图标是 SVG（这类站点常见）：位图校验会拒掉，但 WPF 也画不了 SVG——
        // 先落到同目录的临时 .svg，交给宿主光栅化成 PNG，再当图标存。
        if (image is null && LooksLikeSvg(bytes) && SvgRasterizer is null)
            await PageRenderHook.EnsureAsync(ct).ConfigureAwait(false);

        if (image is null && LooksLikeSvg(bytes) && SvgRasterizer is { } rasterize)
        {
            var tmp = Path.Combine(CacheDirectory, "raster-" + Guid.NewGuid().ToString("N") + ".svg");
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                AtomicFile.WriteAllBytes(tmp, bytes);
                image = AcceptImage(await rasterize(new Uri(tmp).AbsoluteUri, ct).ConfigureAwait(false), faviconUrl);
            }
            catch (Exception ex)
            {
                LpLog.Warn($"favicon svg rasterize failed: {ShortUrl(faviconUrl)} - {ex.Message}", ex);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* 临时文件删不掉不影响结果，下次覆盖 */ }
            }
        }

        if (image is null) return false;

        try
        {
            var path = GetCacheFilePath(ResolveFaviconUrl(faviconUrl));
            AtomicFile.WriteAllBytes(path, image);   // 原子写：半截图标文件比没图标更难查
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"favicon store failed: {ShortUrl(faviconUrl)} - {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 缓存文件路径 = 目录 + SHA256(地址) + 扩展名（同一地址恒定同名）。
    /// SVG 地址额外带上光栅化版本，适配规则更新时只换掉这一类图标的缓存。
    /// </summary>
    public static string GetCacheFilePath(string faviconUrl)
    {
        var key = IsSvgUrl(faviconUrl) ? SvgRasterCacheVersion + faviconUrl : faviconUrl;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // SVG 位子上落盘的是光栅化后的 PNG，文件扩展名跟着内容走（BitmapImage 按内容解码，但名字要对得上）
        return Path.Combine(CacheDirectory, $"{hash}{CacheExtension(faviconUrl)}");
    }

    /// <summary>已缓存 → 磁盘路径；未缓存 → null。读方（UI 解码、备份搬运）用这一条。</summary>
    public static string? TryGetCacheFilePath(string? faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return null;
        var path = GetCacheFilePath(ResolveFaviconUrl(faviconUrl));
        if (!File.Exists(path)) return null;

        // 旧版本可能把错误页 / 乱码当图标落过盘：读一眼文件头，不是图片就删掉当未缓存（触发重新下载）
        try
        {
            Span<byte> head = stackalloc byte[8];
            using var stream = File.OpenRead(path);
            var read = stream.Read(head);
            if (!LooksLikeImage(head[..read]))
            {
                stream.Dispose();
                File.Delete(path);
                return null;
            }
        }
        catch (Exception ex)
        {
            LpLog.Warn($"favicon cache probe failed: {ex.Message}", ex);
        }

        return path;
    }

    /// <summary>
    /// 确保已缓存：已存在 → true；否则按降级链下载并写入（成功都写"解析后地址"的缓存路径，
    /// 与读方命中口径一致）。同地址并发调用共享同一次下载。
    /// </summary>
    public static async Task<bool> EnsureCachedAsync(string? faviconUrl, CancellationToken ct = default)
        => (await EnsureCachedDetailedAsync(faviconUrl, ct).ConfigureAwait(false)).Cached;

    /// <summary>
    /// 与 <see cref="EnsureCachedAsync"/> 同一条管线；额外返回**为什么没有图**，供
    /// <c>favicon.fetch</c> 逐项如实上报（内嵌 / 站内没有 / 被反爬 / 宿主不能画 SVG …）。
    /// </summary>
    public static Task<FaviconFetchOutcome> EnsureCachedDetailedAsync(
        string? faviconUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl))
            return Task.FromResult(new FaviconFetchOutcome(false, FaviconFetchReasons.Empty));

        // 内嵌图标：字节就在地址里，没有可下载的东西（界面自己解码），一个包都不发。
        if (IsInlineData(faviconUrl))
            return Task.FromResult(new FaviconFetchOutcome(false, FaviconFetchReasons.InlineData));

        var resolvedUrl = ResolveFaviconUrl(faviconUrl);
        if (!IsHttpUrl(resolvedUrl))
            return Task.FromResult(new FaviconFetchOutcome(false, FaviconFetchReasons.InvalidUrl));

        var cachePath = GetCacheFilePath(resolvedUrl);
        // 带校验的查询：坏图（旧版本落下的 WebP / 错误页）会被删掉并按未缓存处理，触发重新下载。
        if (TryGetCacheFilePath(resolvedUrl) is not null)
            return Task.FromResult(new FaviconFetchOutcome(true, FaviconFetchReasons.Cached));

        // 退避窗口内不再重试（失败多为"该站点没有图标"，每次刷新重发一遍纯属白烧）。
        if (FailedUntil.TryGetValue(resolvedUrl, out var until) && until > DateTimeOffset.UtcNow)
            return Task.FromResult(new FaviconFetchOutcome(false, FaviconFetchReasons.Backoff));

        return InFlight.GetOrAdd(resolvedUrl, key => DownloadAndCacheAsync(key, cachePath, ct));
    }

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static async Task<FaviconFetchOutcome> DownloadAndCacheAsync(
        string resolvedUrl, string cachePath, CancellationToken ct)
    {
        try
        {
            // 与元数据抓取同一道连接前判据：favicon 下载也不能成为访问本机 / 链路本地的后门。
            if (!await TargetGuard.IsAllowedAsync(new Uri(resolvedUrl), ct).ConfigureAwait(false))
                return new FaviconFetchOutcome(false, FaviconFetchReasons.InvalidTarget);

            // SVG 需要浏览器内核：无头宿主在这里按需装配（装配失败就按原口径回落站点根）。
            if (IsSvgUrl(resolvedUrl) && SvgRasterizer is null)
                await PageRenderHook.EnsureAsync(ct).ConfigureAwait(false);

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[]? bytes = null;
                var reason = FaviconFetchReasons.Failed;
                var svgUnavailable = false;

                // SVG 图标：先让宿主把它画成 PNG（WPF 解不了 SVG）。
                // 光栅化拿不到 → 下面按原链路回落站点根 / 聚合源，且**绝不把 SVG 原文当位图落盘**。
                if (IsSvgUrl(resolvedUrl))
                {
                    if (SvgRasterizer is { } rasterize)
                        bytes = await rasterize(resolvedUrl, ct).ConfigureAwait(false);
                    else
                        svgUnavailable = true;

                    if (bytes is not { Length: > 0 }) reason = FaviconFetchReasons.SvgRasterizerUnavailable;
                }
                else
                {
                    (bytes, reason) = await TryDownloadAsync(resolvedUrl, ct).ConfigureAwait(false);
                }

                if (bytes is { Length: > 0 } && LooksLikeSvg(bytes)) bytes = null;
                bytes = AcceptImage(bytes, resolvedUrl);
                if (bytes is null && reason == FaviconFetchReasons.Cached)
                    reason = FaviconFetchReasons.Failed;   // 防御：原始下载成功但内容不是图片

                if (bytes == null)
                {
                    var root = BuildDefaultFaviconUrl(resolvedUrl);
                    if (root.Length > 0 && root != resolvedUrl)
                    {
                        var (fallback, fallbackReason) = await TryDownloadAsync(root, ct).ConfigureAwait(false);
                        bytes = AcceptImage(fallback, root);
                        if (bytes is null) reason = fallbackReason;
                    }
                }

                if (bytes == null)
                {
                    MarkFailed(resolvedUrl);
                    if (svgUnavailable) reason = FaviconFetchReasons.SvgRasterizerUnavailable;
                    return new FaviconFetchOutcome(false, reason);
                }

                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                FailedUntil.TryRemove(resolvedUrl, out _);   // 成功即解除退避
                return new FaviconFetchOutcome(true, FaviconFetchReasons.Cached);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new FaviconFetchOutcome(false, FaviconFetchReasons.Cancelled);
        }
        catch (Exception ex)
        {
            MarkFailed(resolvedUrl);
            LpLog.Error($"favicon cache write failed: {ShortUrl(resolvedUrl)} - {ex.Message}", ex);
            return new FaviconFetchOutcome(false, FaviconFetchReasons.Failed);
        }
        finally
        {
            InFlight.TryRemove(resolvedUrl, out _);
        }
    }

    /// <summary>记一次失败：写入退避窗口（下次刷新在窗口内不再重发同一地址）。</summary>
    private static void MarkFailed(string resolvedUrl)
        => FailedUntil[resolvedUrl] = DateTimeOffset.UtcNow + FailureBackoff;

    /// <summary>下载一个地址的字节。普通路径被反爬墙拦下（403/503 / 连接被断）时，
    /// 经 <see cref="ImpersonatedDownloader"/>（浏览器指纹，装配于组合根）重试一次。
    /// 返回的 reason 只在失败时有意义（成功固定为 <see cref="FaviconFetchReasons.Cached"/>）。</summary>
    private static async Task<(byte[]? Bytes, string Reason)> TryDownloadAsync(string url, CancellationToken ct)
    {
        var (bytes, botWall, reason) = await TryDownloadPlainAsync(url, ct).ConfigureAwait(false);
        if (bytes != null) return (bytes, FaviconFetchReasons.Cached);
        if (!botWall || ImpersonatedDownloader is not { } fallback) return (null, reason);

        try
        {
            var impersonated = await fallback(url, ct).ConfigureAwait(false);
            if (impersonated is { Length: > 0 and <= MaxBytes }) return (impersonated, FaviconFetchReasons.Cached);

            LpLog.Error($"favicon impersonated download failed (empty or over {MaxBytes} bytes): {ShortUrl(url)}", null);
        }
        catch (Exception ex)
        {
            LpLog.Error($"favicon impersonated download threw: {ShortUrl(url)} - {ex.Message}", ex);
        }
        return (null, reason);
    }

    /// <summary>图标的来源地址（<c>scheme://authority/</c>），用于防盗链 CDN 的 Referer；地址非法时返回 <c>null</c>。</summary>
    private static Uri? BuildReferrer(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new Uri($"{uri.Scheme}://{uri.Authority}/")
            : null;

    /// <summary>普通路径下载。<c>BotWall</c> = 失败形态像反爬墙（403/503 或连接异常）→ 值得交给指纹兜底再试。</summary>
    private static async Task<(byte[]? Bytes, bool BotWall, string Reason)> TryDownloadPlainAsync(
        string url, CancellationToken ct)
    {
        try
        {
            // 部分 CDN 按 Referer 防盗链：直接请求图标地址会 403（ieway.cn 即此例），
            // 浏览器是从页面带 Referer 取的。这里用图标自身的来源作 Referer——
            // 同源图标等于页面来源；跨域 CDN 也满足"来源属于该站"的常见白名单。
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = BuildReferrer(url);
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LpLog.Error($"favicon download failed [{(int)response.StatusCode}]: {ShortUrl(url)}", null);
                var wall = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;
                var status = (int)response.StatusCode;
                var reason = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? FaviconFetchReasons.NotFound
                    : status is 401 or 403 or 405 or 406 or 451 || status >= 500
                        ? FaviconFetchReasons.Blocked
                        : FaviconFetchReasons.Failed;
                return (null, wall, reason);
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxBytes)
            {
                LpLog.Error($"favicon download refused (declared {declared} bytes > {MaxBytes}): {ShortUrl(url)}", null);
                return (null, false, FaviconFetchReasons.TooLarge);
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
                    return (null, false, FaviconFetchReasons.TooLarge);
                }
                buffer.Write(chunk, 0, read);
            }

            return (buffer.Length == 0 ? null : buffer.ToArray(), false,
                buffer.Length == 0 ? FaviconFetchReasons.NotFound : FaviconFetchReasons.Cached);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, false, FaviconFetchReasons.Cancelled);
        }
        catch (Exception ex)
        {
            // 连接被断（TLS 握手重置等）也是反爬墙的常见形态 → 交给指纹兜底判定
            LpLog.Error($"favicon download threw: {ShortUrl(url)} - {ex.Message}", ex);
            return (null, true, FaviconFetchReasons.Blocked);
        }
    }

    /// <summary>缓存文件扩展名：SVG 位子上落盘的是光栅化后的 PNG，所以按 <c>.png</c> 记名。</summary>
    private static string CacheExtension(string url)
        => GetExtensionFromUrl(url).Equals(".svg", StringComparison.OrdinalIgnoreCase) ? ".png" : GetExtensionFromUrl(url);

    /// <summary>这个地址是不是 SVG 图标（要光栅化才能当位图用）。</summary>
    private static bool IsSvgUrl(string url)
        => GetExtensionFromUrl(url).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>按文件头认 PNG / JPEG / GIF / BMP / ICO / CUR。
    /// <para>**不认 WebP**：WPF 的 WebP 解码器返回无 alpha 的 Bgr32，透明图标会变成黑块
    /// （真实站点把图标地址的 CDN 变体做成带 alpha 的 WebP，见 <see cref="CreateHttpClient"/>）。
    /// 旧缓存里的 WebP 因此会被 <see cref="TryGetCacheFilePath"/> 当作坏图删掉并重下。</para>
    /// </summary>
    private static bool LooksLikeImage(ReadOnlySpan<byte> head) => head switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => true,                       // PNG
        [0xFF, 0xD8, 0xFF, ..] => true,                             // JPEG
        [0x47, 0x49, 0x46, 0x38, ..] => true,                       // GIF8
        [0x42, 0x4D, ..] => true,                                   // BMP
        [0x00, 0x00, 0x01, 0x00, ..] => true,                       // ICO
        [0x00, 0x00, 0x02, 0x00, ..] => true,                       // CUR
        _ => false,
    };

    /// <summary>字节是不是 WebP（RIFF 容器 + WEBP 标识）：拒收，理由同 <see cref="LooksLikeImage"/>。</summary>
    private static bool LooksLikeWebP(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12
           && bytes[..4].SequenceEqual("RIFF"u8)
           && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>
    /// 只收"真图片"：按文件头认 PNG / JPEG / GIF / BMP / ICO / WEBP。
    /// <para>
    /// 反爬站点对静态资源也回 403 的 HTML，或者返回一段占位脚本；这些东西一旦被当成图标落盘，
    /// 界面上就是一张"解码失败的破图"——比没有图标更难查。所以每个来源拿到字节都先过这一关，
    /// 不合格就当没拿到，继续走回落链。
    /// </para>
    /// </summary>
    private static byte[]? AcceptImage(byte[]? bytes, string source)
    {
        if (bytes is not { Length: >= 8 }) return null;
        if (LooksLikeWebP(bytes))
        {
            // 透明图标的 WebP 变体：WPF 解码丢 alpha，落盘后就是黑块——宁可回落站点根，也不存坏图
            LpLog.Warn($"favicon rejected (WebP variant; WPF drops its alpha): {ShortUrl(source)}", null);
            return null;
        }
        if (LooksLikeSvg(bytes)) return null;   // 原文不能当位图；SVG 应经光栅化再进来
        if (LooksLikeImage(bytes)) return bytes;

        LpLog.Warn($"favicon rejected (not an image, {bytes.Length} bytes): {ShortUrl(source)}", null);
        return null;
    }

    /// <summary>字节看起来是不是 SVG 原文（光栅化失败时别把它当位图落盘）。</summary>
    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
               || head.Contains("<!doctype svg", StringComparison.OrdinalIgnoreCase);
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

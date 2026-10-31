using System.IO;
using System.Text.Json;
using System.Windows;
using LinkPocket.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LinkPocket.Services;

/// <summary>
/// 浏览器渲染兜底（WebView2 = 系统 Edge 内核）：把网址真正渲染一遍，再取 <c>document.title</c> 与描述。
/// 装配给 <see cref="PageRenderHook"/>，只在"HTTP 拿不到可用元数据"时被调用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么非它不可</b>：有一类站点的人机校验必须执行页内脚本（火山引擎 WAF 的
/// <c>__waf_verification_challenge</c> 即此例：200 返回一张"正在进行安全检测"的空壳，脚本算出 proof、
/// 写入放行 cookie 后自行重载）。请求头、TLS/HTTP2 指纹、乃至 headless 浏览器（暴露
/// <c>navigator.webdriver</c>）一律过不去，只有真浏览器内核能过。实测同一地址：curl 与
/// curl-impersonate 拿回的都是 25 KB 挑战页；本渲染器 1.4 s 就拿到真实标题。
/// </para>
/// <para>
/// <b>认标题的判据不是"有字就算"</b>：主框架最近一次导航必须是 2xx。有一类站点先回一条错误/自举响应
/// （这层壳同样有标题），再由页内脚本自行重载给出正文——按"标题非空"就收工，
/// 拿回来的正是那层壳的标题。所以这里同时看 <c>HttpStatusCode</c>：导航没成功就继续等到超时，
/// 宁可返回"没拿到"（调用方按原口径报错），也不把壳的标题当成站点名称。
/// </para>
/// <para>
/// <b>代价与纪律</b>：起一个 Chromium 渲染进程（约 1 s 冷启动、几十 MB 常驻），所以
/// <b>只在兜底路径使用</b>、<b>全局串行</b>（浏览器实例不可并发）、<b>用完即销毁</b>；
/// profile 目录复用，放行 cookie 留着，同一站点第二次通常不再挑战。
/// </para>
/// <para>
/// <b>不可用时自动降级</b>：目标机没装 WebView2 Runtime（<c>GetAvailableBrowserVersionString</c> 抛错）
/// → 记一条日志、此后不再尝试，抓取退回"仅 HTTP"的既有行为。
/// </para>
/// </remarks>
internal sealed class PageRenderer : IDisposable
{
    /// <summary>渲染轮询间隔：挑战页放行后会自行重载，标题在重载后才有。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>渲染预算（在 <c>MetadataFetcher</c> 的 15 s 外层预算之内，留出销毁时间）。</summary>
    private static readonly TimeSpan RenderBudget = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 挑战页自带的标题（还没放行时读到的就是它，不能当成页面标题）。
    /// <para>
    /// 中文部分是 <c>\u</c> 转义：匹配的是**远端页面标题**，不是界面文案（不进 Loc 表），
    /// 而"零中文字面量"棘轮没有形状白名单。解码后 = "安全检测"、"安全验证"、"请稍候"。
    /// </para>
    /// </summary>
    private static readonly string[] ChallengeTitles =
    [
        "\u5b89\u5168\u68c0\u6d4b", "\u5b89\u5168\u9a8c\u8bc1", "\u8bf7\u7a0d\u5019",
        "Just a moment", "Checking your browser", "Verify you are human",
    ];

    /// <summary>Chromium 启动参数：离屏窗口会被判定为"被遮挡/后台"从而节流定时器，
    /// 而校验脚本正是靠定时器推进的——这三个开关缺一不可。</summary>
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding "
        + "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<CoreWebView2Environment?>? _environment;
    private bool _unavailable;

    /// <summary>渲染一个地址；未装运行时、正忙、超时、脚本报错一律返回 <c>null</c>（兜底失败不影响主路径）。</summary>
    public async Task<RenderedPageMetadata?> RenderAsync(string url, CancellationToken ct)
    {
        if (_unavailable || !Uri.TryCreate(url, UriKind.Absolute, out _)) return null;

        // 串行：一个 UI 线程同时只能伺候一个浏览器实例。正忙就直接放弃（不排队堆积）
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return null;
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return null;

            // WebView2 只能在有消息泵的 UI 线程上创建；引擎侧调用发生在读池的后台线程
            return await dispatcher.InvokeAsync(() => RenderOnUiThreadAsync(url, ct)).Task.Unwrap()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LpLog.Warn($"page render failed: {url}", ex, category: "links.metadata");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RenderedPageMetadata?> RenderOnUiThreadAsync(string url, CancellationToken ct)
    {
        Window? window = null;
        WebView2? view = null;
        try
        {
            var environment = await GetEnvironmentAsync().ConfigureAwait(true);
            if (environment is null) return null;

            window = new Window
            {
                Width = 1024,
                Height = 768,
                Left = -4000,          // 离屏：不抢焦点、不进任务栏、不闪现在用户面前
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "LinkPocket page render",
            };
            view = new WebView2();
            window.Content = view;
            window.Show();

            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // 主框架最近一次导航是否**真的拿到了页面**（2xx）。首屏经常不是真页面：
            // 有的站点先用一条错误/自举响应对付脚本客户端，再靠页内脚本自行重载给出正文——
            // 那页也是有标题的，只按"标题非空"就收工，拿回来的是"I'm a teapot"那类壳的标题。
            var navigationOk = false;
            core.NavigationCompleted += (_, e) =>
                navigationOk = e.IsSuccess
                               && (e.HttpStatusCode == 0 || (e.HttpStatusCode >= 200 && e.HttpStatusCode < 400));

            core.Navigate(url);

            var deadline = DateTime.UtcNow + RenderBudget;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                var reading = await ReadAsync(core).ConfigureAwait(true);
                if (reading is null || string.IsNullOrWhiteSpace(reading.Title)) continue;
                if (IsChallengeTitle(reading.Title)) continue;   // 还没放行：继续等它重载
                if (!navigationOk) continue;                     // 首屏不是真页面：等它自己重载
                return reading;
            }

            return null;
        }
        finally
        {
            // 收尾阶段的两个失败都与结果无关：实例已关时再释放 / 窗口已关时再关，都会抛，不能盖掉渲染结果
            try { view?.Dispose(); } catch { /* 实例已关闭：再释放必抛，与本次渲染结果无关 */ }
            try { window?.Close(); } catch { /* 窗口已关闭：再关必抛，与本次渲染结果无关 */ }
        }
    }

    /// <summary>读一次页面标题与描述（脚本返回 JSON 字符串，外层再包一层 JSON 编码）。</summary>
    private static async Task<RenderedPageMetadata?> ReadAsync(CoreWebView2 core)
    {
        const string script = """
            (() => {
              const meta = s => { const e = document.querySelector(s); return e ? (e.content || '') : ''; };
              return JSON.stringify({
                t: document.title || '',
                d: meta('meta[name="description"]') || meta('meta[property="og:description"]')
              });
            })()
            """;

        var encoded = await core.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(encoded) || encoded == "null") return null;

        var payload = JsonSerializer.Deserialize<string>(encoded);
        if (string.IsNullOrEmpty(payload)) return null;

        using var doc = JsonDocument.Parse(payload);
        var title = doc.RootElement.GetProperty("t").GetString();
        var description = doc.RootElement.GetProperty("d").GetString();
        return new RenderedPageMetadata(title, description);
    }

    private static bool IsChallengeTitle(string title)
        => Array.Exists(ChallengeTitles, t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>环境（含运行时可用性探测）只建一次：缺运行时的判定要记一次日志，不必每次都试。</summary>
    private Task<CoreWebView2Environment?> GetEnvironmentAsync()
        => _environment ??= CreateEnvironmentAsync();

    private async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            _unavailable = true;
            LpLog.Warn("WebView2 runtime not found; page render fallback disabled", ex, category: "links.metadata");
            return null;
        }

        try
        {
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
            return await CoreWebView2Environment.CreateAsync(null, ProfileDirectory, options).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            LpLog.Warn("WebView2 environment creation failed; page render fallback disabled", ex,
                category: "links.metadata");
            return null;
        }
    }

    /// <summary>浏览器 profile 目录：放行 cookie 需要跨进程留着，但它是运行痕迹，不进数据区。</summary>
    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkPocket", "page-profile");

    public void Dispose() => _gate.Dispose();
}

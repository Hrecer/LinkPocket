using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.Services;

/// <summary>
/// 人机验证（Cloudflare Turnstile 这类只能由人放行的墙）：开一个<b>独立的小窗口真实浏览器</b>，
/// 请用户点一下，然后通过调试协议把标题与描述读回来。装配给 <see cref="PageRenderHook.InteractiveRenderer"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能用应用内嵌的 WebView2</b>：实测同一地址、同一出口，WebView2 里连勾选框都不下发
/// （<c>iframes:0</c>、只有一张"请稍候…"），点了也没反应——站点把"嵌入式浏览器"直接判掉。
/// 而本机的真实 Edge/Chrome 会正常给出勾选框，点一下就能过。所以这一档必须借真实浏览器进程。
/// </para>
/// <para>
/// <b>隐私边界（三条硬约束）</b>：
/// ① 用<b>专用 profile</b>（<c>%LOCALAPPDATA%\LinkPocket\verify-profile</c>），与用户日常浏览器完全隔离；
/// ② <b>不读取</b>用户浏览器的 cookie、历史、密码、扩展——我们启动的是新实例，它没有任何既有登录态；
/// ③ 窗口以 <c>--app</c> 打开：无地址栏、无标签页、无扩展按钮，只有站点自己的页面 + 那个勾选框；
/// 用户点出来的放行凭证只落在这个专用 profile 里，随"清除数据"一并删除，不外发、不经第三方。
/// </para>
/// <para>
/// <b>打扰纪律</b>：只在<b>用户主动点"解析"</b>且站点确实在等人点时才会走到这里（批量 / 导入 / 后台预取
/// 一律不经过它）。窗口尺寸固定小窗、<b>不置顶</b>、不抢全屏；用户关掉窗口即放弃（进程退出即中止等待）。
/// </para>
/// </remarks>
internal sealed class HumanVerificationBrowser
{
    /// <summary>
    /// 轮询间隔：放行之后越早收工越好（用户已经看到页面了，多挂一秒都是多余的），所以取 300 ms。
    /// 每次轮询**一次往返**同时取标题与描述，避免"查到标题再单开一次连接"造成的滞后。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>调试协议就绪的最长等待：浏览器冷启动一般 1–2 s，给足 20 s。</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 浏览器启动参数：<c>--app</c> 让它成为一个"只有页面"的独立窗口（无地址栏 / 无标签页）；
    /// 固定小窗尺寸并给一个偏上的位置，避免遮住整个桌面；<b>不加任何置顶参数</b>。
    /// </summary>
    private const string BrowserArguments =
        "--no-first-run --no-default-browser-check --disable-features=Translate,MediaRouter "
        + "--remote-allow-origins=* --window-size=900,700 --window-position=240,120";

    /// <summary>挑战页自带的标题：读到它说明用户还没点完，继续等。</summary>
    private static readonly string[] ChallengeTitles =
    [
        "\u8bf7\u7a0d\u5019", "\u5b89\u5168\u68c0\u6d4b", "\u5b89\u5168\u9a8c\u8bc1",
        "Just a moment", "Checking your browser", "Verify you are human",
    ];

    /// <summary>
    /// 打开专用窗口请用户完成校验；拿到真页面就返回标题与描述，用户关窗 / 超时 / 没通过一律返回 <c>null</c>。
    /// </summary>
    public async Task<RenderedPageMetadata?> VerifyAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return null;

        var exe = FindBrowser();
        if (exe is null)
        {
            LpLog.Warn("no Edge/Chrome found; human verification window unavailable", category: "links.metadata");
            return null;
        }

        var port = FreePort();
        var profile = ProfileDirectory;
        Directory.CreateDirectory(profile);

        using var process = Start(exe, profile, port, target);
        if (process is null) return null;
        Retitle(process);

        try
        {
            if (!await WaitForDebuggerAsync(port, process, ct).ConfigureAwait(false)) return null;

            while (!ct.IsCancellationRequested)
            {
                if (process.HasExited) return null;   // 用户自己关了窗口 = 放弃验证

                var page = await FindPageAsync(port, ct).ConfigureAwait(false);
                if (page is not null)
                {
                    // 一次往返同时拿标题、描述、地址与 readyState：判"到没到真站点"只需要这一趟
                    var reading = await ReadPageAsync(page.WebSocketDebuggerUrl, ct).ConfigureAwait(false);
                    if (IsRealPage(reading, target))
                    {
                        LpLog.Info($"human verification passed ({reading!.Title}): {target.Host}",
                            category: "links.metadata");
                        return new RenderedPageMetadata(
                            reading.Title,
                            reading.Description,
                            string.IsNullOrWhiteSpace(reading.Icon) ? null : reading.Icon);
                    }
                }

                // 站点会在标题变化时改写窗口标题，这里持续压回我们自己的名字（窗口最多活几分钟，代价可忽略）
                Retitle(process);
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"human verification window failed: {target.Host}", ex, category: "links.metadata");
            return null;
        }
        finally
        {
            // 点完就关：不留一个后台浏览器，也不让用户自己收拾
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { LpLog.Debug($"closing verification browser failed: {ex.GetType().Name}", "links.metadata"); }
        }
    }

    /// <summary>起浏览器：新实例 + 专用 profile + <c>--app</c> 单页窗口。</summary>
    private static Process? Start(string exe, string profile, int port, Uri target)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profile}\" --remote-debugging-port={port} "
                            + $"--app=\"{target.AbsoluteUri}\" {BrowserArguments}",
            };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            LpLog.Warn("failed to launch the verification browser", ex, category: "links.metadata");
            return null;
        }
    }

    /// <summary>等调试端口就绪（浏览器冷启动）。超时 / 进程退出都算失败。</summary>
    private static async Task<bool> WaitForDebuggerAsync(int port, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited) return false;
            if (await FindPageAsync(port, ct).ConfigureAwait(false) is not null) return true;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>调试协议里的页面目标（<c>--app</c> 只有一个）。</summary>
    private sealed record PageTarget(string Title, string WebSocketDebuggerUrl);

    private static async Task<PageTarget?> FindPageAsync(int port, CancellationToken ct)
    {
        try
        {
            var json = await GetTargetListAsync(port, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return null;

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() != "page") continue;
                if (!item.TryGetProperty("webSocketDebuggerUrl", out var ws)) continue;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                return new PageTarget(title, ws.GetString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            // 端口还没起来 / 目标正在切换：都属于"再等一轮"，不是失败
            LpLog.Debug($"debugger not ready on {port}: {ex.GetType().Name}", "links.metadata");
        }

        return null;
    }

    /// <summary>
    /// 取调试协议的目标列表（<c>/json/list</c>）。
    /// <para>
    /// 这里刻意<b>不</b>用 <c>HttpClient</c>：那是全局连接池资源，架构红线要求它只出现在已审的少数文件里；
    /// 而这是"问一次本机回环端口"的一次性小请求，裸 TCP 更贴切也更轻。
    /// </para>
    /// </summary>
    private static async Task<string?> GetTargetListAsync(int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);

            await using var stream = tcp.GetStream();
            var request = $"GET /json/list HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return split < 0 ? null : raw[(split + 4)..];
        }
        catch (Exception ex)
        {
            // 端口还没起来 / 目标正在切换：都属于"再等一轮"，不是失败
            LpLog.Debug($"debugger not ready on {port}: {ex.GetType().Name}", "links.metadata");
            return null;
        }
    }

    /// <summary>读标题与描述（与 <c>PageRenderer.ReadAsync</c> 同一段脚本，口径一致）。</summary>
    private sealed record PageReading(string Title, string Description, string Icon, string Url, string Ready);

    private static async Task<PageReading?> ReadPageAsync(string wsUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wsUrl)) return null;

        const string script = """
            (() => {
              const meta = s => { const e = document.querySelector(s); return e ? (e.content || '') : ''; };
              const icon = document.querySelector('link[rel~="icon"]');
              let href = '';
              try { href = icon ? new URL(icon.getAttribute('href') || '', location.href).href : ''; } catch (e) { href = ''; }
              return JSON.stringify({
                t: document.title || '',
                d: meta('meta[name="description"]') || meta('meta[property="og:description"]'),
                i: href,
                h: location.href || '',
                r: document.readyState || ''
              });
            })()
            """;

        var request = "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":"
                      + JsonSerializer.Serialize(script) + ",\"returnByValue\":true}}";

        using var ws = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), timeout.Token).ConfigureAwait(false);
            await ws.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, timeout.Token)
                .ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            while (!timeout.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;

                var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
                if (!doc.RootElement.TryGetProperty("result", out var res)) return null;
                if (!res.TryGetProperty("result", out var value)) return null;

                var encoded = value.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(encoded)) return null;

                using var inner = JsonDocument.Parse(encoded);
                return new PageReading(
                    inner.RootElement.GetProperty("t").GetString() ?? string.Empty,
                    inner.RootElement.GetProperty("d").GetString() ?? string.Empty,
                    inner.RootElement.GetProperty("i").GetString() ?? string.Empty,
                    inner.RootElement.GetProperty("h").GetString() ?? string.Empty,
                    inner.RootElement.GetProperty("r").GetString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            LpLog.Debug($"reading metadata over CDP failed: {ex.GetType().Name}", "links.metadata");
        }

        return null;
    }

    /// <summary>
    /// "已经到了真站点、可以收工"的判据（三条同时成立）：
    /// ① 标题非空且不是挑战页标题；② 文档 <c>readyState=complete</c>（别在半渲染的中间态抓）；
    /// ③ 地址仍在原站点（挑战页会带 <c>?__cf_chl_rt_tk=</c> 之类的参数，放行后会回到干净的地址）。
    /// <para>
    /// 这里不看"等了多久"：窗口的唯一用途就是取标题与描述，拿到就退，一秒都不多留。
    /// </para>
    /// </summary>
    private static bool IsRealPage(PageReading? reading, Uri target)
        => reading is { Ready: "complete", Title.Length: > 0 }
           && !IsChallengeTitle(reading.Title)
           && reading.Url.Contains(target.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsChallengeTitle(string title)
        => Array.Exists(ChallengeTitles, t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    /// <summary>
    /// 把窗口标题压成我们自己的名字。
    /// <para>
    /// <c>--app</c> 窗口的标题与图标都跟着页面走——不加干预时，任务栏上会出现一个"站点自己开的窗口"，
    /// 看上去像钓鱼。标题能压回来（<c>SetWindowText</c>），<b>图标压不回来</b>：那个位图由渲染进程提供，
    /// 外部进程改不了。所以这是"尽力而为"，不做成硬承诺。
    /// </para>
    /// </summary>
    private static void Retitle(Process process)
    {
        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero) _ = SetWindowText(handle, Loc.T("verify.window.title"));
        }
        catch (Exception ex)
        {
            // 标题只是"别让人误会"的修饰，拿不到句柄不值得影响验证流程
            LpLog.Debug($"retitle failed: {ex.GetType().Name}", "links.metadata");
        }
    }

    /// <summary>本机真实浏览器（Edge 优先：Windows 自带，用户环境里一定有）。</summary>
    private static string? FindBrowser()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        ];

        return Array.Find(candidates, File.Exists);
    }

    /// <summary>让操作系统挑一个空闲端口给调试协议（不要撞上用户其它工具）。</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>专用 profile：与用户日常浏览器隔离；放行凭证留在这里，下次少点一次。</summary>
    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkPocket", "verify-profile");
}

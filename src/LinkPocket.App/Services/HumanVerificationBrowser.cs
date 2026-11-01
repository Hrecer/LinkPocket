using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 人机验证（Cloudflare Turnstile 这类只能由人放行的墙）：开一个<b>独立的小窗口真实浏览器</b>，
/// 请用户点一下，然后把标题、描述与图标读回来。装配给 <see cref="PageRenderHook.InteractiveRenderer"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能用应用内嵌的 WebView2</b>：实测同一地址、同一出口，WebView2 里连勾选框都不下发
/// （<c>iframes:0</c>、只有一张"请稍候…"），点了也没反应——站点把"嵌入式浏览器"直接判掉。
/// 而本机的真实 Edge/Chrome 会正常给出勾选框，点一下就能过。所以这一档必须借真实浏览器进程。
/// </para>
/// <para>
/// <b>隐私边界（三条硬约束）</b>：
/// ① 用<b>一次性 profile</b>（应用本地目录下的 <c>verify-&lt;guid&gt;</c>，验证完立即删除），与用户日常浏览器
/// 完全隔离、也不在本机留下任何站点凭证；旧版本留下的固定目录 <c>verify-profile</c> 会在首次使用时清掉；
/// ② <b>不读取</b>用户浏览器的 cookie、历史、密码、扩展——我们启动的是新实例，它没有任何既有登录态；
/// ③ 窗口以 <c>--app</c> 打开：无地址栏、无标签页、无扩展按钮，只有站点自己的页面 + 那个勾选框。
/// </para>
/// <para>
/// <b>打扰纪律</b>：只在<b>用户主动点"解析"</b>且站点确实在等人点时才会走到这里（批量 / 导入 / 后台预取
/// 一律不经过它）。窗口尺寸固定小窗、<b>不置顶</b>；用户关掉窗口即放弃（进程退出即中止等待）。
/// </para>
/// </remarks>
internal sealed class HumanVerificationBrowser
{
    /// <summary>
    /// 轮询间隔：放行之后越早收工越好（用户已经看到页面了，多挂一秒都是多余的），所以取 300 ms。
    /// 每轮只读一次页面状态，<b>不夹带任何网络请求</b>。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>挑战页自带的标题：读到它说明用户还没点完，继续等。</summary>
    private static readonly string[] ChallengeTitles =
    [
        "\u8bf7\u7a0d\u5019", "\u5b89\u5168\u68c0\u6d4b", "\u5b89\u5168\u9a8c\u8bc1",
        "Just a moment", "Checking your browser", "Verify you are human",
    ];

    /// <summary>
    /// 打开专用窗口请用户完成校验；拿到真页面就返回标题、描述与图标，
    /// 用户关窗 / 超时 / 没通过一律返回 <c>null</c>。
    /// </summary>
    public async Task<RenderedPageMetadata?> VerifyAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return null;

        var exe = VerificationBrowserHost.FindBrowser();
        if (exe is null)
        {
            LpLog.Warn("no Edge/Chrome found; human verification window unavailable", category: "links.metadata");
            return null;
        }

        var port = VerificationBrowserHost.FreePort();
        VerificationBrowserHost.CleanupPreviousProfiles();
        var profile = VerificationBrowserHost.CreateEphemeralProfile();

        using var process = VerificationBrowserHost.Start(exe, profile, port, target);
        if (process is null) return null;

        VerificationBrowserHost.Retitle(process);

        CdpSession? session = null;
        try
        {
            if (!await VerificationBrowserHost.WaitForPageAsync(port, process, ct).ConfigureAwait(false)) return null;

            while (!ct.IsCancellationRequested)
            {
                if (process.HasExited) return null;   // 用户自己关了窗口 = 放弃验证

                if (session is null)
                {
                    session = await VerificationBrowserHost.ConnectAsync(port, ct).ConfigureAwait(false);
                    if (session is null)
                    {
                        await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                PageReading? reading;
                try
                {
                    reading = await ReadPageAsync(session, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 挑战页整页重载时目标会短暂不可用：丢掉这条会话，下一轮重连即可
                    LpLog.Debug($"verification session dropped: {ex.GetType().Name}", "links.metadata");
                    await session.DisposeAsync().ConfigureAwait(false);
                    session = null;
                    continue;
                }

                if (IsRealPage(reading, target))
                {
                    LpLog.Info($"human verification passed ({reading!.Title}): {target.Host}",
                        category: "links.metadata");

                    // 文字先定下来，图标单独取一次：它失败不该影响标题/描述，更不该拖住关窗
                    var icon = await GrabIconAsync(session, target, ct).ConfigureAwait(false);
                    return new RenderedPageMetadata(reading.Title, reading.Description, icon);
                }

                // 站点会在标题变化时改写窗口标题，这里持续压回我们自己的名字
                VerificationBrowserHost.Retitle(process);
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
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);

            // 点完就关 + 删掉一次性 profile：不留后台浏览器，也不在磁盘上留站点凭证
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { LpLog.Debug($"closing verification browser failed: {ex.GetType().Name}", "links.metadata"); }
            VerificationBrowserHost.DeleteProfile(profile);
        }
    }

    /// <summary>一轮页面状态：只够回答"到没到真站点"。</summary>
    private sealed record PageReading(string Title, string Description, string Url, string Ready);

    /// <summary>
    /// 每轮只回答"到没到真站点"：<b>不夹带任何网络请求</b>，保证轮询是毫秒级。
    /// 之前把图标下载塞进这段脚本，导致页面早就变成真站点了、窗口却还在等那次 fetch。
    /// </summary>
    private static async Task<PageReading?> ReadPageAsync(CdpSession session, CancellationToken ct)
    {
        const string script = """
            (() => {
              const meta = s => { const e = document.querySelector(s); return e ? (e.content || '') : ''; };
              return JSON.stringify({
                t: document.title || '',
                d: meta('meta[name="description"]') || meta('meta[property="og:description"]'),
                h: location.href || '',
                r: document.readyState || ''
              });
            })()
            """;

        var encoded = await session.EvaluateAsync(script, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(encoded)) return null;

        using var inner = JsonDocument.Parse(encoded);
        return new PageReading(
            inner.RootElement.GetProperty("t").GetString() ?? string.Empty,
            inner.RootElement.GetProperty("d").GetString() ?? string.Empty,
            inner.RootElement.GetProperty("h").GetString() ?? string.Empty,
            inner.RootElement.GetProperty("r").GetString() ?? string.Empty);
    }

    /// <summary>
    /// 取图标并落进缓存，返回可写进链接的图标地址（取不到返回 <c>null</c>）。
    /// </summary>
    private static async Task<string?> GrabIconAsync(CdpSession session, Uri target, CancellationToken ct)
    {
        try
        {
            var captured = await CaptureIconAsync(session, target, ct).ConfigureAwait(false);
            if (captured is not { } icon) return null;

            if (!await FaviconCache.StoreBytesAsync(icon.Url, icon.Bytes, ct).ConfigureAwait(false))
            {
                LpLog.Warn($"icon bytes could not be turned into an image ({icon.Bytes.Length} bytes): {target.Host}",
                    null, category: "links.metadata");
                return null;
            }

            LpLog.Info($"icon captured through the browser network stack ({icon.Bytes.Length} bytes): {target.Host}",
                category: "links.metadata");
            return icon.Url;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"icon capture failed: {target.Host}", ex, category: "links.metadata");
            return null;
        }
    }

    /// <summary>
    /// 按页面声明的顺序逐个取图标，取到第一张能用的就停（浏览器自己也就是这么干的：
    /// 声明了多张时，能解码的那张才算数）。
    /// <para>
    /// 取的方式是<b>浏览器网络栈</b>（<see cref="CdpSession.LoadResourceAsync"/>），不是页面内 <c>fetch</c>：
    /// 后者在严格 CSP 的站点上会直接抛 <c>SecurityError</c>（实测踩过），而且拿不到被 CSP 挡住的资源。
    /// </para>
    /// </summary>
    private static async Task<(string Url, byte[] Bytes)?> CaptureIconAsync(
        CdpSession session, Uri target, CancellationToken ct)
    {
        var candidates = await IconCandidatesAsync(session, target, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            LpLog.Warn($"no icon declared and the site root has none: {target.Host}", null, category: "links.metadata");
            return null;
        }

        var rejected = new List<string>();
        foreach (var candidate in candidates)
        {
            var (status, bytes) = await session.LoadResourceAsync(candidate, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300 && bytes is { Length: > 0 } && LooksLikeIcon(bytes))
                return (candidate, bytes);

            rejected.Add($"{ShortPath(candidate)} [{status}{(bytes is { Length: > 0 } ? ", not an image" : string.Empty)}]");
        }

        LpLog.Warn($"icon fetch failed at {target.Host}: {string.Join(", ", rejected)}",
            null, category: "links.metadata");
        return null;
    }

    /// <summary>
    /// 候选图标地址 = 页面声明的全部（按文档顺序，跳过 <c>data:</c> 空占位）+ 站点根 <c>/favicon.ico</c>。
    /// <para>
    /// <b>不能只取第一个声明</b>：不少站点第一条写的是 <c>data:image/x-icon;</c> 这种空占位
    /// （实测某文档站就是这样，真正的 SVG 在后面一条），照"第一个"取会拿到一张空图。
    /// </para>
    /// </summary>
    private static async Task<List<string>> IconCandidatesAsync(
        CdpSession session, Uri target, CancellationToken ct)
    {
        const string script = """
            (() => {
              const out = [];
              const links = document.querySelectorAll('link[rel~="icon"],link[rel="apple-touch-icon"]');
              for (const l of links) {
                const raw = (l.getAttribute('href') || '').trim();
                if (!raw || raw.startsWith('data:')) continue;
                // 拼不出绝对地址就跳过这一条声明：候选列表少一条不影响别的，也不该让整段失败
                try { out.push(new URL(raw, location.href).href); } catch (e) { /* 跳过这一条 */ }
              }
              return JSON.stringify(out);
            })()
            """;

        var candidates = new List<string>();
        var encoded = await session.EvaluateAsync(script, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(encoded))
        {
            using var doc = JsonDocument.Parse(encoded);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var href = item.GetString();
                    if (href is not null && href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(href);
                }
            }
        }

        candidates.Add(new Uri(target, "/favicon.ico").AbsoluteUri);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>拿到的字节像不像一张图（SVG 源码或常见位图文件头）——像才有必要交给落盘那一步。</summary>
    private static bool LooksLikeIcon(byte[] bytes)
    {
        if (bytes.Length < 8) return false;

        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 64)).TrimStart();
        if (head.StartsWith('<')) return true;   // SVG / XML 原文

        return bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => true,   // PNG
            [0xFF, 0xD8, 0xFF, ..] => true,         // JPEG
            [0x47, 0x49, 0x46, 0x38, ..] => true,   // GIF8
            [0x42, 0x4D, ..] => true,               // BMP
            [0x00, 0x00, 0x01, 0x00, ..] => true,   // ICO
            [0x00, 0x00, 0x02, 0x00, ..] => true,   // CUR
            [0x52, 0x49, 0x46, 0x46, ..] => true,   // RIFF（WEBP）
            _ => false,
        };
    }

    /// <summary>日志里只留路径，别把整条查询串带进来。</summary>
    private static string ShortPath(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

    /// <summary>
    /// "已经到了真站点、可以收工"的判据（三条同时成立）：
    /// ① 标题非空且不是挑战页标题；② 文档 <c>readyState=complete</c>（别在半渲染的中间态抓）；
    /// ③ 地址仍在原站点（挑战页会带 <c>?__cf_chl_rt_tk=</c> 之类的参数，放行后会回到干净的地址）。
    /// <para>
    /// 这里不看"等了多久"：窗口的唯一用途就是取这三样，拿到就退，一秒都不多留。
    /// </para>
    /// </summary>
    private static bool IsRealPage(PageReading? reading, Uri target)
        => reading is { Ready: "complete", Title.Length: > 0 }
           && !IsChallengeTitle(reading.Title)
           && reading.Url.Contains(target.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsChallengeTitle(string title)
        => ChallengeTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
}

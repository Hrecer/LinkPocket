using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.Services;

/// <summary>
/// 人机验证窗口的"宿主"部分：找一个真实浏览器、起一个独立进程、管它的临时 profile 与窗口标题。
/// </summary>
/// <remarks>
/// 与 <see cref="HumanVerificationBrowser"/> 的分工：这里只管"把浏览器拉起来、用完收拾干净"，
/// 判断页面到没到真站点、怎么读标题图标那些事都在那边。分开是为了两边都能读得完——
/// 一个类把启动参数、profile 生命周期、窗口标题和协议读页全塞进去，早就超过单类 400 行的硬杠了。
/// </remarks>
internal static class VerificationBrowserHost
{
    /// <summary>
    /// 浏览器启动参数：<c>--app</c> 让它成为一个"只有页面"的独立窗口（无地址栏 / 无标签页）；
    /// 固定小窗尺寸并给一个偏上的位置，避免遮住整个桌面；<b>不加任何置顶参数</b>。
    /// 窗口越小，验证框占页面的比例越大、越聚焦；520×390 比原 800×600 宽/高各小约 35%，
    /// 仍容得下挑战页的居中布局（Turnstile 框本身约 300×65）。
    /// </summary>
    private const string Arguments =
        "--no-first-run --no-default-browser-check --disable-sync --disable-translate --disable-extensions "
        + "--remote-allow-origins=* --window-size=520,390 --window-position=240,120 "
        // 关掉浏览器自带的浮层：账号同步提示（会露出本机微软账号）、翻译气泡、身份浮标——
        // 它们会盖住验证框，且与"只用来过验证"的用途无关
        + "--disable-features=Translate,TranslateUI,msEdgeTranslate,EdgeTranslate,MediaRouter,"
        + "msEdgeSyncPromo,EdgeSyncPromo,msEdgeIdentityFloatingButton";

    /// <summary>一次性 profile 的落点（应用自己的本地目录，不往系统临时目录堆）。</summary>
    private static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkPocket");

    /// <summary>等调试端点时的轮询间隔（与编排侧同节奏：目标一出现就往下走）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>调试协议就绪的最长等待：浏览器冷启动一般 1–2 s，给足 20 s。</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>一条会话的整体时长上限（用户在窗口里停留多久都算正常，所以给得很宽）。</summary>
    private static readonly TimeSpan SessionBudget = TimeSpan.FromMinutes(10);

    private static bool _cleaned;

    /// <summary>本机真实浏览器（Edge 优先：Windows 自带，用户环境里一定有）。</summary>
    public static string? FindBrowser()
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
        ];

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    /// <summary>起浏览器：新实例 + 一次性 profile + <c>--app</c> 单页窗口。</summary>
    public static Process? Start(string exe, string profile, int port, Uri target)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profile}\" --remote-debugging-port={port} "
                            + $"--app=\"{target.AbsoluteUri}\" {Arguments}",
            };

            var process = Process.Start(psi);
            if (process is not null) AssignToKillOnCloseJob(process);
            return process;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"starting the verification browser failed: {target.Host}", ex, category: "links.metadata");
            return null;
        }
    }

    /// <summary>
    /// 把浏览器进程放进一个"作业对象"，并设置 <c>KILL_ON_JOB_CLOSE</c>：
    /// 本进程无论正常退出、被杀、还是崩溃，**系统都会把这个浏览器连同它的子进程一起收掉**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：只靠 <c>finally</c> 里的 <c>Kill</c> 只能覆盖"我们还有机会跑代码"的情况。
    /// 进程被任务管理器结束、或我们自己崩在某个 finally 之前，那个验证窗口就会留在桌面上没人管
    /// （实测踩过：一个窗口挂在屏幕上，关掉主程序它还在）。
    /// </para>
    /// <para>
    /// 作业对象由内核维护：句柄关闭即触发，不依赖我们的托管代码。子进程（Edge 的渲染进程）
    /// 默认继承作业，所以整棵进程树都会被回收。分配失败不算错——<c>finally</c> 那条路仍然有效。
    /// </para>
    /// </remarks>
    private static void AssignToKillOnCloseJob(Process process)
    {
        try
        {
            var job = _jobHandle ??= CreateJob();
            if (job == IntPtr.Zero) return;

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                LpLog.Debug($"assigning the verification browser to a job failed: {Marshal.GetLastWin32Error()}",
                    "links.metadata");
            }
        }
        catch (Exception ex)
        {
            // 作业分配只是"崩溃也能回收"的加固，失败不影响本次验证
            LpLog.Debug($"job assignment skipped: {ex.GetType().Name}", "links.metadata");
        }
    }

    private static IntPtr? _jobHandle;

    private static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                LpLog.Debug($"configuring the verification job failed: {Marshal.GetLastWin32Error()}", "links.metadata");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>
    /// 清掉历史残留：① 上次进程崩溃留下的一次性 profile（正常路径会自己删）；
    /// ② 旧版本用的固定目录 <c>verify-profile</c>——那一版会把站点凭证长期留在本机，
    /// 与现在的隐私口径不符，发现就删。只做一次。
    /// </summary>
    public static void CleanupPreviousProfiles()
    {
        if (_cleaned) return;
        _cleaned = true;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProfileRoot, "verify-*"))
                TryDelete(dir);
        }
        catch (Exception ex)
        {
            LpLog.Debug($"scanning stale verification profiles failed: {ex.GetType().Name}", "links.metadata");
        }

        TryDelete(Path.Combine(ProfileRoot, "verify-profile"));
    }

    /// <summary>
    /// 每次验证一个**一次性 profile**：验证完就删，磁盘上不留站点凭证。
    /// <para>
    /// 这取代了原先"固定目录复用凭证"的做法——固定目录会在本机长期保存站点的放行 cookie，
    /// 与"不留痕迹"的隐私口径冲突。代价说清楚：**没有可复用的凭证，站点要求人点时每次都得点一次**。
    /// </para>
    /// </summary>
    public static string CreateEphemeralProfile()
    {
        var dir = Path.Combine(ProfileRoot, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>删掉一次性 profile（浏览器刚退出时文件可能还被占用，重试几次）。</summary>
    public static void DeleteProfile(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                LpLog.Debug($"deleting verification profile failed (attempt {attempt + 1}): {ex.GetType().Name}",
                    "links.metadata");
                Thread.Sleep(200);
            }
        }
    }

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
    public static void Retitle(Process process)
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

    /// <summary>找一个空闲端口给调试端点用（浏览器自己会监听它）。</summary>
    public static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            LpLog.Debug($"deleting a stale verification profile failed: {ex.GetType().Name}", "links.metadata");
        }
    }
    /// <summary>等调试端点起来，并且已经有一个 http 页面目标可以连。</summary>
    public static async Task<bool> WaitForPageAsync(int port, Process process, CancellationToken ct)
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

    /// <summary>连上页面目标；目标还在切换时返回 <c>null</c>（下一轮再试）。</summary>
    public static async Task<CdpSession?> ConnectAsync(int port, CancellationToken ct)
    {
        var page = await FindPageAsync(port, ct).ConfigureAwait(false);
        if (page is null || string.IsNullOrEmpty(page.WebSocketDebuggerUrl)) return null;

        try
        {
            return await CdpSession.ConnectAsync(page.WebSocketDebuggerUrl, SessionBudget, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LpLog.Debug($"connecting to the verification page failed: {ex.GetType().Name}", "links.metadata");
            return null;
        }
    }

    /// <summary>调试协议里的页面目标（<c>--app</c> 只有一个）。</summary>
    public sealed record PageTarget(string Title, string WebSocketDebuggerUrl);

    public static async Task<PageTarget?> FindPageAsync(int port, CancellationToken ct)
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
    /// <para>
    /// <b>必须按 <c>Content-Length</c> 读满就停</b>：调试端口发完响应不会关连接（写了 <c>Connection: close</c> 也不关），
    /// 用 "读到连接结束" 会一直挂到超时——那正是"窗口开着不退、也抓不到东西"的根因。
    /// </para>
    /// </summary>
    private static async Task<string?> GetTargetListAsync(int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);

            await using var stream = tcp.GetStream();
            var request = $"GET /json/list HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);

            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            var bodyStart = -1;
            var contentLength = -1;
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }   // 读到够用的那一刻超时也无妨
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);

                if (bodyStart >= 0)
                {
                    if (contentLength >= 0 && buffer.Length - bodyStart >= contentLength) break;
                    continue;
                }

                var head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                var split = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (split < 0) continue;

                bodyStart = split + 4;
                foreach (var line in head[..split].Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line[15..].Trim(), out var declared))
                    {
                        contentLength = declared;
                    }
                }
            }

            if (bodyStart < 0 || buffer.Length <= bodyStart) return null;
            return Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, (int)buffer.Length - bodyStart);
        }
        catch (Exception ex)
        {
            // 端口还没起来 / 目标正在切换：都属于"再等一轮"，不是失败
            LpLog.Debug($"debugger not ready on {port}: {ex.GetType().Name}", "links.metadata");
            return null;
        }
    }

}

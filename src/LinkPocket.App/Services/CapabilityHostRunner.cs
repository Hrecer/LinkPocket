using System.Diagnostics;
using System.IO;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 按需能力宿主：CLI / MCP 真正需要浏览器能力时，用同一个 <c>LinkPocket.exe</c> 的
/// <c>--capability-host</c> 模式启动它。不建主窗口、不装配引擎、不开数据库，
/// 只把本进程已有的浏览器渲染、SVG 光栅化、人机验证窗口经同一条能力桥借出去。
/// <para>每库只有一个能力提供者；拿不到提供者锁就退出。空闲 60 秒或父进程退出后自行结束。</para>
/// </summary>
internal sealed class CapabilityHostRunner : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ParentGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly PageRenderer _renderer;
    private readonly SvgIconRasterizer _svgIcons;
    private readonly HumanVerificationBrowser _humanVerify = new();
    private readonly CapabilityBridgeServer _bridge;
    private readonly CancellationTokenSource _cts = new();
    private readonly int? _parentPid;
    private Task? _watch;

    public CapabilityHostRunner(string databasePath, int? parentPid)
    {
        _parentPid = parentPid;

        // 与完整桌面宿主分开的 profile：两个提供者即使短暂并存，也不会抢同一个 WebView2 user-data 目录。
        var profileRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkPocket", "capability-host");
        _renderer = new PageRenderer(Path.Combine(profileRoot, "page-profile"));
        _svgIcons = new SvgIconRasterizer(Path.Combine(profileRoot, "icon-profile"));
        _bridge = new CapabilityBridgeServer(
            databasePath, _renderer, _humanVerify, _svgIcons,
            url => AppHost.ConfirmExternalVerification(url, centerOnScreen: true));
    }

    public bool Start()
    {
        if (!_bridge.Start()) return false;

        _watch = Task.Run(WatchAsync);
        LpLog.Info("capability host ready", category: "app.capability");
        return true;
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var idle = DateTime.UtcNow - _bridge.LastActivityUtc;
                if (_bridge.ActiveRequests != 0) continue;

                if (idle > IdleTimeout)
                {
                    LpLog.Info($"capability host idle for {idle.TotalSeconds:0}s; exiting", "app.capability");
                    break;
                }

                if (idle > ParentGrace && !ParentAlive())
                {
                    LpLog.Info("capability host parent exited; exiting", "app.capability");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            LpLog.Warn("capability host watcher failed", ex, category: "app.capability");
        }

        if (!_cts.IsCancellationRequested) Shutdown();
    }

    private bool ParentAlive()
    {
        if (_parentPid is not { } pid) return true;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // 进程不存在
        }
        catch (Exception)
        {
            // 查询失败（权限/瞬态）不当作父进程已退出；交给空闲超时收尾
            return true;
        }
    }

    private static void Shutdown()
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
        app.Dispatcher.BeginInvoke(() => app.Shutdown());
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 已经取消 */ }

        _bridge.Dispose();
        _renderer.Dispose();
        _svgIcons.Dispose();
        _cts.Dispose();
    }
}

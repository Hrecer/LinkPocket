using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Composition;

/// <summary>
/// 无头宿主（CLI / MCP）的**能力桥客户端**：优先连接已经运行的能力提供者；没有提供者时，
/// 按需启动同一个 <c>LinkPocket.exe</c> 的 <c>--capability-host</c> 模式，再连接它。
/// <para>连接只会在真正需要渲染 / SVG / 人机验证时发生（由 <see cref="PageRenderHook.EnsureAsync"/>
/// 触发），普通查询不会拉起任何进程。连不上 = 能力不可用，由命令结果如实上报。</para>
/// </summary>
internal sealed class CapabilityBridgeClient : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(15);
    private const string AppExecutableEnvironmentVariable = "LINKPOCKET_APP_EXE";

    private readonly string _databasePath;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    private bool _connected;
    private bool _render;
    private bool _verify;
    private bool _svg;
    private DateTime _lastLaunchAttempt = DateTime.MinValue;

    public bool RenderAvailable => _connected && _render;
    public bool VerifyAvailable => _connected && _verify;
    public bool SvgAvailable => _connected && _svg;

    public CapabilityBridgeClient(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _pipeName = HostCapabilityBridge.PipeNameFor(_databasePath);
    }

    /// <summary>启动时的快速握手：桌面宿主已经开着就直接用，不必等第一次渲染。</summary>
    public bool TryConnectNow(int timeoutMs = 300)
    {
        if (_connected) return true;
        var response = SendAsync(_pipeName, "hello", null, timeoutMs, CancellationToken.None)
            .GetAwaiter().GetResult();
        return ApplyHello(response);
    }

    /// <summary>
    /// 确保有一个可用的能力提供者：先重试握手（桌面可能刚启动），再按需拉起能力宿主。
    /// 并发调用共享同一次装配；失败后 15 秒内不重复尝试，避免每个条目都等一次启动超时。
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (_connected) return true;

        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return true;

            var hello = await SendAsync(_pipeName, "hello", null,
                (int)HandshakeTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            if (ApplyHello(hello)) return true;

            if (DateTime.UtcNow - _lastLaunchAttempt < LaunchCooldown) return false;
            _lastLaunchAttempt = DateTime.UtcNow;

            var appExe = ResolveAppExecutable();
            if (appExe is null || !Environment.UserInteractive)
            {
                LpLog.Debug("capability host unavailable: no desktop executable or non-interactive session",
                    "host.capability");
                return false;
            }

            if (!TryStartHost(appExe)) return false;

            var deadline = DateTime.UtcNow + LaunchTimeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
                hello = await SendAsync(_pipeName, "hello", null, 500, ct).ConfigureAwait(false);
                if (ApplyHello(hello))
                {
                    LpLog.Info(
                        $"capability host attached (render={_render}, verify={_verify}, svg={_svg})",
                        category: "host.capability");
                    return true;
                }
            }

            LpLog.Warn("capability host did not answer within the launch budget", null, "host.capability");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LpLog.Debug($"capability ensure failed: {ex.GetType().Name}", "host.capability");
            return false;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    public async Task<RenderedPageMetadata?> RenderAsync(string url, CancellationToken ct)
    {
        if (!await EnsureAsync(ct).ConfigureAwait(false)) return null;
        var response = await SendAsync(_pipeName, "render", url, 20_000, ct).ConfigureAwait(false);
        if (response is null) MarkDisconnected();
        return ToMetadata(response);
    }

    public async Task<RenderedPageMetadata?> VerifyAsync(string url, CancellationToken ct)
    {
        if (!await EnsureAsync(ct).ConfigureAwait(false)) return null;
        var response = await SendAsync(_pipeName, "verify", url, 200_000, ct).ConfigureAwait(false);
        if (response is null) MarkDisconnected();
        return ToMetadata(response);
    }

    public async Task<byte[]?> RasterizeAsync(string url, CancellationToken ct)
    {
        if (!await EnsureAsync(ct).ConfigureAwait(false)) return null;
        var response = await SendAsync(_pipeName, "rasterize_svg", url, 15_000, ct).ConfigureAwait(false);
        if (response is null) MarkDisconnected();
        if (response is not { Ok: true } || string.IsNullOrWhiteSpace(response.PngBase64)) return null;
        try { return Convert.FromBase64String(response.PngBase64); }
        catch (FormatException) { return null; }
    }

    private bool ApplyHello(HostCapabilityBridge.Response? response)
    {
        if (response is not { Ok: true } || (!response.Render && !response.Verify && !response.Svg))
        {
            MarkDisconnected();
            return false;
        }

        _render = response.Render;
        _verify = response.Verify;
        _svg = response.Svg;
        _connected = true;
        return true;
    }

    private void MarkDisconnected()
    {
        _connected = false;
        _render = false;
        _verify = false;
        _svg = false;
    }

    /// <summary>
    /// 能力宿主 = 同一个程序根下的 <c>LinkPocket.exe</c>。开发布局里用
    /// <c>LINKPOCKET_APP_EXE</c> 指向构建产物；找不到就如实退回"无能力"。
    /// </summary>
    private static string? ResolveAppExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(AppExecutableEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        try
        {
            var candidate = Path.Combine(AppPaths.ProgramRoot, "LinkPocket.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 程序根不可读：按"没有能力宿主"处理
        }

        return null;
    }

    private bool TryStartHost(string appExe)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(appExe), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return false;

            var psi = new ProcessStartInfo(appExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(appExe) ?? AppPaths.ProgramRoot,
                Arguments = $"--capability-host --parent-pid {Environment.ProcessId}",
            };
            // 能力宿主只靠库路径派生管道名；显式给出解析后的绝对路径，别让它的发现规则再猜一次。
            psi.Environment[AppPaths.DatabaseEnvironmentVariable] = _databasePath;

            using var process = Process.Start(psi);
            if (process is null) return false;

            LpLog.Info($"capability host starting ({Path.GetFileName(appExe)})", category: "host.capability");
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Warn("starting the capability host failed", ex, category: "host.capability");
            return false;
        }
    }

    private static RenderedPageMetadata? ToMetadata(HostCapabilityBridge.Response? response)
    {
        if (response is not { Ok: true }) return null;
        if (string.IsNullOrWhiteSpace(response.Title) && string.IsNullOrWhiteSpace(response.Description))
            return null;
        return new RenderedPageMetadata(response.Title, response.Description, response.FaviconUrl);
    }

    private static async Task<HostCapabilityBridge.Response?> SendAsync(
        string pipeName, string operation, string? url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Math.Min(timeoutMs, 5_000), ct).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var request = new HostCapabilityBridge.Request(
                HostCapabilityBridge.Version, operation, url, timeoutMs);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, HostCapabilityBridge.JsonOptions))
                .ConfigureAwait(false);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeoutMs);
            var line = await reader.ReadLineAsync(budget.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return null;

            var response = JsonSerializer.Deserialize<HostCapabilityBridge.Response>(
                line, HostCapabilityBridge.JsonOptions);
            if (response is { Ok: false })
                LpLog.Debug($"capability bridge {operation}: {response.Error}/{response.Reason}", "host.capability");
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 调用方主动取消：如实上抛，别伪装成"能力不可用"
        }
        catch (Exception ex)
        {
            LpLog.Debug($"capability bridge {operation} unavailable: {ex.GetType().Name}", "host.capability");
            return null;
        }
    }

    public void Dispose()
    {
        // 每次请求一条短连接；能力宿主由空闲/父进程存活看护，不在客户端退出时强杀
        //（它可能正被另一个 CLI / MCP 使用）。
    }
}

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 能力提供者的**服务端**：把本进程已有的浏览器渲染、SVG 光栅化、人机验证窗口
/// 经本机命名管道借给 CLI / MCP（无头宿主）。完整桌面应用与按需能力宿主共用这一份实现；
/// 同一数据库 = 同一条管道；每库一个提供者锁；只接受当前用户。
/// <para>人机验证是唯一会开真实窗口的操作：服务端先调用宿主提供的确认回调（界面弹确认框），
/// 用户拒绝或没有界面时如实返回 <c>denied</c>，绝不静默开窗。</para>
/// </summary>
internal sealed class CapabilityBridgeServer : IDisposable
{
    private readonly string _databasePath;
    private readonly string _pipeName;
    private readonly PageRenderer _renderer;
    private readonly HumanVerificationBrowser _humanVerify;
    private readonly SvgIconRasterizer _svgIcons;
    private readonly Func<string, bool> _confirmVerify;
    private readonly SemaphoreSlim _verifyGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Mutex? _providerMutex;
    private bool _ownsProvider;
    private int _activeRequests;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public CapabilityBridgeServer(
        string databasePath,
        PageRenderer renderer,
        HumanVerificationBrowser humanVerify,
        SvgIconRasterizer svgIcons,
        Func<string, bool> confirmVerify)
    {
        _databasePath = databasePath;
        _pipeName = HostCapabilityBridge.PipeNameFor(databasePath);
        _renderer = renderer;
        _humanVerify = humanVerify;
        _svgIcons = svgIcons;
        _confirmVerify = confirmVerify;
    }

    /// <summary>拿到本库唯一的提供者锁才开管道；拿不到 = 已有桌面宿主或另一个能力宿主在服务。</summary>
    public bool Start()
    {
        if (!TryAcquireProvider()) return false;

        _acceptLoop = Task.Run(AcceptLoopAsync);
        LpLog.Info($"capability bridge listening ({_pipeName})", category: "app.capability");
        return true;
    }

    /// <summary>正在处理中的请求数（能力宿主据此判断能否空闲退出）。</summary>
    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    public DateTime LastActivityUtc
        => new(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    private bool TryAcquireProvider()
    {
        try
        {
            var mutex = new Mutex(false, HostCapabilityBridge.ProviderMutexNameFor(_databasePath));
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个提供者崩溃了：所有权归当前进程，继续服务。
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                LpLog.Info("another capability provider is active; this host yields", category: "app.capability");
                return false;
            }

            _providerMutex = mutex;
            _ownsProvider = true;
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Warn("capability provider mutex failed", ex, category: "app.capability");
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(pipe);
                pipe = null;   // 所有权已交给连接处理器
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                LpLog.Warn("capability bridge accept failed", ex, category: "app.capability");
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            Interlocked.Increment(ref _activeRequests);
            Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;

                HostCapabilityBridge.Request? request;
                try
                {
                    request = JsonSerializer.Deserialize<HostCapabilityBridge.Request>(
                        line, HostCapabilityBridge.JsonOptions);
                }
                catch (JsonException)
                {
                    request = null;
                }

                HostCapabilityBridge.Response response;
                if (request is null || request.Version != HostCapabilityBridge.Version ||
                    string.IsNullOrWhiteSpace(request.Operation))
                {
                    response = new HostCapabilityBridge.Response(
                        false, "bad_request", "protocol/version", null, null, null, null,
                        false, false, false, 0);
                }
                else
                {
                    response = await DispatchAsync(request).ConfigureAwait(false);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, HostCapabilityBridge.JsonOptions))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 宿主退出 / 对端断开：不是错误
            }
            catch (Exception ex)
            {
                LpLog.Warn("capability bridge connection failed", ex, category: "app.capability");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            }
        }
    }

    private async Task<HostCapabilityBridge.Response> DispatchAsync(HostCapabilityBridge.Request request)
    {
        var started = Stopwatch.GetTimestamp();
        var timeout = Math.Clamp(request.TimeoutMs <= 0 ? 20_000 : request.TimeoutMs, 1_000, 300_000);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        budget.CancelAfter(timeout);

        try
        {
            switch (request.Operation)
            {
                case "hello":
                    // 报价必须来自真实探测：没装 WebView2 / 没有真实浏览器时不能让调用方以为能用。
                    var renderReady = PageRenderer.IsRuntimeAvailable();
                    var verifyReady = VerificationBrowserHost.FindBrowser() is not null;
                    return new HostCapabilityBridge.Response(
                        true, null, null, null, null, null, null,
                        Render: renderReady, Verify: verifyReady, Svg: renderReady, ElapsedMs: Elapsed(started));

                case "render":
                    if (string.IsNullOrWhiteSpace(request.Url))
                        return Error("bad_request", "url", started);
                    if (!PageRenderer.IsRuntimeAvailable())
                        return Error("unavailable", "webview2_runtime", started);
                    if (!await IsAllowedTargetAsync(request.Url, budget.Token).ConfigureAwait(false))
                        return Error("blocked_target", "loopback_or_link_local", started);
                    var rendered = await _renderer.RenderAsync(request.Url, budget.Token).ConfigureAwait(false);
                    return rendered is null
                        ? Error("render_failed", "no_metadata", started)
                        : new HostCapabilityBridge.Response(
                            true, null, null, rendered.Title, rendered.Description, rendered.FaviconUrl, null,
                            false, false, false, Elapsed(started));

                case "rasterize_svg":
                    if (string.IsNullOrWhiteSpace(request.Url))
                        return Error("bad_request", "url", started);
                    if (!PageRenderer.IsRuntimeAvailable())
                        return Error("unavailable", "webview2_runtime", started);
                    if (!await IsAllowedTargetAsync(request.Url, budget.Token).ConfigureAwait(false))
                        return Error("blocked_target", "loopback_or_link_local", started);
                    var png = await _svgIcons.RasterizeAsync(request.Url, budget.Token).ConfigureAwait(false);
                    return png is not { Length: > 0 }
                        ? Error("rasterize_failed", "no_png", started)
                        : new HostCapabilityBridge.Response(
                            true, null, null, null, null, null, Convert.ToBase64String(png),
                            false, false, false, Elapsed(started));

                case "verify":
                    if (string.IsNullOrWhiteSpace(request.Url))
                        return Error("bad_request", "url", started);
                    if (VerificationBrowserHost.FindBrowser() is null)
                        return Error("unavailable", "no_browser", started);
                    if (!await IsAllowedTargetAsync(request.Url, budget.Token).ConfigureAwait(false))
                        return Error("blocked_target", "loopback_or_link_local", started);
                    if (!_verifyGate.Wait(0))
                        return Error("busy", "another_verification", started);
                    try
                    {
                        if (!_confirmVerify(request.Url))
                            return Error("denied", "user_declined", started);

                        var verified = await _humanVerify.VerifyAsync(request.Url, budget.Token).ConfigureAwait(false);
                        return verified is null
                            ? Error("verify_failed", "not_completed", started)
                            : new HostCapabilityBridge.Response(
                                true, null, null, verified.Title, verified.Description, verified.FaviconUrl, null,
                                false, false, false, Elapsed(started));
                    }
                    finally
                    {
                        _verifyGate.Release();
                    }

                default:
                    return Error("unknown_operation", request.Operation, started);
            }
        }
        catch (OperationCanceledException)
        {
            return Error("timeout", "cancelled", started);
        }
        catch (Exception ex)
        {
            LpLog.Warn($"capability bridge {request.Operation} failed", ex, category: "app.capability");
            return Error("failed", ex.GetType().Name, started);
        }
    }

    private static HostCapabilityBridge.Response Error(string error, string? reason, long started)
        => new(false, error, reason, null, null, null, null, false, false, false, Elapsed(started));

    private static async Task<bool> IsAllowedTargetAsync(string url, CancellationToken ct)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && await TargetGuard.IsAllowedAsync(uri, ct).ConfigureAwait(false);

    private static long Elapsed(long started)
        => (long)Math.Min(long.MaxValue,
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 已经取消 */ }

        if (_ownsProvider && _providerMutex is not null)
        {
            try { _providerMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 已不是持有者（异常/重复释放） */ }
            _ownsProvider = false;
        }
        _providerMutex?.Dispose();
        _providerMutex = null;
    }
}

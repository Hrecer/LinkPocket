using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LinkPocket.Services;

/// <summary>
/// 一条调试协议（CDP）会话：按 id 多路复用请求，事件帧直接忽略。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一个类</b>：原来每读一次页面就"连一次、发一条、读一帧、断开"，两处硬伤——
/// ① 长消息被拆成多帧时只读了第一帧（<c>EndOfMessage</c> 没看），偶尔解析失败；
/// ② 一次会话只能问一件事，而"取图标"要连着问三件（帧树 → 取资源 → 读流）。
/// </para>
/// <para>
/// 还有一条更关键的能力：<see cref="LoadResourceAsync"/> 走的是 <c>Network.loadNetworkResource</c>，
/// 也就是<b>浏览器的网络栈</b>，不在页面里——因此<b>不受页面 CSP 限制</b>，并且带着这个 profile 的 cookie。
/// 页面内 <c>fetch</c> 在严格 CSP 的站点上会直接抛 <c>SecurityError</c>（实测踩过），所以取图标必须走这条路。
/// </para>
/// </remarks>
internal sealed class CdpSession : IAsyncDisposable
{
    /// <summary>单条消息上限：图标、页面脚本的返回都在这个量级以内，超过即视为异常。</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>取资源时单次读取的块大小。</summary>
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>取资源的整体上限（图标文件不该有这么大，超过就是拿错了）。</summary>
    private const int MaxResourceBytes = 2 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _budget;
    private int _nextId = 1;

    /// <summary>主框架 id：取资源要用它，而它对一个页面目标是恒定的，问一次就够。</summary>
    private string? _frameId;

    private CdpSession(TimeSpan budget) => _budget = new CancellationTokenSource(budget);

    /// <summary>连上某个页面目标的调试端点；连不上抛出去（调用方决定记什么日志）。</summary>
    public static async Task<CdpSession> ConnectAsync(string webSocketUrl, TimeSpan budget, CancellationToken ct)
    {
        var session = new CdpSession(budget);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session._budget.Token);
            await session._socket.ConnectAsync(new Uri(webSocketUrl), linked.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            // 连接失败时把已建的东西放掉再抛：这里不吞异常，只是不让 socket 泄漏
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>发一条命令并等它自己的响应（事件帧跳过，长消息按帧拼完整再解析）。</summary>
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = _nextId++;
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _budget.Token);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, linked.Token)
            .ConfigureAwait(false);

        var buffer = new byte[256 * 1024];
        var payload = new MemoryStream();
        while (!linked.IsCancellationRequested)
        {
            var frame = await _socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"CDP closed while waiting for {method}");

            payload.Write(buffer, 0, frame.Count);
            if (payload.Length > MaxMessageBytes)
                throw new InvalidOperationException($"CDP message too large for {method}");
            if (!frame.EndOfMessage) continue;   // 长消息会拆成多帧，拼完再解析

            using var doc = JsonDocument.Parse(payload.ToArray());
            payload.SetLength(0);

            if (!doc.RootElement.TryGetProperty("id", out var idNode) || idNode.GetInt32() != id) continue;
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"{method} failed: {error}");

            return doc.RootElement.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }

        throw new OperationCanceledException($"{method} timed out");
    }

    /// <summary>求值一段脚本，返回它的值（字符串）；拿不到返回 <c>null</c>。</summary>
    public async Task<string?> EvaluateAsync(string script, TimeSpan budget, CancellationToken ct)
    {
        using var scoped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scoped.CancelAfter(budget);

        var result = await CallAsync("Runtime.evaluate", new
        {
            expression = script,
            returnByValue = true,
            awaitPromise = true,
        }, scoped.Token).ConfigureAwait(false);

        return result.TryGetProperty("result", out var inner) && inner.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// 走<b>浏览器网络栈</b>取一个资源，返回状态码与原始字节（取不到字节时为 <c>null</c>）。
    /// <para>
    /// 这是"取图标"的正路：它不在页面里，所以不受页面 CSP 约束；<c>includeCredentials</c> 让它带上
    /// 这个 profile 的 cookie（反爬站点靠这条通行凭证才放行静态资源）。
    /// </para>
    /// </summary>
    public async Task<(int Status, byte[]? Bytes)> LoadResourceAsync(string url, CancellationToken ct)
    {
        // 单次取资源自带 10 s 预算：图标是个小文件，卡住只会让验证窗口挂在那里不关
        using var scoped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scoped.CancelAfter(TimeSpan.FromSeconds(10));
        ct = scoped.Token;

        if (_frameId is null)
        {
            var tree = await CallAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
            _frameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        }

        var loaded = await CallAsync("Network.loadNetworkResource", new
        {
            frameId = _frameId,
            url,
            options = new { disableCache = false, includeCredentials = true },
        }, ct).ConfigureAwait(false);

        var resource = loaded.GetProperty("resource");
        var status = resource.TryGetProperty("httpStatusCode", out var statusNode) ? statusNode.GetInt32() : -1;

        if (!resource.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return (status, null);
        if (!resource.TryGetProperty("stream", out var streamNode) || streamNode.ValueKind != JsonValueKind.String)
            return (status, null);

        var handle = streamNode.GetString();
        if (string.IsNullOrEmpty(handle)) return (status, null);

        var bytes = new MemoryStream();
        while (bytes.Length < MaxResourceBytes)
        {
            var read = await CallAsync("IO.read", new { handle, size = ReadChunkBytes }, ct).ConfigureAwait(false);
            var data = read.TryGetProperty("data", out var dataNode) ? dataNode.GetString() ?? string.Empty : string.Empty;

            if (data.Length > 0)
            {
                var base64 = read.TryGetProperty("base64Encoded", out var flag) && flag.GetBoolean();
                var chunk = base64 ? Convert.FromBase64String(data) : Encoding.UTF8.GetBytes(data);
                bytes.Write(chunk, 0, chunk.Length);
            }

            if (!read.TryGetProperty("eof", out var eof) || eof.GetBoolean()) break;
        }

        return (status, bytes.Length == 0 ? null : bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _budget.Cancel();
        _budget.Dispose();

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 关闭握手失败无所谓：浏览器进程马上就被杀掉了，这里只求别把异常抛给调用方
        }

        _socket.Dispose();
    }
}

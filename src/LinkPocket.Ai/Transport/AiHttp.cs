using System.Net;
using System.Text;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>一次 HTTP 调用（方法 + 地址 + 头 + JSON 体）。</summary>
public sealed record AiHttpRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? BodyJson);

/// <summary>响应（状态码 + 体；体已按上限截断读取）。</summary>
public sealed record AiHttpResponse(int Status, string Body);

/// <summary>
/// LLM HTTP 的**注入缝**：生产只装配 <see cref="HttpAiTransport"/>（全进程唯一实现），
/// 测试用假传输层（CI **不打真实网络**）。
/// </summary>
public interface IAiHttpTransport
{
    Task<AiHttpResponse> SendAsync(AiHttpRequest request, CancellationToken ct = default);

    /// <summary>流式：逐行吐**原始 SSE 行**（含 <c>event:</c> / <c>data:</c> / 空分隔行），协议解析归适配器。</summary>
    IAsyncEnumerable<string> SendStreamLinesAsync(AiHttpRequest request, CancellationToken ct = default);
}

/// <summary>
/// LLM HTTP 的**唯一实现**：池化 HttpClient（长生存期）+ 逐请求超时 + **并发闸 = 1**
/// （任何时刻在飞的模型请求不超过 1 个）+ 响应体上限 + 退避重试（仅可重试错误，过程写日志不静默）
/// + 状态码 → <c>LP.AI.*</c> 映射。Authorization / Key 永不进日志。
/// </summary>
public sealed class HttpAiTransport : IAiHttpTransport, IDisposable
{
    /// <summary>响应体上限（8MB；超限截断，超长响应属异常）。</summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Shared = CreateClient();
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;

    /// <param name="handler">测试注入的桩处理器（生产传 null = 用全进程唯一的池化 HttpClient）。</param>
    /// <param name="timeout">逐请求总超时（缺省 120s；流式按整个流计）。</param>
    /// <param name="maxAttempts">可重试错误的最大尝试次数（缺省 3，含首次）。</param>
    public HttpAiTransport(HttpMessageHandler? handler = null, TimeSpan? timeout = null, int maxAttempts = 3)
    {
        _client = handler is null
            ? Shared
            : new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_client, Shared)) _client.Dispose();
        _gate.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AiHttpResponse> SendAsync(AiHttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await SendOnceAsync(request, ct);
                }
                catch (AiException ex) when (ex.Error.Retryable && attempt < _maxAttempts)
                {
                    LpLog.Warn($"llm request retry {attempt}/{_maxAttempts - 1}: {ex.Error.Code}",
                        category: "ai.transport");
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)), ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<string> SendStreamLinesAsync(AiHttpRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            using var message = Build(request);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiException(AiErrors.Of(AiErrors.ProviderUnreachable, "stream request timed out"));
            }
            catch (HttpRequestException ex)
            {
                throw new AiException(AiErrors.Of(AiErrors.ProviderUnreachable, $"network error: {ex.Message}"));
            }

            try
            {
                if (!response.IsSuccessStatusCode)
                    throw MapError(response.StatusCode, await ReadCappedAsync(response, cts.Token));

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new AiException(AiErrors.Of(AiErrors.ProviderUnreachable, "stream idle timeout"));
                    }
                    if (line is null) yield break;
                    yield return line;
                }
            }
            finally
            {
                response.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AiHttpResponse> SendOnceAsync(AiHttpRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var message = Build(request);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException(AiErrors.Of(AiErrors.ProviderUnreachable, "request timed out"));
        }
        catch (AiException)
        {
            throw;   // 已带稳定码（如地址非法）：原样上抛，不再包一层网络错
        }
        catch (HttpRequestException ex)
        {
            throw new AiException(AiErrors.Of(AiErrors.ProviderUnreachable, $"network error: {ex.Message}"));
        }

        using (response)
        {
            var text = await ReadCappedAsync(response, cts.Token);
            if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, text);
            return new AiHttpResponse((int)response.StatusCode, text);
        }
    }

    private static HttpRequestMessage Build(AiHttpRequest request)
    {
        // Base URL 未填 / 非法 → 这里必须**自己抛 AiException**：HttpRequestException 之外的异常
        // （new Uri 抛 UriFormatException）会穿透到界面的 `async void` 处理器，被兜底成"界面异常"弹窗。
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AiException(AiErrors.Of(AiErrors.InvalidInput,
                $"provider base url is missing or not a valid http(s) url: {request.Url}"));

        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.BodyJson is { } body)
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        return message;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (ms.Length < MaxResponseBytes)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>状态码 → 稳定错误码（**绝不把原始报文上屏**；报文只进 details 供日志/排障）。</summary>
    private static AiException MapError(HttpStatusCode status, string body)
    {
        var statusCode = (int)status;
        var code = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiErrors.AuthFailed,
            HttpStatusCode.NotFound => AiErrors.ModelNotFound,
            HttpStatusCode.TooManyRequests => AiErrors.UpstreamRateLimited,
            HttpStatusCode.RequestTimeout => AiErrors.ProviderUnreachable,
            _ when statusCode >= 500 => AiErrors.ProviderUnreachable,
            _ => AiErrors.BadResponse,
        };
        return new AiException(AiErrors.Of(code, $"provider returned HTTP {statusCode}", details: JsonOf(new
        {
            status = statusCode,
            body = body.Length > 400 ? body[..400] : body,
        })));
    }

    private static System.Text.Json.JsonElement JsonOf(object value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);
}

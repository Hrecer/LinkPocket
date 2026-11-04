using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkPocket.Contracts;

/// <summary>
/// 宿主能力桥的**唯一协议**：持有浏览器内核与真实浏览器窗口的进程（完整桌面应用，或同一个 exe
/// 的按需能力宿主）在进程内提供能力；CLI / MCP 等无头宿主通过本机命名管道请求同一份能力
/// （渲染 / 光栅化 SVG / 人机验证窗口）。
/// <para><b>为什么是桥而不是各宿主各带一套</b>：解析、降级链、人机验证窗口都已在宿主侧实现；
/// 让外部宿主复用同一实例，既不重复启动浏览器，也保证四种入口看到的是同一套能力与同一份缓存。</para>
/// <para><b>边界</b>：管道名由数据库绝对路径派生（同库 = 同桥）、<see cref="PipeOptions.CurrentUserOnly"/>；
/// 人机验证请求在桌面宿主里还要用户明确确认，外部调用不能无声开窗。</para>
/// </summary>
public static class HostCapabilityBridge
{
    public const int Version = 1;
    public const string PipePrefix = "LinkPocket.Capability.";
    private const string ProviderMutexPrefix = @"Local\LinkPocket.Capability.Provider.";

    public static string PipeNameFor(string databasePath) => PipePrefix + KeyFor(databasePath);

    /// <summary>
    /// 每个库同时只能有一个能力提供者（完整桌面宿主或按需能力宿主）。拿到它的一方才开管道；
    /// 另一方让位，避免两个进程同时开 WebView2 profile、也避免客户端随机连到两个服务端。
    /// </summary>
    public static string ProviderMutexNameFor(string databasePath) => ProviderMutexPrefix + KeyFor(databasePath);

    private static string KeyFor(string databasePath)
    {
        var full = Path.GetFullPath(databasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed record Request(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("op")] string Operation,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("timeout_ms")] int TimeoutMs);

    public sealed record Response(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("favicon_url")] string? FaviconUrl,
        [property: JsonPropertyName("png_base64")] string? PngBase64,
        [property: JsonPropertyName("render")] bool Render,
        [property: JsonPropertyName("verify")] bool Verify,
        [property: JsonPropertyName("svg")] bool Svg,
        [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);
}

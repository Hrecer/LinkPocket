using LinkPocket.Api;

namespace LinkPocket.Services;

/// <summary>
/// 前端组装根（组合根）：应用启动时初始化一次。
/// 前端代码一律通过 <see cref="Api"/>（强类型契约）访问后端；
/// 所有请求实际经 <see cref="Transport"/> 走 JSON-RPC 协议。
/// 未来 Web 化时只需把 Transport 换成 HttpTransport，其余代码不动。
/// </summary>
public static class AppServices
{
    /// <summary>前端可用的后端 API（经传输层代理）。</summary>
    public static Api.ILinkPocketApi Api { get; private set; } = null!;

    /// <summary>通信通道（当前为进程内直连，未来可替换为 HTTP/WebSocket）。</summary>
    public static ILinkPocketTransport Transport { get; private set; } = null!;

    /// <summary>
    /// 内容定位组件（「跳转」的标准实现）：进入目标所在目录并选中目标行。
    /// 任何页面/工具都通过它做跳转，而不是各自调用界面方法（避免定位逻辑散落在界面里）。
    /// 界面宿主通过 BrowserLocateHost.Current 注册，组件本身不认识任何窗口类型。
    /// </summary>
    public static IContentLocator Locator { get; private set; } = null!;

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        // 后端：契约实现 + 协议分发器；前端：传输代理
        var backend = new LinkPocketApi();
        Transport = new InProcessTransport(new LinkPocketApiDispatcher(backend));
        Api = new TransportedLinkPocketApi(Transport);
        Locator = new ContentLocator(Api, () => BrowserLocateHost.Current);

        _initialized = true;
    }
}

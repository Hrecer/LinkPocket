using System;
using LinkPocket.Api;

namespace LinkPocket.Services;

/// <summary>
/// 前端组合根：应用启动时装配一次（全工程唯一允许 new 具体实现的地方）。
/// 后端 = 契约实现 + 协议分发器；前端 = 传输代理 + 内容定位器 + 端口槽位。
/// 未来 Web 化时只需把 Transport 换成 HttpTransport，其余代码不动。
/// 阶段 7（前端端口）：AppServices / UiCoordinator / BrowserLocateHost 三个静态定位器
/// 由本类实例替代，依赖经构造注入流向 ViewModel 与页面。
/// </summary>
public sealed class AppHost
{
    /// <summary>通信通道（当前为进程内直连，未来可替换为 HTTP/WebSocket）。</summary>
    public ILinkPocketTransport Transport { get; }

    /// <summary>前端可用的后端 API（经传输层代理）。</summary>
    public ILinkPocketApi Api { get; }

    /// <summary>
    /// 内容定位组件（「跳转」的标准实现）：进入目标所在目录并选中目标行。
    /// 任何页面/工具都通过它做跳转，而不是各自调用界面方法（避免定位逻辑散落在界面里）。
    /// 界面宿主经 <see cref="LocateHost"/> 登记，组件本身不认识任何窗口类型。
    /// </summary>
    public IContentLocator Locator { get; }

    /// <summary>UI 端口槽位：MainWindow（Shell）在构造时登记 IDialogService/INavigationService 实现。</summary>
    public UiPortProvider Ports { get; } = new();

    /// <summary>
    /// UI 事件枢纽（阶段 8 定稿）：后端数据变更 → 界面刷新的唯一 300ms 防抖通道。
    /// 旧链事件源在 <see cref="CreateDefault"/> 里自动接入；阶段 9 起新引擎源经
    /// <c>Hub.Attach(engine.Events)</c> 并入同一条流。
    /// </summary>
    public UiEventHub Hub { get; } = new();

    /// <summary>浏览页宿主（「跳转」原语提供方）：由 MainWindow 构造时登记自身。</summary>
    public IBrowserLocateHost? LocateHost { get; set; }

    private AppHost(ILinkPocketTransport transport, ILinkPocketApi api)
    {
        Transport = transport;
        Api = api;
        Locator = new ContentLocator(Api, () => LocateHost);
    }

    /// <summary>默认装配：进程内后端 + JSON-RPC 分发器 + 传输代理 + 事件枢纽。</summary>
    public static AppHost CreateDefault()
    {
        // 后端：契约实现 + 协议分发器；前端：传输代理
        var backend = new LinkPocketApi();
        var transport = new InProcessTransport(new LinkPocketApiDispatcher(backend));
        var host = new AppHost(transport, new TransportedLinkPocketApi(transport));
        host.Hub.Attach(transport);   // 旧链事件源自动接入（新引擎源在逐页切换时并入）
        return host;
    }
}

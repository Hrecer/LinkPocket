using LinkPocket.Contracts;
using LinkPocket.Engine;

namespace LinkPocket.Services;

/// <summary>
/// 前端组合根：应用启动时装配一次（全工程唯一允许 new 具体实现的地方）。
/// 后端 = 引擎组合根（由共享 Composition 的 <c>EngineComposer</c> 收敛装配：EngineCore +
/// EngineWire + 九模块命令注册 + 编排层）；前端 = 引擎客户端门面 + 内容定位器 + 端口槽位。
/// 前端端口：AppServices / UiCoordinator / BrowserLocateHost 三个静态定位器
/// 由本类实例替代，依赖经构造注入流向 ViewModel 与页面。
/// </summary>
public sealed class AppHost
{
    /// <summary>引擎客户端门面（分层 API 面）：ViewModel 与页面唯一的数据入口。</summary>
    public EngineClient Client { get; }

    /// <summary>引擎 wire 层（JSON-RPC 端点，与内存层同一语义）；暂由未来宿主/诊断消费。</summary>
    public EngineWire Wire { get; }

    /// <summary>
    /// 内容定位组件（「跳转」的标准实现）：进入目标所在目录并选中目标行。
    /// 任何页面/工具都通过它做跳转，而不是各自调用界面方法（避免定位逻辑散落在界面里）。
    /// 界面宿主经 <see cref="LocateHost"/> 登记，组件本身不认识任何窗口类型。
    /// </summary>
    public IContentLocator Locator { get; }

    /// <summary>UI 端口槽位：MainWindow（Shell）在构造时登记 IDialogService/INavigationService 实现。</summary>
    public UiPortProvider Ports { get; } = new();

    /// <summary>
    /// UI 事件枢纽：后端数据变更 → 界面刷新的唯一 300ms 防抖通道。
    /// 事件源 = 引擎事件总线（<see cref="LinkPocket.Contracts.Engine.IEventBus"/>），
    /// 经 <c>Hub.Attach(engine.Events)</c> 并入；ChangeSet 增量投递 + 防抖行级刷新由此生效。
    /// </summary>
    public UiEventHub Hub { get; } = new();

    /// <summary>浏览页宿主（「跳转」原语提供方）：由 MainWindow 构造时登记自身。</summary>
    public IBrowserLocateHost? LocateHost { get; set; }

    private AppHost(EngineClient client, EngineWire wire)
    {
        Client = client;
        Wire = wire;
        Locator = new ContentLocator(client, () => LocateHost);
    }

    /// <summary>
    /// 默认装配：引擎组合根（九模块全量注册 + 编排层；组合由共享 Composition 收敛）+
    /// 引擎客户端 + 事件枢纽接线。
    /// </summary>
    public static AppHost CreateDefault()
    {
        // 组合根 = 全仓库唯一允许 new 具体实现的地方。
        // 引擎装配（DB 工厂 → 九模块 → EngineCore → 编排层 → EngineClient/EngineWire）由
        // LinkPocket.Composition.EngineComposer 统一收敛（审计/幂等落库 + 编排 + wire 全量选项）。
        // 数据库路径沿用旧面默认（AppContext.BaseDirectory/linkpocket.db，WAL + schema 版本链
        // 由 LinkPocketDbContextFactory 一次性启好），保证既有用户数据无缝接管（同一文件，零迁移）。
        var composed = LinkPocket.Composition.EngineComposer.Compose(
            System.IO.Path.Join(AppContext.BaseDirectory, "linkpocket.db"));

        // null-forgiving 必须换显式断言——默认装配必然带 wire（BuildWire 缺省 true），
        // 若未来选项被改动导致 null，这里立即失败而不是把 null 埋进 AppHost.Wire 等运行期 NRE。
        if (composed.Wire is null)
            throw new InvalidOperationException("默认装配必须产出 EngineWire（ComposeOptions.BuildWire 被关闭？）");

        var host = new AppHost(composed.Client, composed.Wire);
        host.Hub.Attach(composed.Engine.Events);   // 新引擎事件源：ChangeSet 增量 + 300ms 防抖刷新
        return host;
    }
}
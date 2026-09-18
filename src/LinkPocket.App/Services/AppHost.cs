using System;
using LinkPocket.Contracts;
using LinkPocket.Engine;

namespace LinkPocket.Services;

/// <summary>
/// 前端组合根：应用启动时装配一次（全工程唯一允许 new 具体实现的地方）。
/// 后端 = 引擎组合根（EngineCore + EngineWire + 九模块命令注册 + 编排层）；
/// 前端 = 引擎客户端门面 + 内容定位器 + 端口槽位。
/// 阶段 7（前端端口）：AppServices / UiCoordinator / BrowserLocateHost 三个静态定位器
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
    /// UI 事件枢纽（阶段 8 定稿）：后端数据变更 → 界面刷新的唯一 300ms 防抖通道。
    /// 自 A1 起事件源 = 新引擎事件总线（<see cref="LinkPocket.Contracts.Engine.IEventBus"/>），
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

    /// <summary>默认装配：引擎组合根（九模块全量注册 + 编排层）+ 引擎客户端 + 事件枢纽接线。</summary>
    public static AppHost CreateDefault()
    {
        // 组合根 = 全仓库唯一允许 new 具体实现的地方。
        // 数据库路径沿用旧面默认（AppContext.BaseDirectory/linkpocket.db，WAL + schema 版本链
        // 由 LinkPocketDbContextFactory 一次性启好），保证既有用户数据无缝接管（同一文件，零迁移）。
        var dbPath = System.IO.Path.Join(AppContext.BaseDirectory, "linkpocket.db");
        var factory = new LinkPocket.Data.LinkPocketDbContextFactory(dbPath);

        var registry = new LinkPocket.Engine.CommandRegistry();
        EngineCore? engineRef = null;   // diagnostics.collect 的 runtime 段接线（引擎后于注册构造）

        registry.RegisterAll(LinkPocket.Modules.Folders.FoldersModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Links.LinksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Trash.TrashModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Search.SearchModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Bookmarks.BookmarksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Backup.BackupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Dedup.DedupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Favicon.FaviconModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Maintenance.MaintenanceModule.CreateHandlers(
            () => engineRef!.RuntimeStats));

        // 编排层（批引擎/撤销协调器挂引擎）+ 16 个编排命令入目录 + audit_log/idempotency 落表
        var engine = new EngineCore(registry, () => new LinkPocket.Data.EfUnitOfWork(factory.CreateDbContext()),
            audit: new LinkPocket.Engine.CompositeAuditWriter(
                new LinkPocket.Engine.InMemoryAuditWriter(),
                new LinkPocket.Engine.SqlAuditWriter(() => factory.CreateDbContext())),
            idempotency: new LinkPocket.Engine.SqlIdempotencyStore(() => factory.CreateDbContext()));
        engineRef = engine;
        registry.RegisterAll(LinkPocket.Engine.OrchestrationHost.CreateHandlers(
            engine, () => factory.CreateDbContext(), StagingRootFor(dbPath)));

        var client = new EngineClient(engine);
        var wire = new EngineWire(engine);
        var host = new AppHost(client, wire);
        host.Hub.Attach(engine.Events);   // 新引擎事件源：ChangeSet 增量 + 300ms 防抖刷新
        return host;
    }

    /// <summary>暂存区根 = 库文件同目录下的派生目录（staging.transform 的文件准备区）。</summary>
    private static string StagingRootFor(string dbPath)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dbPath)!,
            $"linkpocket_staging_{System.IO.Path.GetFileNameWithoutExtension(dbPath)}");
}
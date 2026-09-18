using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;
using LinkPocket.Kernel;

namespace LinkPocket.Composition;

/// <summary>
/// 引擎装配选项（四处宿主的差异点收敛）：审计/幂等是否落库、wire、编排层、暂存区根。
/// 历史形态：AppHost/ProbeEnv = 全量（审计/幂等落库 + 编排 + wire）；TestHost = 裸引擎
/// （无审计落库、无编排、无 wire，其单测断言 Describe==52 条业务命令）；CatalogExport = 只出目录。
/// </summary>
public sealed class ComposeOptions
{
    /// <summary>审计落库 = Composite(InMemory + Sql)；false = 不显式传（EngineCore 缺省纯内存审计）。</summary>
    public bool SqlAudit { get; init; } = true;

    /// <summary>幂等记录落库 = SqlIdempotencyStore；false = 不显式传（EngineCore 缺省内存幂等表）。</summary>
    public bool SqlIdempotency { get; init; } = true;

    /// <summary>是否构造 EngineWire（构造期缓存命令目录 → 必须在全部注册完成后创建）。</summary>
    public bool BuildWire { get; init; } = true;

    /// <summary>是否注册编排层（macro/undo/staging 15 条命令 + 批引擎/撤销协调器挂引擎）。
    /// 模块单测宿主关闭该选项：它只管 52 条业务命令，编排语义归 Engine.Tests。</summary>
    public bool IncludeOrchestration { get; init; } = true;

    /// <summary>暂存区根（staging.transform 文件准备区）；null = 由库路径推导 linkpocket_staging_*。</summary>
    public string? StagingRoot { get; init; }
}

/// <summary>组合根装配结果：引擎本体 + 客户端门面 +（可选）wire + 命令注册表 +（可选）工厂。</summary>
public sealed class EngineComposition
{
    public required EngineCore Engine { get; init; }
    public required EngineClient Client { get; init; }
    public EngineWire? Wire { get; init; }
    public required CommandRegistry Registry { get; init; }
    public LinkPocketDbContextFactory? Factory { get; init; }
}

/// <summary>
/// 引擎组合根装配器（组合抽取 · 2026-09-18）：把四处重复的「DB 工厂 → 九模块注册 → EngineCore
/// → 编排层 → EngineClient/EngineWire」收敛为单一入口。统一配方 =
/// dbPath → LinkPocketDbContextFactory → CommandRegistry 注册九模块（Maintenance 用
/// <c>() =&gt; engineRef!.RuntimeStats</c> 延迟闭包）→ EngineCore(UoW 工厂, audit?, idempotency?)
/// → engineRef = engine → OrchestrationHost.CreateHandlers（可选）→ EngineClient + EngineWire。
/// 消费方（WPF 宿主 / 冒烟 / 测试 / 工具）只依赖 Contracts + Composition。
/// </summary>
public static class EngineComposer
{
    /// <summary>真实库路径入口：缺省建 <see cref="LinkPocketDbContextFactory"/>（构造即启 WAL + 建 schema）。</summary>
    public static EngineComposition Compose(string dbPath, ComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        var factory = new LinkPocketDbContextFactory(dbPath);
        options ??= new ComposeOptions();
        return BuildCore(
            () => new EfUnitOfWork(factory.CreateDbContext()),
            () => factory.CreateDbContext(),
            options,
            options.StagingRoot ?? Path.Combine(Path.GetDirectoryName(dbPath)!,
                $"linkpocket_staging_{Path.GetFileNameWithoutExtension(dbPath)}"),
            factory);
    }

    /// <summary>不触库入口（目录导出等）：UoW 与宏库工厂必须由调用方提供「调用即抛」的占位实现——
    /// 导出流程构造不触库，任何真实数据访问都要立刻暴露（零责任红线）。</summary>
    public static EngineComposition Compose(
        Func<IUnitOfWork> uowFactory, Func<LinkPocketDbContext> dbContextFactory,
        ComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(uowFactory);
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        return BuildCore(uowFactory, dbContextFactory, options ?? new ComposeOptions(), null, null);
    }

    private static EngineComposition BuildCore(
        Func<IUnitOfWork> uowFactory, Func<LinkPocketDbContext> dbContextFactory,
        ComposeOptions options, string? defaultStagingRoot, LinkPocketDbContextFactory? factory)
    {
        var registry = new CommandRegistry();

        // diagnostics.collect 的 runtime 段由组合根接线（阶段 12）：引擎在注册之后才构造，
        // 故用延迟读取的闭包 —— 引擎 = 观测对象本身，接线不得引入第二份统计源。
        EngineCore? engineRef = null;
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

        var engine = new EngineCore(registry, uowFactory,
            audit: options.SqlAudit
                ? new CompositeAuditWriter(new InMemoryAuditWriter(), new SqlAuditWriter(dbContextFactory))
                : null,
            idempotency: options.SqlIdempotency ? new SqlIdempotencyStore(dbContextFactory) : null);
        engineRef = engine;

        if (options.IncludeOrchestration)
            registry.RegisterAll(OrchestrationHost.CreateHandlers(engine, dbContextFactory,
                options.StagingRoot ?? defaultStagingRoot));

        var client = new EngineClient(engine);
        var wire = options.BuildWire ? new EngineWire(engine) : null;   // 构造期缓存目录 → 必须在注册完成后创建
        return new EngineComposition
        {
            Engine = engine,
            Client = client,
            Wire = wire,
            Registry = registry,
            Factory = factory,
        };
    }
}
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Diagnostics;
using LinkPocket.Engine;
using LinkPocket.Kernel;

namespace LinkPocket.Composition;

/// <summary>
/// 引擎装配选项（四处宿主的差异点收敛）：审计/幂等是否落库、wire、编排层、暂存区根。
/// 历史形态：AppHost/ProbeEnv = 全量（审计/幂等落库 + 编排 + wire）；TestHost = 裸引擎
/// （无审计落库、无编排、无 wire，其单测断言 Describe==58 条业务命令）；CatalogExport = 只出目录。
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
    /// 模块单测宿主关闭该选项：它只管 58 条业务命令，编排语义归 Engine.Tests。</summary>
    public bool IncludeOrchestration { get; init; } = true;

    /// <summary>暂存区根（staging.transform 文件准备区）；null = 由库路径推导 linkpocket_staging_*。</summary>
    public string? StagingRoot { get; init; }
}

/// <summary>组合根装配结果：引擎本体 + 客户端门面 +（可选）wire + 命令注册表 +（可选）工厂 + 会话管理器。</summary>
public sealed class EngineComposition
{
    public required EngineCore Engine { get; init; }
    public required EngineClient Client { get; init; }
    public EngineWire? Wire { get; init; }
    public required CommandRegistry Registry { get; init; }
    public LinkPocketDbContextFactory? Factory { get; init; }

    /// <summary>会话管理器（能力门：只读拒绝写 + 滑窗限流）。四个宿主共用同一实现；
    /// 调用方须自行 BeginAsync 并把 SessionId 放进 <c>CallOptions.Caller</c> 才受约束——
    /// 未带 SessionId 的调用是宿主自有调用，零约束（既有口径）。</summary>
    public required ISessionManager Sessions { get; init; }
}

/// <summary>
/// 引擎组合根装配器：把四处重复的「DB 工厂 → 九模块注册 → EngineCore
/// → 编排层 → EngineClient/EngineWire」收敛为单一入口。统一配方 =
/// dbPath → LinkPocketDbContextFactory → CommandRegistry 注册九模块（Maintenance 用
/// <c>() =&gt; engineRef!.RuntimeStats</c> 延迟闭包）→ EngineCore(UoW 工厂, audit?, idempotency?)
/// → engineRef = engine → OrchestrationHost.CreateHandlers（可选）→ EngineClient + EngineWire。
/// 消费方（WPF 宿主 / 冒烟 / 测试 / 工具）只依赖 Contracts + Composition。
/// </summary>
public static class EngineComposer
{
    /// <summary>
    /// 日志管道装配（观测面组合根，**由宿主显式调用**——测试/工具不调即不落盘，避免污染）：
    /// 缺省落点 = 「JSONL 文件（按日 + 轮转 + 保留）+ 内存环」；宿主可传自定义落点
    /// （如无头宿主用 stderr JSONL，见 <c>Diagnostics.JsonlTextWriterSink</c>——**建议仍带上内存环**，
    /// 否则 <c>logs.query source=memory</c> 无数据）。
    /// 重复调用 = 替换管道（旧管道不自动处置，调用方若持有请自行 Dispose）。
    /// 退出前宿主必须调 <c>LpLog.Shutdown()</c>（刷盘 + 卸管道）。
    /// </summary>
    public static LogPipeline ConfigureLogging(LoggingOptions? options = null, params ILogSink[] sinks)
    {
        options ??= new LoggingOptions();
        var targets = sinks is { Length: > 0 }
            ? sinks
            : [new JsonlFileSink(options), new MemoryLogSink(options.MemoryCapacity)];
        var pipeline = new LogPipeline(options, targets);
        LpLog.Configure(pipeline);
        return pipeline;
    }

    /// <summary>
    /// 宿主日志选项的**唯一实现**（环境策略归宿主，但 App 与无头宿主同一套口径 → 收在这里，
    /// 不再各写一份）：缺省 info + <c>{BaseDirectory}/logs</c>；
    /// <c>LINKPOCKET_LOG_LEVEL</c> = trace|debug|info|warn|error|fatal；<c>LINKPOCKET_LOG_DIR</c> = 目录覆盖。
    /// 级别名称经契约的 <see cref="LogLevels"/> 解析（**全站唯一映射**）；不认识的写法回落 info（缺省口径）。
    /// </summary>
    public static LoggingOptions HostLoggingOptions() => new()
    {
        MinimumLevel = LogLevels.TryParse(Environment.GetEnvironmentVariable("LINKPOCKET_LOG_LEVEL"), out var level)
            ? level
            : LogLevel.Info,
        Directory = Environment.GetEnvironmentVariable("LINKPOCKET_LOG_DIR"),
    };

    /// <summary>
    /// 无头 / 服务型宿主的日志装配：**stderr JSONL + 内存环**（stdout 留给协议帧，日志绝不混入）。
    /// 与 App 的差别只在落点——选项口径（<see cref="HostLoggingOptions"/>）与管道实现完全同一份。
    /// </summary>
    public static LogPipeline ConfigureHeadlessLogging(TextWriter? stderr = null, LoggingOptions? options = null)
    {
        options ??= HostLoggingOptions();
        return ConfigureLogging(options,
            new JsonlTextWriterSink(stderr ?? Console.Error),
            new MemoryLogSink(options.MemoryCapacity));
    }

    /// <summary>真实库路径入口：缺省建 <see cref="LinkPocketDbContextFactory"/>（构造即启 WAL + 建 schema）。</summary>
    public static EngineComposition Compose(string dbPath, ComposeOptions? options = null)
    {
        // 空串会绕过 ThrowIfNull（空串非 null），随后 Path.GetDirectoryName("")
        // 返回 null → Path.Combine 抛「参数为 null」——错误信息误导；这里显式拒绝 + 归一化
        // 绝对路径（相对路径的 cwd 漂移风险收敛）
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("dbPath must not be empty", nameof(dbPath));
        dbPath = Path.GetFullPath(dbPath);
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
        ComposeOptions options, string? fallbackStagingRoot, LinkPocketDbContextFactory? factory)
    {
        var registry = new CommandRegistry();

        // diagnostics.collect 的 runtime 段由组合根接线：引擎在注册之后才构造，
        // 故用延迟读取的闭包 —— 引擎 = 观测对象本身，接线不得引入第二份统计源。
        // ⚠️ 该闭包在 new EngineCore 之后才可求值（届时 engineRef 已赋值）；
        // 若构造中途抛异常，registry 不会对外泄漏，闭包也不会被求值——仅登记时序依赖，勿提前解引用。
        EngineCore? engineRef = null;
        registry.RegisterAll(LinkPocket.Modules.Folders.FoldersModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Links.LinksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Trash.TrashModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Search.SearchModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Bookmarks.BookmarksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Backup.BackupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Dedup.DedupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Favicon.FaviconModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Locate.LocateModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Maintenance.MaintenanceModule.CreateHandlers(
            () => engineRef!.RuntimeStats));

        // 会话管理器：四宿主共用（AI 代理的限流/只读能力门的唯一执行点）。
        // 未带 SessionId 的调用不受约束，故对既有界面/测试零影响。
        var sessions = new SessionManager();
        var engine = new EngineCore(registry, uowFactory,
            audit: options.SqlAudit
                ? new CompositeAuditWriter(new InMemoryAuditWriter(), new SqlAuditWriter(dbContextFactory))
                : null,
            idempotency: options.SqlIdempotency ? new SqlIdempotencyStore(dbContextFactory) : null,
            sessions: sessions);
        engineRef = engine;

        // fallbackStagingRoot 仅在「库路径入口」非空；uowFactory 入口传 null →
        // 由 StagingService 走 TempArea 兜底。选项优先级 = StagingRoot（显式）> fallbackStagingRoot（推导）> TempArea（缺省）。
        if (options.IncludeOrchestration)
            registry.RegisterAll(OrchestrationHost.CreateHandlers(engine, dbContextFactory,
                options.StagingRoot ?? fallbackStagingRoot));

        var client = new EngineClient(engine);
        var wire = options.BuildWire ? new EngineWire(engine) : null;   // 构造期缓存目录 → 必须在注册完成后创建
        return new EngineComposition
        {
            Engine = engine,
            Client = client,
            Wire = wire,
            Registry = registry,
            Factory = factory,
            Sessions = sessions,
        };
    }
}
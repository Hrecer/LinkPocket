using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 编排层装配宿主：在引擎构造完成后调用——
/// 创建 <see cref="BatchEngine"/>（挂到 <see cref="IEngine.Batch"/>）、
/// <see cref="UndoCoordinator"/>（挂到 <see cref="EngineCore.Undo"/>）、
/// <see cref="StagingService"/>、<see cref="MacroStore"/>，并把 16 个编排命令注册进命令表。
/// 组合点顺序：new EngineCore(...) → CreateHandlers(...) → registry.RegisterAll(handlers) → 再构造 wire（目录缓存）。
/// </summary>
public static class OrchestrationHost
{
    /// <summary>装配编排层并返回待注册命令（stagingRoot 缺省 = 临时目录下 linkpocket-staging）。</summary>
    public static IReadOnlyList<ICommandHandler> CreateHandlers(
        EngineCore engine, Func<LinkPocketDbContext> dbFactory, string? stagingRoot = null,
        Kernel.EngineLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dbFactory);

        var batch = new BatchEngine(engine, limits);
        engine.Batch = batch;

        var undo = new UndoCoordinator();
        engine.Undo = undo;

        var staging = new StagingService(stagingRoot, () => engine);
        return OrchestrationHandlers.CreateAll(new MacroStore(dbFactory), undo, staging, limits ?? Kernel.EngineLimits.Default);
    }

    /// <summary>装配引擎目录（registry 全量 + batch 三命令描述符；AI 工具清单/文档的唯一事实源）。</summary>
    public static IEngineCatalog CreateCatalog(CommandRegistry registry) => new EngineCatalog(registry, includeBatch: true);
}

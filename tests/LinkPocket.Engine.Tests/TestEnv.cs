using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine.Tests;

/// <summary>测试支撑：临时文件库（真实 WAL 语义）+ 样例命令集。</summary>
internal static class TestEnv
{
    public static (LinkPocketDbContextFactory Factory, string Path) CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpengine_{Guid.NewGuid():N}.db");
        // 工厂构造内已完成 WAL 启用 + schema v2 建库（SchemaMigrator），无需 EnsureCreated
        var factory = new LinkPocketDbContextFactory(path);
        return (factory, path);
    }

    public static EngineCore CreateEngine(LinkPocketDbContextFactory factory, params ICommandHandler[] extra)
        => CreateEngine(factory, withOrchestration: false, sessions: null, extra);

    public static EngineCore CreateEngine(LinkPocketDbContextFactory factory, bool withOrchestration,
        ISessionManager? sessions = null, params ICommandHandler[] extra)
    {
        var registry = new CommandRegistry();
        var builtins = new List<ICommandHandler>
        {
            new CountFoldersHandler(),
            new AddFolderHandler(),
            new DestructiveHandler(),
            new NestedAddHandler(),
            new FailingHandler(),
            new SlowWriteHandler(),
        };
        // extra 同名替换内置（测试注入自持实例以观察调用）
        var extraNames = extra.Select(h => h.Descriptor.Name).ToHashSet();
        registry.RegisterAll(builtins.Where(h => !extraNames.Contains(h.Descriptor.Name)));
        registry.RegisterAll(extra);

        var engine = new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext()), sessions: sessions);
        if (withOrchestration)
        {
            // 阶段 11：编排命令入同一目录（Batch/Undo 挂引擎，宿主装配口径与 OrchestrationHost 一致）
            registry.RegisterAll(OrchestrationHost.CreateHandlers(engine, () => factory.CreateDbContext()));
        }
        return engine;
    }
}

/// <summary>查询：统计文件夹数。</summary>
internal sealed class CountFoldersHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.count_folders", "test", "统计文件夹数量", [], CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        return CommandResult.Ok(folders.Count);
    }
}

/// <summary>变更：新增文件夹（记录调用次数与执行时的工作单元实例，供断言）。</summary>
internal sealed class AddFolderHandler : ICommandHandler
{
    private int _invocations;

    public int Invocations => _invocations;
    public List<IUnitOfWork> SeenUows { get; } = [];

    public CommandDescriptor Descriptor { get; } =
        new("test.add_folder", "test", "新增测试文件夹",
            [ParamSpec.Req<string>("name", "文件夹名")], CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        _invocations++;
        SeenUows.Add(ctx.Uow);
        var name = args.GetProperty("name").GetString() ?? "未命名";
        var folder = await ctx.Uow.Folders.AddAsync(new Folder { Name = name }, ctx.Ct);
        return CommandResult.Ok(folder.FolderId,
            ChangeSet.Of(new EntityRef("folder", folder.FolderId), "folders.changed", $"已创建「{name}」"));
    }
}

/// <summary>破坏性命令：验证两阶段确认令牌生命周期。</summary>
internal sealed class DestructiveHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.destructive", "test", "破坏性测试命令",
            [], CommandCaps.Mutation | CommandCaps.Destructive, Impact: ImpactSummary.Database);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok("done",
            ChangeSet.Of(new EntityRef("database", "*"), "test.changed", "已执行破坏性操作")));
}

/// <summary>嵌套：父命令内派发 test.add_folder（断言复用父工作单元、不重入写闸）。</summary>
internal sealed class NestedAddHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.nested_add", "test", "嵌套派发新增文件夹",
            [ParamSpec.Req<string>("name", "文件夹名")], CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var child = await ctx.DispatchNestedAsync("test.add_folder", new { name = args.GetProperty("name").GetString() });
        return CommandResult.Ok(child.Data);
    }
}

/// <summary>失败命令：验证审计记录错误码。</summary>
internal sealed class FailingHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.fail", "test", "必定失败", [], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "测试用失败"));
}

/// <summary>慢写：验证查询可与在途写并发（写闸不阻塞读）。</summary>
internal sealed class SlowWriteHandler : ICommandHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CommandDescriptor Descriptor { get; } =
        new("test.slow_write", "test", "慢变更（由测试手动放行）", [], CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        Started.TrySetResult();
        await Finish.Task.WaitAsync(ctx.Ct);
        await ctx.Uow.Folders.AddAsync(new Folder { Name = "slow" }, ctx.Ct);
        return CommandResult.Ok("slow-done");
    }
}

/// <summary>
/// 可缓存查询（测试用）：命令名与缓存依赖可配，并记录「实际触库次数」——
/// 缓存断言只能靠触库次数证明（结果相同可能是缓存命中，也可能是数据没变）。
/// </summary>
internal sealed class CachedQueryHandler(string name, CachePolicy policy) : ICommandHandler
{
    private int _executions;

    /// <summary>实际进入 Handler 的次数（缓存命中不递增）。</summary>
    public int Executions => Volatile.Read(ref _executions);

    public CommandDescriptor Descriptor { get; } = new(
        name, "test", $"可缓存测试查询（依赖 {string.Join("+", policy.DependsOn)}，TTL {policy.TtlSeconds}s）",
        [], CommandCaps.Query, Cache: policy);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        Interlocked.Increment(ref _executions);
        var folders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        return CommandResult.Ok(folders.Count);
    }
}

/// <summary>变更（测试用）：只发布指定事件名，用于验证「缓存失效按事件名精确生效、不是全清」。</summary>
internal sealed class EmitEventHandler(string eventName) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        $"test.emit_{eventName.Replace('.', '_')}", "test", $"只发布 {eventName}", [], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok("ok", ChangeSet.Of(new EntityRef("test", "1"), eventName)));
}

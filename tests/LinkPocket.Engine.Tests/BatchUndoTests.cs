using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// E5（批/宏入撤销栈）+ P3-0（<c>batch.*</c> 引擎入口直路由）：
/// 一次批 = 一条记录 N 逆向步（归属键合并）、整批回滚 / 干跑绝不入栈、撤销逆序回绕、重做正序重放、
/// 独立批同组合并、宏同口径、调用方归属键（AI 的工具调用 ID）透传。
/// </summary>
public class BatchUndoTests
{
    private static (EngineCore Engine, LinkPocketDbContextFactory Factory, string Db) Create()
    {
        var (factory, path) = TestEnv.CreateDb();
        MarkHandler.Reset();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: true, sessions: null,
            new MarkHandler(), new UnmarkHandler());
        return (engine, factory, path);
    }

    private static BatchScript Script(params string[] tags)
        => new("批", tags.Select((tag, i) => new BatchStep($"s{i}", "test.mark",
            JsonSerializer.SerializeToElement(new { tag }))).ToArray());

    private static async Task<JsonElement> UndoListAsync(EngineCore engine)
        => await engine.QueryAsync<JsonElement>("undo.list");

    private static JsonElement[] Entries(JsonElement list)
        => list.GetProperty("entries").EnumerateArray().ToArray();

    // ===== E5：批/宏入撤销栈 =====

    [Fact]
    public async Task 事务批_入栈一条记录_撤销逆序退全部_重做正序重放()
    {
        var (engine, _, path) = Create();
        try
        {
            var batch = engine.Batch!;
            var report = await batch.RunAsync(Script("A", "B"));
            Assert.True(report.Ok);
            Assert.Equal(new[] { "A", "B" }, MarkHandler.Marks);

            var entry = Assert.Single(Entries(await UndoListAsync(engine)));
            Assert.Equal(2, entry.GetProperty("steps").GetArrayLength());   // 一次批 = 一条记录 N 逆向步
            Assert.Equal(report.BatchId, entry.GetProperty("group_id").GetString());

            await engine.ExecuteAsync<JsonElement>("undo.undo", null);
            Assert.Equal(new[] { "B", "A" }, UnmarkHandler.Log);                    // 逆序回绕：后做的先退
            Assert.Empty(MarkHandler.Marks);

            await engine.ExecuteAsync<JsonElement>("undo.redo", null);
            Assert.Equal(new[] { "A", "B" }, MarkHandler.Marks);                    // 正序重放：先做的先来
            Assert.Single(Entries(await UndoListAsync(engine)));            // 重做后重新入栈
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 事务批_整批回滚_不留撤销记录()
    {
        var (engine, _, path) = Create();
        try
        {
            var script = new BatchScript("回滚批",
            [
                new BatchStep("a", "test.mark", JsonSerializer.SerializeToElement(new { tag = "A" })),
                new BatchStep("b", "test.missing", JsonSerializer.SerializeToElement(new { })),
            ]);
            await Assert.ThrowsAsync<EngineException>(() => engine.Batch!.RunAsync(script));

            Assert.Empty(Entries(await UndoListAsync(engine)));             // 回滚 = 没发生 = 不入栈
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 干跑_不入撤销栈()
    {
        var (engine, _, path) = Create();
        try
        {
            var report = await engine.Batch!.DryRunAsync(Script("A"));
            Assert.True(report.Ok);
            Assert.Empty(Entries(await UndoListAsync(engine)));             // 干跑零副作用：撤销栈不例外
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 独立批_逐条提交_仍按同组合并为一条记录()
    {
        var (engine, _, path) = Create();
        try
        {
            var script = new BatchScript("独立批",
            [
                new BatchStep("a", "test.mark", JsonSerializer.SerializeToElement(new { tag = "A" })),
                new BatchStep("b", "test.mark", JsonSerializer.SerializeToElement(new { tag = "B" })),
            ], BatchScope.Independent);
            await engine.Batch!.RunAsync(script);
            Assert.Equal(new[] { "A", "B" }, MarkHandler.Marks);

            var entry = Assert.Single(Entries(await UndoListAsync(engine)));
            Assert.Equal(2, entry.GetProperty("steps").GetArrayLength());
            await engine.ExecuteAsync<JsonElement>("undo.undo", null);
            Assert.Empty(MarkHandler.Marks);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 宏_入栈与批同口径()
    {
        var (engine, _, path) = Create();
        try
        {
            await engine.ExecuteAsync<JsonElement>("macro.save", new
            {
                name = "打标宏",
                script = JsonSerializer.SerializeToElement(Script("X", "Y")),
            });
            await engine.ExecuteAsync<JsonElement>("macro.run", new { name = "打标宏" });
            Assert.Equal(new[] { "X", "Y" }, MarkHandler.Marks);

            var entry = Assert.Single(Entries(await UndoListAsync(engine)));
            Assert.Equal(2, entry.GetProperty("steps").GetArrayLength());
            await engine.ExecuteAsync<JsonElement>("undo.undo", null);
            Assert.Empty(MarkHandler.Marks);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 归属键_调用方给定_原样进撤销记录()
    {
        var (engine, _, path) = Create();
        try
        {
            await engine.Batch!.RunAsync(Script("A"), new CallOptions(UndoGroupId: "call-1"));
            var entry = Assert.Single(Entries(await UndoListAsync(engine)));
            Assert.Equal("call-1", entry.GetProperty("group_id").GetString());   // AI 的工具调用 ID 归属口径
        }
        finally { TestEnv.Cleanup(path); }
    }

    // ===== P3-0：batch.* 经引擎入口直路由（进程内消费者与 wire 同口径）=====

    [Fact]
    public async Task 批三命令_引擎入口直路由_与IBatchEngine同口径()
    {
        var (engine, _, path) = Create();
        try
        {
            var result = await engine.ExecuteAsync<object>("batch.run",
                new { script = Script("A") });
            var report = Assert.IsType<BatchReport>(result.Data);
            Assert.True(report.Ok);
            Assert.Equal(new[] { "A" }, MarkHandler.Marks);                            // 写流直路由真的执行了
            Assert.Single(Entries(await UndoListAsync(engine)));               // 直路由同样入撤销栈

            var status = await engine.QueryAsync<BatchStatus>("batch.status",
                new { batch_id = report.BatchId });
            Assert.Equal("completed", status.State);

            var dry = await engine.QueryAsync<BatchReport>("batch.dry_run",
                new { script = Script("Z") });
            Assert.True(dry.Ok);
            // 干跑 = 执行但不提交：处理器照跑（内存侧效应拦不住），但撤销栈只记**已提交事实**——不许多一条
            Assert.Single(Entries(await UndoListAsync(engine)));
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 批状态_未知批次_如实报不存在()
    {
        var (engine, _, path) = Create();
        try
        {
            var ex = await Assert.ThrowsAsync<EngineException>(() =>
                engine.QueryAsync<BatchStatus>("batch.status", new { batch_id = "nope" }));
            Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 批状态_省略批次号_读在飞批_空闲时为null()
    {
        var (engine, _, path) = Create();
        try
        {
            // 空闲：data = null（"现在没有批在跑"是合法答案，不是错误）
            var idle = await engine.QueryAsync<JsonElement>("batch.status", new { });
            Assert.Equal(JsonValueKind.Null, idle.ValueKind);
            Assert.Null(engine.Batch!.CurrentStatus);

            // 批跑完即自清零（状态表里只剩终态，没有 running 条目）
            var report = await engine.Batch.RunAsync(Script("A"));
            Assert.NotNull(engine.Batch.GetStatus(report.BatchId));
            Assert.Null(engine.Batch.CurrentStatus);
            var after = await engine.QueryAsync<JsonElement>("batch.status", new { });
            Assert.Equal(JsonValueKind.Null, after.ValueKind);
        }
        finally { TestEnv.Cleanup(path); }
    }
}

/// <summary>test.mark（Reversible）：向内存账本追加标记；**处理器回填**逆向步（弹出该标记）。</summary>
internal sealed class MarkHandler : ICommandHandler
{
    public static readonly List<string> Marks = [];

    public static void Reset()
    {
        Marks.Clear();
        UnmarkHandler.Log.Clear();
    }

    public CommandDescriptor Descriptor { get; } = new("test.mark", "test", "打标记（可逆，逆向 unmark）",
        [ParamSpec.Req<string>("tag", "mark tag")], CommandCaps.Mutation | CommandCaps.Reversible);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var tag = args.GetProperty("tag").GetString()!;
        Marks.Add(tag);
        return Task.FromResult(CommandResult.Ok("marked",
            ChangeSet.Of(new EntityRef("test", tag), "test.changed", "marked"),
            undo: [new UndoInverseStep("test.unmark", JsonSerializer.SerializeToElement(new { tag }))]));
    }
}

/// <summary>test.unmark：弹出标记并记录撤销顺序（逆序回绕断言用）。</summary>
internal sealed class UnmarkHandler : ICommandHandler
{
    public static readonly List<string> Log = [];

    public CommandDescriptor Descriptor { get; } = new("test.unmark", "test", "弹出标记",
        [ParamSpec.Req<string>("tag", "mark tag")], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var tag = args.GetProperty("tag").GetString()!;
        MarkHandler.Marks.Remove(tag);
        Log.Add(tag);
        return Task.FromResult(CommandResult.Ok("unmarked", ChangeSet.Of(new EntityRef("test", tag), "test.changed")));
    }
}

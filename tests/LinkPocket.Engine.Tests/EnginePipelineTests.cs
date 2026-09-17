using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>管道行为断言：错误模型、写闸、幂等、确认令牌、干跑、嵌套、审计、事件、WAL（方案第八章 Engine 行）。</summary>
public class EnginePipelineTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lpengine_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task UnknownCommand_Throws_Sys001()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);
            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<string>("test.nope"));
            Assert.Equal(EngineErrors.UnknownCommand, ex.Error.Code);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Mutation_Persists_And_Publishes_Event()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);
            var events = new List<string>();
            using (engine.Events.Subscribe(e => events.Add(e.Name)))
            {
                var result = await engine.ExecuteAsync<string>("test.add_folder", new { name = "工作" });

                Assert.True(result.Ok);
                Assert.NotNull(result.Data);
                Assert.Contains("folders.changed", result.Changes!.Events);
                Assert.Equal("已创建「工作」", result.Changes.HumanSummary);
                await Task.Yield();
            }

            // 事件已同步推送（持闸期间）
            Assert.Contains("folders.changed", events);

            // 已持久化：新工作单元读得到
            var count = await engine.QueryAsync<int>("test.count_folders");
            Assert.Equal(1, count);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task IdempotencyKey_SecondCall_ReturnsFirstResult_WithoutReexecution()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var handler = new AddFolderHandler();
            var engine2 = TestEnv.CreateEngine(factory, handler);

            var options = new CallOptions(IdempotencyKey: "idem-1");
            var first = await engine2.ExecuteAsync<string>("test.add_folder", new { name = "A" }, options);
            var second = await engine2.ExecuteAsync<string>("test.add_folder", new { name = "B" }, options);

            Assert.Equal(first.Data, second.Data);       // 返回首次结果副本
            Assert.Equal(1, handler.Invocations);        // 未重复执行
            var count = await engine2.QueryAsync<int>("test.count_folders");
            Assert.Equal(1, count);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Destructive_WithoutToken_ThrowsConfirmRequired_ThenTokenExecutes()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);

            // 第一次：CONFIRM_REQUIRED，Details 携带 impact + confirm_token
            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<string>("test.destructive"));
            Assert.Equal(EngineErrors.ConfirmRequired, ex.Error.Code);
            Assert.True(ex.Error.Details.HasValue);
            var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
            Assert.False(string.IsNullOrEmpty(token));
            Assert.Equal("整库", ex.Error.Details!.Value.GetProperty("impact").GetString());

            // 第二次：持令牌 → 执行成功
            var result = await engine.ExecuteAsync<string>("test.destructive",
                options: new CallOptions(ConfirmToken: token));
            Assert.True(result.Ok);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Destructive_WithExpiredToken_ThrowsConfirmExpired()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var tokens = new ConfirmTokenStore(TimeSpan.Zero);
            var registry = new CommandRegistry();
            registry.Register(new DestructiveHandler());
            var engine = new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext()), confirmTokens: tokens);

            var token = tokens.Issue("test.destructive");
            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<string>("test.destructive", options: new CallOptions(ConfirmToken: token)));
            Assert.Equal(EngineErrors.ConfirmExpired, ex.Error.Code);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task DryRun_Executes_But_Persists_Nothing_Publishes_Nothing()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);
            var events = new List<string>();
            using (engine.Events.Subscribe(e => events.Add(e.Name)))
            {
                var result = await engine.ExecuteAsync<string>("test.add_folder",
                    new { name = "预演" }, new CallOptions(DryRun: true));
                Assert.True(result.Ok);
                Assert.NotNull(result.Changes);   // 预演仍返回变更集与影响面
                await Task.Yield();
            }

            Assert.Empty(events);   // 零事件
            var count = await engine.QueryAsync<int>("test.count_folders");
            Assert.Equal(0, count); // 零副作用
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Nested_ReusesParentUow_NoGateReentry_NoDeadlock()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var child = new AddFolderHandler();
            var engine = TestEnv.CreateEngine(factory, child);

            // 父命令持闸期间嵌套派发：若重入写闸将死锁（测试自身带超时兜底）
            var result = await engine.ExecuteAsync<string>("test.nested_add", new { name = "嵌套目录" });
            Assert.True(result.Ok);
            Assert.Equal(1, child.Invocations);
            Assert.Single(child.SeenUows);

            var count = await engine.QueryAsync<int>("test.count_folders");
            Assert.Equal(1, count);   // 嵌套变更随父提交一并落库
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Failure_Is_Audited_With_ErrorCode_And_NoSideEffects()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var audit = new InMemoryAuditWriter();
            var registry = new CommandRegistry();
            registry.Register(new FailingHandler());
            registry.Register(new CountFoldersHandler());
            var engine = new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext()), audit);

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<string>("test.fail"));
            Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);

            var entries = audit.Snapshot();
            var entry = Assert.Single(entries);
            Assert.False(entry.Success);
            Assert.Equal(EngineErrors.EntityNotFound, entry.ErrorCode);
            Assert.NotNull(entry.StackTrace);   // 失败审计可追溯

            var count = await engine.QueryAsync<int>("test.count_folders");
            Assert.Equal(0, count);             // 失败零副作用
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Query_Runs_While_Write_InFlight_WriteGateNotBlockingReads()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var slow = new SlowWriteHandler();
            var engine = TestEnv.CreateEngine(factory, slow);

            var writeTask = engine.ExecuteAsync<string>("test.slow_write");
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 写已持闸

            // 读不占写闸：写在途期间读立即可用
            var readTask = engine.QueryAsync<int>("test.count_folders");
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read);

            slow.Finish.TrySetResult();
            var write = await writeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("slow-done", write.Data);

            var count = await engine.QueryAsync<int>("test.count_folders");
            Assert.Equal(1, count);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Wal_Is_Enabled_On_Database()
    {
        var (_, path) = TestEnv.CreateDb();
        try
        {
            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder($"Data Source={path}").ToString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)(await cmd.ExecuteScalarAsync());
            Assert.Equal("wal", mode?.ToLowerInvariant());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Describe_Lists_Commands_ByCategory()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);
            var all = engine.Describe();
            Assert.Contains(all.Commands, d => d.Name == "test.add_folder");
            Assert.Contains(all.Commands, d => d.Name == "test.count_folders");

            var none = engine.Describe("no-such-category");
            Assert.Empty(none.Commands);
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
        catch { /* 临时文件清理尽力而为 */ }
    }
}

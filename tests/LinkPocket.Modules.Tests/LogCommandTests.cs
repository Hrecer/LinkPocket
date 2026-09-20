using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Modules.Maintenance;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// logs.query / logs.level 黑盒语义（S2b）：日志读侧是"刚才发生了什么"的唯一入口。
/// <para>⚠️ LpLog 是**进程级静态**：本类与 <see cref="DiagnosticsLoggingSectionTests"/> 同属一个 xunit Collection
/// （同集合内串行），否则两类的"装配 / 未装配"起点会互相插入。**同程序集的其它测试类仍并行**，
/// 它们的引擎日志（失败行是 Warn/Error）也会进同一个管道——因此本类断言一律**只针对自己写的记录**
/// （带唯一分类过滤），不对"环里一共几条"作整体断言。</para>
/// <para>两种起点都覆盖：**未装配必须报 LP.STATE.005**（不得返回空列表假装"没有日志"）；装配后验过滤 /
/// 游标 / 调级。文件源的内容级断言（回读 + 残行跳过计数）在单线程确定性口径的 Diagnostics 层。</para>
/// </summary>
[Collection("日志管道")]
public class LogCommandTests
{
    /// <summary>本类记录的分类：与其它测试类（engine.pipeline 等）的日志隔离开，断言才稳定。</summary>
    private const string TestCategory = "logs-cmd-test";

    [Fact]
    public async Task 未装配日志管道_两个命令都如实报错()
    {
        LpLog.Configure(null);   // 防御：本用例起点必须未装配（其它用例会装过）
        var (engine, _, _) = TestHost.Create();

        var query = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<LogQueryResult>("logs.query"));
        Assert.Equal(EngineErrors.LogUnavailable, query.Error.Code);
        Assert.Equal("LP.STATE.005", query.Error.Code);

        var level = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<LogsLevelResult>("logs.level", new { level = "debug" }));
        Assert.Equal(EngineErrors.LogUnavailable, level.Error.Code);
    }

    [Fact]
    public async Task 装配后_内存源按级别分类过滤且游标可增量轮询()
    {
        var dir = TempDir();
        // 级别 = info：引擎自己的里程碑日志是 Debug 级（engine.pipeline），本用例的口径里看不到它
        var logging = EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Info });
        try
        {
            var (engine, _, _) = TestHost.Create();
            Assert.Equal(LogLevel.Info, logging.Level);   // 装配口径生效

            LpLog.Info("第一条", TestCategory);
            LpLog.Warn("第二条", category: TestCategory);
            LpLog.Info("第三条", TestCategory);

            var all = await engine.QueryAsync<LogQueryResult>("logs.query", new { category = TestCategory });
            Assert.Equal(LogSource.Memory, all.Source);
            Assert.Equal(LogLevel.Info, all.MinimumLevel);
            Assert.Equal(200, all.Limit);
            Assert.Equal(new[] { "第一条", "第二条", "第三条" }, all.Items.Select(r => r.Message));   // 时间升序
            Assert.NotNull(all.NextCursor);

            var warn = await engine.QueryAsync<LogQueryResult>("logs.query",
                new { category = TestCategory, level = "WARN" });   // 级别名大小写不敏感
            Assert.Equal("第二条", Assert.Single(warn.Items).Message);

            var limited = await engine.QueryAsync<LogQueryResult>("logs.query", new { category = TestCategory, limit = 2 });
            Assert.Equal(new[] { "第一条", "第二条" }, limited.Items.Select(r => r.Message));

            var incremental = await engine.QueryAsync<LogQueryResult>("logs.query",
                new { category = TestCategory, cursor = all.NextCursor });
            Assert.Empty(incremental.Items);
            Assert.Equal(all.NextCursor, incremental.NextCursor);

            LpLog.Info("第四条", TestCategory);
            var next = await engine.QueryAsync<LogQueryResult>("logs.query",
                new { category = TestCategory, cursor = all.NextCursor });
            Assert.Equal("第四条", Assert.Single(next.Items).Message);
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task 参数校验_非法级别来源上限游标一律明确报错()
    {
        var dir = TempDir();
        var logging = EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir });
        try
        {
            var (engine, _, _) = TestHost.Create();

            var badLevel = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<LogQueryResult>("logs.query", new { level = "verbose" }));
            Assert.Equal(EngineErrors.EnumOutOfRange, badLevel.Error.Code);
            Assert.Contains("trace", badLevel.Error.Details!.Value.GetProperty("allowed")
                .EnumerateArray().Select(e => e.GetString()));   // 详情带合法清单（调用方可自纠）

            var badSource = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<LogQueryResult>("logs.query", new { source = "stdout" }));
            Assert.Equal(EngineErrors.EnumOutOfRange, badSource.Error.Code);

            foreach (var limit in new[] { 0, 5000 })
            {
                var badLimit = await Assert.ThrowsAsync<EngineException>(
                    () => engine.QueryAsync<LogQueryResult>("logs.query", new { limit }));
                Assert.Equal(EngineErrors.EnumOutOfRange, badLimit.Error.Code);
            }

            var badCursor = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<LogQueryResult>("logs.query", new { cursor = -1 }));
            Assert.Equal(EngineErrors.EnumOutOfRange, badCursor.Error.Code);

            var badType = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<LogQueryResult>("logs.query", new { level = 3 }));
            Assert.Equal(EngineErrors.TypeMismatch, badType.Error.Code);

            var missing = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<LogsLevelResult>("logs.level"));
            Assert.Equal(EngineErrors.RequiredParam, missing.Error.Code);

            var badLevelWrite = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<LogsLevelResult>("logs.level", new { level = "3" }));   // 数字形态不认
            Assert.Equal(EngineErrors.EnumOutOfRange, badLevelWrite.Error.Code);

            // 全部是**调用前校验**：参数错误不得留下任何副作用（级别未被动过）
            Assert.Equal(LogLevel.Info, logging.Level);
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task 调级_立即生效_文件源可用()
    {
        var dir = TempDir();
        var logging = EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Info });
        try
        {
            var (engine, _, _) = TestHost.Create();

            LpLog.Debug("info 级下看不到", TestCategory);
            var raised = await engine.ExecuteAsync<LogsLevelResult>("logs.level", new { level = "debug" });
            Assert.Equal("debug", raised.Data!.Level);
            Assert.Equal("info", raised.Data!.Previous);
            Assert.Equal(LogLevel.Debug, logging.Level);   // 管道即时生效

            LpLog.Debug("调级后看得见", TestCategory);

            var memory = await engine.QueryAsync<LogQueryResult>("logs.query", new { category = TestCategory });
            Assert.Equal(LogLevel.Debug, memory.MinimumLevel);
            Assert.Contains(memory.Items, r => r.Message == "调级后看得见");
            Assert.DoesNotContain(memory.Items, r => r.Message == "info 级下看不到");

            // 文件源：命令侧只验"确实接到了文件读侧"（内容级断言在 Diagnostics 层的确定性口径里）
            var fromFile = await engine.QueryAsync<LogQueryResult>("logs.query", new { source = "file" });
            Assert.Equal(LogSource.File, fromFile.Source);
            Assert.True(fromFile.FilesRead >= 1, "文件源应实际回读到日志文件");
            Assert.NotEmpty(fromFile.Items);

            // 调高到 warn：之后的 Debug 不再记录（环里已收下的历史记录不会被追溯剔除——过滤发生在写入时）
            var backUp = await engine.ExecuteAsync<LogsLevelResult>("logs.level", new { level = "warn" });
            Assert.Equal("warn", backUp.Data!.Level);
            Assert.Equal("debug", backUp.Data!.Previous);

            LpLog.Debug("调高后不再记录", TestCategory);
            var afterFilter = await engine.QueryAsync<LogQueryResult>("logs.query",
                new { category = TestCategory, level = "debug" });
            Assert.DoesNotContain(afterFilter.Items, r => r.Message == "调高后不再记录");

            // 切换公告（category = logs.level）在 warn 口径下仍然可见——切换动作自己留痕
            var announcement = await engine.QueryAsync<LogQueryResult>("logs.query",
                new { category = "logs.level" });
            Assert.Contains(announcement.Items, r => r.Message.Contains("日志最低级别已切换"));
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(TempArea.Resolve(), "logs-cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
    }
}

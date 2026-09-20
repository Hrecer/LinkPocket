using System.Text;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>
/// <c>logs.query</c> 的实现层语义（S2b）：两个来源各自的过滤口径 + **运行期调级**（logs.level 的底座）。
/// 断言一律落在可观测结果（返回记录 / 计数 / 读取的文件数），不测内部实现。
/// </summary>
public class LogQueryTests
{
    private static LogRecord Rec(LogLevel level, string message, string category = "test")
        => new(DateTimeOffset.UtcNow, level, category, message, Environment.CurrentManagedThreadId);

    [Fact]
    public void 内存源_按级别分类游标与上限过滤_返回时间升序()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Trace }, memory);

        pipeline.Write(Rec(LogLevel.Info, "i1", "ui"));
        pipeline.Write(Rec(LogLevel.Warn, "w1", "engine.pipeline"));
        pipeline.Write(Rec(LogLevel.Info, "i2", "ui"));

        var all = pipeline.Query(new LogQuery());
        Assert.Equal(new[] { "i1", "w1", "i2" }, all.Items.Select(r => r.Message));   // 时间升序（写入顺序）
        Assert.Equal(LogSource.Memory, all.Source);
        Assert.Equal(LogLevel.Trace, all.MinimumLevel);
        Assert.Equal(200, all.Limit);                                        // 回显生效值
        Assert.Equal(all.Items[^1].Sequence, all.NextCursor);                // 游标推进到环内最新

        var warn = pipeline.Query(new LogQuery(MinimumLevel: LogLevel.Warn));
        Assert.Equal("w1", Assert.Single(warn.Items).Message);

        var byCategory = pipeline.Query(new LogQuery(Category: "ui"));
        Assert.Equal(new[] { "i1", "i2" }, byCategory.Items.Select(r => r.Message));

        var limited = pipeline.Query(new LogQuery(Limit: 2));
        Assert.Equal(new[] { "i1", "w1" }, limited.Items.Select(r => r.Message));

        var afterFirst = pipeline.Query(new LogQuery(Cursor: all.Items[0].Sequence));
        Assert.Equal(new[] { "w1", "i2" }, afterFirst.Items.Select(r => r.Message));
    }

    [Fact]
    public void 文件源_从最新文件向前回读_跳过残行并计数()
    {
        var dir = TempDir();
        try
        {
            var options = new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Trace };
            using var sink = new JsonlFileSink(options);
            using var pipeline = new LogPipeline(options, sink, new MemoryLogSink(50));

            for (var i = 0; i < 5; i++) pipeline.Write(Rec(LogLevel.Info, $"f{i}"));
            pipeline.Flush(TimeSpan.FromSeconds(2));

            // 模拟"进程被杀"留下的半截行（写侧长开句柄在跑 → 必须以 FileShare.ReadWrite 打开）
            var path = Assert.Single(sink.Files);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.WriteLine("{ 半截");

            var tail = pipeline.Query(new LogQuery(Source: LogSource.File, Limit: 3));
            Assert.Equal(LogSource.File, tail.Source);
            Assert.Equal(new[] { "f2", "f3", "f4" }, tail.Items.Select(r => r.Message));   // 最近 3 条，仍按时间升序
            Assert.True(tail.MoreAvailable, "还有更早的 f0 / f1 未回读");
            Assert.Null(tail.NextCursor);                                          // 文件源无进程内游标
            Assert.Equal(1, tail.FilesRead);

            var full = pipeline.Query(new LogQuery(Source: LogSource.File, Limit: 10));
            Assert.Equal(new[] { "f0", "f1", "f2", "f3", "f4" }, full.Items.Select(r => r.Message));
            Assert.Equal(1, full.SkippedLines);   // 残行被跳过但**计数暴露**（不静默）
            Assert.False(full.MoreAvailable);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 运行期调级_立即生效于过滤与读数()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Info }, memory);

        Assert.Equal(LogLevel.Info, pipeline.Level);          // 初值 = 装配口径
        Assert.Equal(LogLevel.Info, pipeline.Stats.Level);
        Assert.False(pipeline.IsEnabled(LogLevel.Debug));

        Assert.Equal(LogLevel.Info, pipeline.SetLevel(LogLevel.Debug));   // 返回切换前的级别
        Assert.Equal(LogLevel.Debug, pipeline.Level);
        Assert.Equal(LogLevel.Debug, pipeline.Stats.Level);
        Assert.True(pipeline.IsEnabled(LogLevel.Debug));

        pipeline.Write(Rec(LogLevel.Debug, "调级后可见"));
        pipeline.Flush(TimeSpan.FromSeconds(2));
        var after = pipeline.Query(new LogQuery());
        Assert.Equal("调级后可见", Assert.Single(after.Items).Message);
        Assert.Equal(LogLevel.Debug, after.MinimumLevel);
        Assert.Equal(0, pipeline.Stats.Filtered);

        pipeline.SetLevel(LogLevel.Error);
        pipeline.Write(Rec(LogLevel.Warn, "调高后被过滤"));
        pipeline.Flush(TimeSpan.FromSeconds(2));
        Assert.Equal(1, pipeline.Stats.Filtered);
        Assert.Single(pipeline.Query(new LogQuery()).Items);   // 环里仍只有那一条
    }

    [Fact]
    public void 无对应能力位时_查询返回空且不抛()
    {
        // 只挂一个"什么都不实现"的落点：查询必须给出空结果（能力位缺失 ≠ 报错；"未装配"由门面层报错负责）
        using var pipeline = new LogPipeline(new LoggingOptions(), new CountingSink());

        Assert.Empty(pipeline.Query(new LogQuery()).Items);

        var file = pipeline.Query(new LogQuery(Source: LogSource.File));
        Assert.Empty(file.Items);
        Assert.Equal(0, file.FilesRead);
    }

    private sealed class CountingSink : ILogSink
    {
        public bool IsEnabled(LogLevel level) => true;
        public void Write(LogRecord record) { }
        public void Flush(TimeSpan timeout) { }
        public LogStats Stats => new();
    }

    private static string TempDir()
    {
        var dir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "log-query-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
    }
}

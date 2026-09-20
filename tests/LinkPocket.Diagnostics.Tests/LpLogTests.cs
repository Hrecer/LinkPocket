using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>门面语义：未装配不静默（计数）/ 装配后记录携带分类·调用成员·作用域 / 结构化入口 / Shutdown 收尾。
/// ⚠️ 本类独占 <see cref="LpLog"/> 静态状态（xunit 类内串行）：每个用例自装自卸。</summary>
public class LpLogTests
{
    [Fact]
    public void 未装配管道_丢弃计数且调用方无异常()
    {
        LpLog.Configure(null);
        var before = LpLog.UnconfiguredDrops;

        LpLog.Error("落点未装配也不许抛", new InvalidOperationException("x"));

        Assert.True(LpLog.UnconfiguredDrops > before, "未装配期间的记录必须计数（不静默）");
        Assert.Null(LpLog.Stats);
        Assert.False(LpLog.IsEnabled(LogLevel.Info));
    }

    [Fact]
    public void 装配后_记录携带分类与调用成员与作用域()
    {
        var memory = new MemoryLogSink(100);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Debug }, memory);
        LpLog.Configure(pipeline);
        try
        {
            using (LpLog.BeginScope(("corr", "c1"), ("sess", "s1")))
            using (LpLog.BeginScope(("corr", "c2")))
            {
                LpLog.Info("hello");
            }

            pipeline.Flush(TimeSpan.FromSeconds(2));
            var record = Assert.Single(memory.Snapshot());
            Assert.Equal("hello", record.Message);
            Assert.Equal(LpLog.DefaultCategory, record.Category);
            Assert.Equal("c2", record.Scope!["corr"]);     // 内层覆盖外层
            Assert.Equal("s1", record.Scope!["sess"]);
            Assert.Equal(nameof(装配后_记录携带分类与调用成员与作用域), record.Props!["at"]);
        }
        finally
        {
            LpLog.Configure(null);
        }
    }

    [Fact]
    public void 结构化入口_显式分类与字段与错误载荷()
    {
        var memory = new MemoryLogSink(100);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Debug }, memory);
        LpLog.Configure(pipeline);
        try
        {
            LpLog.Write(LogLevel.Warn, "engine.observe", "观测面失败（已提交写仍返回成功）",
                new InvalidOperationException("boom"),
                new Dictionary<string, object?> { ["corr"] = "abc", ["what"] = "写审计失败" });

            pipeline.Flush(TimeSpan.FromSeconds(2));
            var record = Assert.Single(memory.Snapshot());
            Assert.Equal("engine.observe", record.Category);
            Assert.Equal(LogLevel.Warn, record.Level);
            Assert.Equal("abc", record.Props!["corr"]);
            Assert.Equal("boom", record.Error!.Message);
            Assert.Contains(nameof(InvalidOperationException), record.Error!.Type);
        }
        finally
        {
            LpLog.Configure(null);
        }
    }

    [Fact]
    public void 级别快捷方法_错误级携带异常且信息级不携带()
    {
        var memory = new MemoryLogSink(100);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Trace }, memory);
        LpLog.Configure(pipeline);
        try
        {
            LpLog.Debug("调试");
            LpLog.Warn("警告");
            LpLog.Error("错误", new InvalidOperationException("x"));
            LpLog.Fatal("致命");

            pipeline.Flush(TimeSpan.FromSeconds(2));
            var records = memory.Snapshot();
            Assert.Equal(4, records.Count);
            Assert.Null(records[0].Error);
            Assert.NotNull(records[2].Error);
            Assert.Equal(LogLevel.Fatal, records[3].Level);
        }
        finally
        {
            LpLog.Configure(null);
        }
    }

    [Fact]
    public void Shutdown_刷盘并卸下管道_幂等()
    {
        var memory = new MemoryLogSink(10);
        var pipeline = new LogPipeline(new LoggingOptions(), memory);
        LpLog.Configure(pipeline);

        LpLog.Info("退出前的最后一条");
        LpLog.Shutdown();
        LpLog.Shutdown();   // 幂等：不得抛

        Assert.Null(LpLog.Sink);
        Assert.Equal("退出前的最后一条", Assert.Single(memory.Snapshot()).Message);
    }
}

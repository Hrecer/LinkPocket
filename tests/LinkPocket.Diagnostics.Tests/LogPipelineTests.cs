using LinkPocket.Contracts;
using LinkPocket.Diagnostics;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>管道语义：级别过滤 / 序号富化 / 溢出丢弃计数 / error+fatal 直写旁路 / 落点失败隔离 / 排空刷盘 / 脱敏。</summary>
public class LogPipelineTests
{
    private static LogRecord Rec(LogLevel level, string message, params (string Key, object? Value)[] props)
        => new(DateTimeOffset.UtcNow, level, "test", message, Environment.CurrentManagedThreadId,
            Props: props.Length == 0 ? null : props.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void 低于最低级别的记录_被过滤并计数()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Warn }, memory);

        pipeline.Write(Rec(LogLevel.Info, "被过滤"));
        pipeline.Write(Rec(LogLevel.Warn, "通过"));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        Assert.Equal(1, pipeline.Stats.Filtered);
        var record = Assert.Single(memory.Snapshot());
        Assert.Equal("通过", record.Message);
    }

    [Fact]
    public void 富化_分配单调序号与线程号()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Debug }, memory);

        pipeline.Write(Rec(LogLevel.Debug, "a"));
        pipeline.Write(Rec(LogLevel.Debug, "b"));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        var records = memory.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.True(records[1].Sequence > records[0].Sequence);
        Assert.All(records, r => Assert.True(r.ThreadId > 0));
        Assert.Equal(2, pipeline.Stats.Accepted);
        Assert.Equal(2, pipeline.Stats.Written);
    }

    [Fact]
    public void 队列满_普通记录丢弃并计数()
    {
        var gate = new GatedSink(blockFirst: 1);
        using var pipeline = new LogPipeline(
            new LoggingOptions { MinimumLevel = LogLevel.Info, QueueCapacity = 2 }, gate, new MemoryLogSink(50));

        pipeline.Write(Rec(LogLevel.Info, "first"));
        Assert.True(gate.WaitEntered(TimeSpan.FromSeconds(5)), "泵应已取走第一条并阻塞在落点");
        pipeline.Write(Rec(LogLevel.Info, "q1"));
        pipeline.Write(Rec(LogLevel.Info, "q2"));
        pipeline.Write(Rec(LogLevel.Info, "overflow"));

        Assert.Equal(1, pipeline.Stats.Dropped);
        gate.Open();
        pipeline.Flush(TimeSpan.FromSeconds(5));
        Assert.Equal(3, gate.Records.Count);
        Assert.DoesNotContain(gate.Records, r => r.Message == "overflow");
    }

    [Fact]
    public void 队列满_错误级记录走直写旁路不丢()
    {
        var gate = new GatedSink(blockFirst: 1);
        using var pipeline = new LogPipeline(
            new LoggingOptions { MinimumLevel = LogLevel.Info, QueueCapacity = 1 }, gate, new MemoryLogSink(50));

        pipeline.Write(Rec(LogLevel.Info, "first"));
        Assert.True(gate.WaitEntered(TimeSpan.FromSeconds(5)));
        pipeline.Write(Rec(LogLevel.Info, "q1"));            // 占满队列
        pipeline.Write(Rec(LogLevel.Error, "boom"));         // 直写旁路（同步落到落点）

        Assert.Equal(1, pipeline.Stats.Dropped);
        Assert.Equal(1, pipeline.Stats.DirectWrites);
        Assert.Single(gate.Records, r => r.Message == "boom");

        gate.Open();
        pipeline.Flush(TimeSpan.FromSeconds(5));
        Assert.Equal(3, gate.Records.Count);
    }

    [Fact]
    public void 落点抛异常_计数暴露且不影响其余落点与调用方()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Debug }, new ThrowingSink(), memory);

        pipeline.Write(Rec(LogLevel.Info, "hello"));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        Assert.Equal(1, pipeline.Stats.Failed);
        Assert.False(string.IsNullOrEmpty(pipeline.Stats.LastError));
        Assert.Single(memory.Snapshot());
    }

    [Fact]
    public void 消息脱敏_敏感查询串被掩码且超长截断()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Debug, MaxMessageLength = 40 }, memory);

        pipeline.Write(Rec(LogLevel.Info, "打开 https://x.test/p?token=secret0123&a=1 失败"));
        pipeline.Write(Rec(LogLevel.Info, new string('长', 100)));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        var records = memory.Snapshot();
        Assert.Contains("token=***", records[0].Message);
        Assert.DoesNotContain("secret0123", records[0].Message);
        Assert.Contains("...(truncated)", records[1].Message);
    }

    [Fact]
    public void 关闭脱敏_只截断不掩码()
    {
        var memory = new MemoryLogSink(50);
        using var pipeline = new LogPipeline(
            new LoggingOptions { MinimumLevel = LogLevel.Debug, Redact = false, MaxMessageLength = 200 }, memory);

        pipeline.Write(Rec(LogLevel.Info, "https://x.test/p?token=secret0123"));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        Assert.Contains("secret0123", Assert.Single(memory.Snapshot()).Message);
    }

    [Fact]
    public void 清空文件与文件清单_委托给具备维护能力的落点()
    {
        var dir = TempDir();
        try
        {
            var file = new JsonlFileSink(new LoggingOptions { Directory = dir });
            using var pipeline = new LogPipeline(new LoggingOptions { Directory = dir }, file, new MemoryLogSink(10));
            pipeline.Write(Rec(LogLevel.Info, "x"));
            pipeline.Flush(TimeSpan.FromSeconds(2));
            Assert.Single(pipeline.Files);

            Assert.Equal(1, pipeline.ClearFiles());
            Assert.Empty(pipeline.Files);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "diag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
    }

    /// <summary>可控阻塞落点：只阻塞前 N 次写入（用于把队列"钉"在满的状态，断言确定性）。</summary>
    private sealed class GatedSink(int blockFirst) : ILogSink
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _gate = new(false);
        private readonly object _lock = new();
        private readonly List<LogRecord> _records = [];
        private int _remainingBlocks = blockFirst;

        public bool WaitEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Open() => _gate.Set();

        public IReadOnlyList<LogRecord> Records
        {
            get { lock (_lock) return [.. _records]; }
        }

        public bool IsEnabled(LogLevel level) => true;

        public void Write(LogRecord record)
        {
            if (Interlocked.Decrement(ref _remainingBlocks) >= 0)
            {
                _entered.Set();
                _gate.Wait(TimeSpan.FromSeconds(10));
            }
            lock (_lock) _records.Add(record);
        }

        public void Flush(TimeSpan timeout) { }

        public LogStats Stats => new();
    }

    private sealed class ThrowingSink : ILogSink
    {
        public bool IsEnabled(LogLevel level) => true;
        public void Write(LogRecord record) => throw new InvalidOperationException("落点故障（注入）");
        public void Flush(TimeSpan timeout) { }
        public LogStats Stats => new();
    }
}

using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>
/// 文本流落点（无头/服务型宿主的 **stderr** 通道）：与文件落点**同一 schema**（一行一条 JSONL）、
/// error 立即刷出、写失败**计数暴露**且绝不抛给业务调用方。
/// </summary>
public class JsonlTextWriterSinkTests
{
    private static LogRecord Rec(LogLevel level, string message)
        => new(DateTimeOffset.UtcNow, level, "headless", message, Environment.CurrentManagedThreadId);

    [Fact]
    public void 一行一条JSONL_与文件落点同schema()
    {
        var buffer = new StringWriter();
        using var pipeline = new LogPipeline(new LoggingOptions { MinimumLevel = LogLevel.Info },
            new JsonlTextWriterSink(buffer));

        pipeline.Write(Rec(LogLevel.Info, "引擎就绪"));
        pipeline.Write(Rec(LogLevel.Error, "失败示例"));
        pipeline.Flush(TimeSpan.FromSeconds(2));

        var lines = buffer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, pipeline.Stats.Written);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("info", first.RootElement.GetProperty("level").GetString());
        Assert.Equal("引擎就绪", first.RootElement.GetProperty("msg").GetString());
        Assert.Equal("headless", first.RootElement.GetProperty("cat").GetString());
        Assert.True(first.RootElement.GetProperty("seq").GetInt64() > 0);   // 序号由管道分配

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("error", second.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void 首类字段_照搬记录的顶层键()
    {
        // corr / cmd / caller 是**首类字段** → JSONL 顶层键（跨源关联的根据，不再塞进 props）
        var buffer = new StringWriter();
        new JsonlTextWriterSink(buffer).Write(new LogRecord(
            DateTimeOffset.UtcNow, LogLevel.Info, "engine.call", "调用完成：folders.create",
            Environment.CurrentManagedThreadId,
            CorrelationId: "corr-9", Caller: "ui:-", Command: "folders.create",
            Props: new Dictionary<string, object?> { ["audit_ref"] = "a-1", ["ms"] = 7 }));

        using var doc = JsonDocument.Parse(buffer.ToString().Trim());
        var root = doc.RootElement;
        Assert.Equal("corr-9", root.GetProperty("corr").GetString());
        Assert.Equal("folders.create", root.GetProperty("cmd").GetString());
        Assert.Equal("ui:-", root.GetProperty("caller").GetString());
        Assert.Equal("a-1", root.GetProperty("props").GetProperty("audit_ref").GetString());
    }

    [Fact]
    public void 写失败_计数暴露且不抛()
    {
        var sink = new JsonlTextWriterSink(new ThrowingWriter());

        sink.Write(Rec(LogLevel.Info, "注定失败"));   // 不得抛
        sink.Flush(TimeSpan.FromSeconds(1));          // 同样不得抛

        Assert.Equal(2, sink.Stats.Failed);   // Write 与 Flush 各失败一次（都计数暴露）
        Assert.False(string.IsNullOrEmpty(sink.Stats.LastError));
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("流已关闭");
        public override void Flush() => throw new IOException("流已关闭");
    }
}

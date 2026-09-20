using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>文件落点：JSONL 格式 / 无 BOM / 按大小轮转 / 保留（按天 + 按总量）/ 清空重开 / 不可用目录的失败暴露。</summary>
public class JsonlFileSinkTests
{
    private static LogRecord Rec(LogLevel level, string message)
        => new(DateTimeOffset.UtcNow, level, "test", message, Environment.CurrentManagedThreadId);

    [Fact]
    public void 一行一条JSON_含稳定核心字段且无BOM()
    {
        var dir = TempDir();
        try
        {
            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir });
            using var pipeline = new LogPipeline(new LoggingOptions { Directory = dir }, sink);
            pipeline.Write(Rec(LogLevel.Info, "第一条"));
            pipeline.Write(Rec(LogLevel.Error, "第二条"));
            pipeline.Flush(TimeSpan.FromSeconds(2));   // 经管道：序号由管道分配，刷盘落到落点

            var path = Assert.Single(sink.Files);
            var bytes = ReadShared(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不得写 UTF-8 BOM");

            var lines = ReadLinesShared(path);
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            var root = first.RootElement;
            Assert.Equal("info", root.GetProperty("level").GetString());
            Assert.Equal("test", root.GetProperty("cat").GetString());
            Assert.Equal("第一条", root.GetProperty("msg").GetString());
            Assert.True(root.GetProperty("seq").GetInt64() > 0);
            Assert.False(string.IsNullOrEmpty(root.GetProperty("ts").GetString()));

            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("error", second.RootElement.GetProperty("level").GetString());
            Assert.Equal("第二条", second.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 按大小轮转_同名日期下按三位序号分段()
    {
        var dir = TempDir();
        try
        {
            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir, MaxFileBytes = 260 });
            for (var i = 0; i < 6; i++) sink.Write(Rec(LogLevel.Info, $"记录 {i}"));
            sink.Flush(TimeSpan.FromSeconds(2));

            var files = sink.Files;
            Assert.True(files.Count >= 2, $"应至少轮转出一个新段（实际 {files.Count}）");
            Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), files);   // 名称升序 = 时间升序
            foreach (var file in files)
                foreach (var line in ReadLinesShared(file))
                    using (JsonDocument.Parse(line)) { }
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 保留_按天淘汰超期文件()
    {
        var dir = TempDir();
        try
        {
            var expired = Path.Combine(dir, "linkpocket-2000-01-01.000.jsonl");
            File.WriteAllText(expired, "{\"seq\":1}\n");

            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir, RetentionDays = 30 });
            sink.Write(Rec(LogLevel.Info, "触发开文件与保留"));
            sink.Flush(TimeSpan.FromSeconds(2));

            Assert.False(File.Exists(expired), "超期文件应被清理");
            Assert.Single(sink.Files);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 保留_按总量从最旧淘汰且绝不删当前文件()
    {
        var dir = TempDir();
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var filler = new string('x', 300);
            foreach (var seq in new[] { 0, 1, 2 })
                File.WriteAllText(Path.Combine(dir, $"linkpocket-{today}.{seq:D3}.jsonl"), filler);

            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir, RetentionMaxBytes = 700 });
            sink.Write(Rec(LogLevel.Info, "触发保留"));
            sink.Flush(TimeSpan.FromSeconds(2));

            Assert.False(File.Exists(Path.Combine(dir, $"linkpocket-{today}.000.jsonl")), "最旧文件应被淘汰");
            Assert.True(File.Exists(sink.Files[^1]), "当前写入的文件必须保留");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 清空_删除全部文件并在下次写入时重开()
    {
        var dir = TempDir();
        try
        {
            var legacy = Path.Combine(dir, "linkpocket-2026-01-01.log");   // 历史格式一并清理
            File.WriteAllText(legacy, "legacy");

            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir });
            sink.Write(Rec(LogLevel.Info, "x"));
            sink.Flush(TimeSpan.FromSeconds(2));
            Assert.Equal(2, sink.Files.Count);

            Assert.Equal(2, sink.ClearFiles());
            Assert.Empty(sink.Files);

            sink.Write(Rec(LogLevel.Info, "重开后仍可写"));
            sink.Flush(TimeSpan.FromSeconds(2));
            Assert.Single(sink.Files);
            Assert.False(File.Exists(legacy), "遗留 .log 不应复活");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 跨日自动轮转_跨零点换新文件且按新日期重算保留()
    {
        var dir = TempDir();
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero));
            var longExpired = Path.Combine(dir, "linkpocket-2026-08-29.000.jsonl");
            var expired = Path.Combine(dir, "linkpocket-2026-08-30.000.jsonl");
            File.WriteAllText(longExpired, "{\"seq\":1}\n");
            File.WriteAllText(expired, "{\"seq\":2}\n");

            using var sink = new JsonlFileSink(new LoggingOptions { Directory = dir, RetentionDays = 1 }, clock);
            sink.Write(Rec(LogLevel.Info, "跨日前最后一条"));
            sink.Flush(TimeSpan.FromSeconds(2));

            // 开文件即重算保留（半开：只有严格早于 08-30 的被清）
            Assert.False(File.Exists(longExpired), "开文件时应按当天重算保留");
            Assert.True(File.Exists(expired), "当天边界内的文件不得误删");
            Assert.EndsWith("linkpocket-2026-08-31.000.jsonl", sink.Files[^1], StringComparison.Ordinal);

            // 跨零点：写入必须换到今天的文件（否则按日命名失真、按天保留永不触发）
            clock.Advance(TimeSpan.FromMinutes(2));
            sink.Write(Rec(LogLevel.Info, "跨日后第一条"));
            sink.Flush(TimeSpan.FromSeconds(2));

            var today = Path.Combine(dir, "linkpocket-2026-09-01.000.jsonl");
            Assert.True(File.Exists(today), "跨日必须自动轮转到新日期的文件");
            Assert.Single(ReadLinesShared(today));
            Assert.Single(ReadLinesShared(Path.Combine(dir, "linkpocket-2026-08-31.000.jsonl")));   // 昨天的不再被追加
            Assert.False(File.Exists(expired), "跨日轮转应重跑保留（08-30 此时已超期）");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void 目录不可用_失败计数暴露且调用方无异常()
    {
        var root = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "diag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "占用路径（使子目录无法创建）");

            using var sink = new JsonlFileSink(new LoggingOptions { Directory = Path.Combine(blocker, "logs") });
            sink.Write(Rec(LogLevel.Info, "注定失败"));   // 不得抛

            Assert.True(sink.Stats.Failed >= 1, "写失败必须计数（观测面铁律：不静默）");
            Assert.False(string.IsNullOrEmpty(sink.Stats.LastError));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "diag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>读"写入中的文件"：必须用 FileShare.ReadWrite 打开（落点持有写句柄）。</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
    }

    /// <summary>可推进的假时钟（跨日轮转的可测性注入点；<see cref="TimeProvider"/> 是 .NET 8 BCL 类型，
    /// 不引 FakeTimeProvider 包）。本地时区固定 UTC，于是"本地日期"== 注入时刻的日期。</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}

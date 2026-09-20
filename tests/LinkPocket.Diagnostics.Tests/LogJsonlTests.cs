using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>
/// JSONL 编解码的**往返**（S2b）：写出的行必须能被 <see cref="LogJsonl.TryParse"/> 解析回同一记录
/// （logs.query 的文件源与外部消费者都吃这个 schema）；半截行 / 非法行必须被**安全拒绝**（返回 false，绝不抛）——
/// 进程被杀时留下的残行是最常见的输入。
/// </summary>
public class LogJsonlTests
{
    private static LogRecord Sample() => new(
        new DateTimeOffset(2026, 9, 20, 7, 30, 15, TimeSpan.Zero),
        LogLevel.Warn, "engine.pipeline", "命令失败：folders.move", 42,
        Sequence: 17,
        CorrelationId: "corr-1",
        SessionId: "sess-1",
        Caller: "ui:-",
        BatchId: "batch-1",
        UndoGroupId: "undo-1",
        Command: "folders.move",
        ElapsedMs: 12,
        Error: new LogError("LinkPocket.Contracts.EngineException", "找不到实体", "LP.STATE.001",
            "   at Foo()", new LogError("System.Exception", "内层原因", null, "   at Bar()")),
        Scope: new Dictionary<string, object?> { ["corr"] = "corr-1", ["depth"] = 2 },
        Props: new Dictionary<string, object?>
        {
            ["at"] = "ExecuteAsync",
            ["count"] = 3L,
            ["ratio"] = 0.5,
            ["flag"] = true,
            ["note"] = "多行\n文本",
            ["none"] = null,
        });

    [Fact]
    public void 往返_写出的行能解析回同一记录且再次写出逐字节一致()
    {
        var line = LogJsonl.ToLine(Sample());
        Assert.DoesNotContain('\n', line);   // 一条记录 = 一行（多行消息由 JSON 转义）

        Assert.True(LogJsonl.TryParse(line, out var parsed), "写出的行必须能解析回来");
        var record = parsed!;

        Assert.Equal(new DateTimeOffset(2026, 9, 20, 7, 30, 15, TimeSpan.Zero), record.Timestamp);
        Assert.Equal(LogLevel.Warn, record.Level);
        Assert.Equal("engine.pipeline", record.Category);
        Assert.Equal("命令失败：folders.move", record.Message);
        Assert.Equal(42, record.ThreadId);
        Assert.Equal(17, record.Sequence);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Equal("sess-1", record.SessionId);
        Assert.Equal("ui:-", record.Caller);
        Assert.Equal("batch-1", record.BatchId);
        Assert.Equal("undo-1", record.UndoGroupId);
        Assert.Equal("folders.move", record.Command);
        Assert.Equal(12, record.ElapsedMs);

        Assert.Equal("LP.STATE.001", record.Error!.Code);
        Assert.Equal("   at Foo()", record.Error.StackTrace);
        Assert.Equal("内层原因", record.Error.Inner!.Message);

        Assert.Equal("corr-1", record.Scope!["corr"]);
        Assert.Equal(2L, record.Scope["depth"]);

        Assert.Equal("ExecuteAsync", record.Props!["at"]);
        Assert.Equal(3L, record.Props["count"]);
        Assert.Equal(0.5, record.Props["ratio"]);
        Assert.Equal(true, record.Props["flag"]);
        Assert.Equal("多行\n文本", record.Props["note"]);
        Assert.Null(record.Props["none"]);

        Assert.Equal(line, LogJsonl.ToLine(record));   // 往返幂等（同一条记录写两次逐字节一致）
    }

    [Fact]
    public void 解析_半截行与非法行一律返回false且不抛()
    {
        Assert.False(LogJsonl.TryParse(null, out _));
        Assert.False(LogJsonl.TryParse("", out _));
        Assert.False(LogJsonl.TryParse("   ", out _));
        Assert.False(LogJsonl.TryParse("{", out _));                       // 半截 JSON（进程被杀）
        Assert.False(LogJsonl.TryParse("""["level","info"]""", out _));    // 非对象
        Assert.False(LogJsonl.TryParse("""{"level":"info"}""", out _));    // 缺 ts / msg
        Assert.False(LogJsonl.TryParse("""{"ts":"2026-09-20T00:00:00+00:00","msg":"x"}""", out _));   // 缺 level
        Assert.False(LogJsonl.TryParse("""{"level":"verbose","ts":"2026-09-20T00:00:00+00:00","msg":"x"}""", out _));  // 非法级别名
        Assert.False(LogJsonl.TryParse("""{"level":"3","ts":"2026-09-20T00:00:00+00:00","msg":"x"}""", out _));        // 数字形态不认
        Assert.False(LogJsonl.TryParse("""{"level":"info","ts":"昨天","msg":"x"}""", out _));                          // 非法时间
    }

    [Fact]
    public void 级别名称_写码与读码共用唯一映射()
    {
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            var name = LogJsonl.LevelName(level);
            Assert.Equal(name, LogLevels.Name(level));
            Assert.True(LogLevels.TryParse(name.ToUpperInvariant(), out var parsed));   // 大小写不敏感
            Assert.Equal(level, parsed);

            // JSON 形态同口径（wire 与 JSONL 文件看到同一个拼写）
            var json = System.Text.Json.JsonSerializer.Serialize(level);
            Assert.Equal($"\"{name}\"", json);
            Assert.Equal(level, System.Text.Json.JsonSerializer.Deserialize<LogLevel>(json));
        }

        Assert.False(LogLevels.TryParse("warning", out _));   // 别名不认（只有一张表）
        Assert.False(LogLevels.TryParse("", out _));
    }
}

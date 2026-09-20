using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>脱敏：文本里的敏感键值掩码 / 非敏感不动 / 字段键名敏感即整体掩码 / 超长截断。</summary>
public class LogRedactorTests
{
    private static LogRecord Rec(string message, params (string Key, object? Value)[] props)
        => new(DateTimeOffset.UtcNow, LogLevel.Info, "test", message, Environment.CurrentManagedThreadId,
            Props: props.Length == 0 ? null : props.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void 文本_敏感键值被掩码()
    {
        var record = LogRedactor.Redact(
            Rec("GET https://x.test/p?token=secret0123&q=ok failed"), maxMessageLength: 4000, maskSensitive: true);

        Assert.Contains("token=***", record.Message);
        Assert.Contains("q=ok", record.Message);
        Assert.DoesNotContain("secret0123", record.Message);
    }

    [Fact]
    public void 文本_非敏感键值不动()
    {
        var record = LogRedactor.Redact(Rec("https://x.test/?q=hello&page=2"), 4000, maskSensitive: true);
        Assert.Equal("https://x.test/?q=hello&page=2", record.Message);
    }

    [Fact]
    public void 字段_键名敏感则值整体掩码()
    {
        var record = LogRedactor.Redact(
            Rec("打开失败", ("password", "p@ssw0rd"), ("url", "https://x.test/?token=abc")),
            4000, maskSensitive: true);

        Assert.Equal("***", record.Props!["password"]);
        Assert.Contains("token=***", (string)record.Props!["url"]!);
    }

    [Fact]
    public void 超长字段值被截断并标记()
    {
        var record = LogRedactor.Redact(Rec("x", ("note", new string('长', 800))), 4000, maskSensitive: true);
        var note = (string)record.Props!["note"]!;
        Assert.True(note.Length <= 520, $"字段值应被截断（实际 {note.Length}）");
        Assert.EndsWith("…(已截断)", note);
    }

    [Fact]
    public void 关闭掩码_不掩码但仍截断()
    {
        var kept = LogRedactor.Redact(Rec("?token=secret0123"), maxMessageLength: 4000, maskSensitive: false);
        Assert.Contains("secret0123", kept.Message);

        var capped = LogRedactor.Redact(Rec(new string('长', 40)), maxMessageLength: 10, maskSensitive: false);
        Assert.EndsWith("…(已截断)", capped.Message);
        Assert.Equal(10 + "…(已截断)".Length, capped.Message.Length);
    }

    [Fact]
    public void 错误载荷_消息同样脱敏()
    {
        var record = Rec("外层") with
        {
            Error = new LogError("System.Exception", "https://x.test/?key=abc123", StackTrace: "at X()"),
        };
        var redacted = LogRedactor.Redact(record, 4000, maskSensitive: true);

        Assert.Contains("key=***", redacted.Error!.Message);
        Assert.DoesNotContain("abc123", redacted.Error!.Message);
        Assert.Equal("at X()", redacted.Error.StackTrace);
    }
}

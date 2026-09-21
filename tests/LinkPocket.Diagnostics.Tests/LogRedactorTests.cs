using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Diagnostics.Tests;

/// <summary>脱敏：文本里的敏感键值掩码 / 非敏感不动 / 字段键名敏感即整体掩码 / 超长截断 /
/// **JSON 文本（审计 args 快照）掩码且结构完好**。</summary>
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
        Assert.EndsWith("...(truncated)", note);
    }

    [Fact]
    public void 关闭掩码_不掩码但仍截断()
    {
        var kept = LogRedactor.Redact(Rec("?token=secret0123"), maxMessageLength: 4000, maskSensitive: false);
        Assert.Contains("secret0123", kept.Message);

        var capped = LogRedactor.Redact(Rec(new string('长', 40)), maxMessageLength: 10, maskSensitive: false);
        Assert.EndsWith("...(truncated)", capped.Message);
        Assert.Equal(10 + "...(truncated)".Length, capped.Message.Length);
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

    [Fact]
    public void JSON_敏感键的标量值被掩码_结构与其余字段不动()
    {
        var json = "{\"url\":\"https://x.test/p?token=secret0123&q=1\",\"password\":\"p@ssw0rd\"," +
                   "\"session_id\":8848,\"title\":\"标题\",\"page\":2,\"ok\":true}";
        var redacted = LogRedactor.RedactJson(json);

        Assert.DoesNotContain("secret0123", redacted);
        Assert.DoesNotContain("p@ssw0rd", redacted);
        Assert.DoesNotContain("8848", redacted);
        Assert.Contains("token=***", redacted);

        // 脱敏不得破坏结构：audit.query 取回的行要能被消费者反序列化
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        Assert.Equal("***", root.GetProperty("password").GetString());
        Assert.Equal("***", root.GetProperty("session_id").GetString());   // 非字符串标量同样掩码
        Assert.Equal("标题", root.GetProperty("title").GetString());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void JSON_容器值不整块吞掉_内部敏感键各自掩码_残缺文本不抛()
    {
        var json = "{\"credentials\":{\"user\":\"甲\",\"password\":\"p@ss\"},\"tags\":[\"a=1\",\"b=2\"]," +
                   "\"note\":\"token=abc123\"}";
        var redacted = LogRedactor.RedactJson(json);

        using var doc = JsonDocument.Parse(redacted);   // 容器若被整块替换会产出半截 JSON —— 这里必须仍可解析
        var root = doc.RootElement;
        Assert.Equal("甲", root.GetProperty("credentials").GetProperty("user").GetString());
        Assert.Equal("***", root.GetProperty("credentials").GetProperty("password").GetString());
        Assert.Equal("a=1", root.GetProperty("tags")[0].GetString());   // 非敏感键值对不动
        Assert.Equal("b=2", root.GetProperty("tags")[1].GetString());
        Assert.Equal("token=***", root.GetProperty("note").GetString());   // 值以敏感键开头也要掩码

        Assert.Equal(string.Empty, LogRedactor.RedactJson(null));
        Assert.Equal(string.Empty, LogRedactor.RedactJson(""));
        Assert.DoesNotContain("abc123", LogRedactor.RedactJson("{\"token\":\"abc123\", \"note\": "));   // 半截也掩码
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// JSONL 编解码（**日志文件格式的唯一实现**）：一条记录 = 一行 JSON（稳定字段序，跨版本只增不改）。
/// 字段（含缺省值约定）：
/// <code>
/// seq   单调整数（进程内；跨进程按 ts 排序）      ts     UTC ISO-8601（往返格式，带 Z）
/// level trace|debug|info|warn|error|fatal        cat    来源分类
/// msg   消息（单行；换行/引号由 JSON 转义）       tid    托管线程 id
/// corr / sess / caller / batch / undo / cmd / ms  上下文（缺省不出现）
/// err   { type, code, msg, stack, inner{type,msg,stack} }（缺省不出现）
/// scope { k: v, ... }（作用域合并结果，键升序）   props { k: v, ... }（附加字段，键升序）
/// </code>
/// 约定：不写 BOM、不写缩进、值一律转义为单行；解析方（logs.query / 外部 AI）按此 schema 消费。
/// </summary>
public static class LogJsonl
{
    /// <summary>记录 → 单行 JSON（不含换行符）。</summary>
    public static string ToLine(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(640);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", record.Sequence);
            writer.WriteString("ts", record.Timestamp.ToUniversalTime().ToString("O"));
            writer.WriteString("level", LevelName(record.Level));
            writer.WriteString("cat", record.Category);
            writer.WriteString("msg", record.Message);
            writer.WriteNumber("tid", record.ThreadId);
            WriteOptionalString(writer, "corr", record.CorrelationId);
            WriteOptionalString(writer, "sess", record.SessionId);
            WriteOptionalString(writer, "caller", record.Caller);
            WriteOptionalString(writer, "batch", record.BatchId);
            WriteOptionalString(writer, "undo", record.UndoGroupId);
            WriteOptionalString(writer, "cmd", record.Command);
            if (record.ElapsedMs is { } ms) writer.WriteNumber("ms", ms);
            if (record.Error is { } error) WriteError(writer, error);
            if (record.Scope is { Count: > 0 } scope) WriteMap(writer, "scope", scope);
            if (record.Props is { Count: > 0 } props) WriteMap(writer, "props", props);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>级别 → 线上名称（小写；稳定枚举，不随枚举顺序变）。
    /// 实现委托契约层的唯一映射 <see cref="LogLevels.Name"/>——写码与回读（<see cref="TryParse"/>）
    /// 共用同一张表，绝不存在第二份。</summary>
    public static string LevelName(LogLevel level) => LogLevels.Name(level);

    private static void WriteError(Utf8JsonWriter writer, LogError error)
    {
        writer.WriteStartObject("err");
        writer.WriteString("type", error.Type);
        writer.WriteString("msg", error.Message);
        WriteOptionalString(writer, "code", error.Code);
        WriteOptionalString(writer, "stack", error.StackTrace);
        if (error.Inner is { } inner)
        {
            writer.WriteStartObject("inner");
            writer.WriteString("type", inner.Type);
            writer.WriteString("msg", inner.Message);
            WriteOptionalString(writer, "stack", inner.StackTrace);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, object?> map)
    {
        writer.WriteStartObject(name);
        foreach (var (key, value) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto.ToUniversalTime().ToString("O")); break;
            case DateTime dt: writer.WriteStringValue(dt.ToUniversalTime().ToString("O")); break;
            case Enum e: writer.WriteStringValue(e.ToString()); break;
            default: writer.WriteStringValue(value.ToString() ?? string.Empty); break;
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) writer.WriteString(name, value);
    }

    // —— 读侧（logs.query 的文件源 / 外部消费者按同一 schema 解析）——

    /// <summary>
    /// 单行 JSON → 记录（<see cref="ToLine"/> 的**逆操作**，两者是往返对）。
    /// 返回 false 的情形：空行 / 非法 JSON / 非对象 / <c>level</c> 缺失或不是合法名称 / <c>ts</c> 缺失或非法时间 / 缺 <c>msg</c>。
    /// **绝不抛异常**——进程被杀时留下的半截行必须能被安全跳过，由调用方**计数暴露**（跳过要可见，不静默）。
    /// 已知边界：<c>scope</c> / <c>props</c> 的值按 JSON 原生类型回归 string / number / bool / null；
    /// 原 DateTimeOffset 值退化为 ISO 字符串，对象/数组值退化为原始 JSON 文本（写侧不会产生后两者，属外部改文件的兜底）。
    /// </summary>
    public static bool TryParse(string? line, out LogRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("level", out var levelElement)
                || !LogLevels.TryParse(levelElement.GetString(), out var level))
                return false;

            if (!root.TryGetProperty("ts", out var tsElement)
                || !DateTimeOffset.TryParse(tsElement.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp))
                return false;

            if (!root.TryGetProperty("msg", out var messageElement)) return false;

            record = new LogRecord(
                timestamp.ToUniversalTime(), level,
                GetString(root, "cat") ?? LpLog.DefaultCategory,
                messageElement.GetString() ?? string.Empty,
                (int)(GetLong(root, "tid") ?? 0),
                Sequence: GetLong(root, "seq") ?? 0,
                CorrelationId: GetString(root, "corr"),
                SessionId: GetString(root, "sess"),
                Caller: GetString(root, "caller"),
                BatchId: GetString(root, "batch"),
                UndoGroupId: GetString(root, "undo"),
                Command: GetString(root, "cmd"),
                ElapsedMs: GetLong(root, "ms"),
                Error: ReadError(root),
                Scope: ReadMap(root, "scope"),
                Props: ReadMap(root, "props"));
            return true;
        }
        catch (JsonException)
        {
            return false;   // 半截行 / 非法 JSON：跳过由调用方计数，绝不抛给读日志的一方
        }
    }

    private static LogError? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("err", out var error) || error.ValueKind != JsonValueKind.Object) return null;

        LogError? inner = null;
        if (error.TryGetProperty("inner", out var innerElement) && innerElement.ValueKind == JsonValueKind.Object)
            inner = new LogError(
                GetString(innerElement, "type") ?? "unknown",
                GetString(innerElement, "msg") ?? string.Empty,
                StackTrace: GetString(innerElement, "stack"));

        return new LogError(
            GetString(error, "type") ?? "unknown",
            GetString(error, "msg") ?? string.Empty,
            Code: GetString(error, "code"),
            StackTrace: GetString(error, "stack"),
            Inner: inner);
    }

    private static IReadOnlyDictionary<string, object?>? ReadMap(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject()) result[property.Name] = ReadValue(property.Value);
        return result.Count == 0 ? null : result;
    }

    private static object? ReadValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        // ⚠️ 两个分支必须显式收敛到 object：`cond ? longValue : doubleValue` 的自然类型是 **double**
        // （会把整数回归成 2.0，往返断言与消费者都读错类型）
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),   // 对象 / 数组：退化为原始 JSON 文本（写侧不产生，外部改文件的兜底）
    };

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : null;
}

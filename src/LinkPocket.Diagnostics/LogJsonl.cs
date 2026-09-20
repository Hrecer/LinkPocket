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

    /// <summary>级别 → 线上名称（小写；稳定枚举，不随枚举顺序变）。</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        LogLevel.Fatal => "fatal",
        _ => "info",
    };

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
}

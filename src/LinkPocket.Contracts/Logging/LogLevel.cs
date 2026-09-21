using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkPocket.Contracts;

/// <summary>日志级别（数值即过滤阈值：低于管道最低级别的记录在入口被过滤并计入 Filtered）。
/// 与常见日志体系同名同序（trace &lt; debug &lt; info &lt; warn &lt; error &lt; fatal）。
/// <para>线上形态 = **小写名称**（见 <see cref="LogLevelJsonConverter"/>）：wire 与 JSONL 文件同口径，
/// 外部消费者（AI / 排障工具）看到的是同一个拼写。</para></summary>
[JsonConverter(typeof(LogLevelJsonConverter))]
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

/// <summary>
/// 级别名称的**唯一映射**（线上形态 = 小写 <c>trace|debug|info|warn|error|fatal</c>）：
/// JSONL 写码（<c>LogJsonl.ToLine</c>）、日志文件回读（<c>LogJsonl.TryParse</c>）、诊断读数
/// （<c>diagnostics.collect.logging.level</c>）、JSON 序列化（<see cref="LogLevelJsonConverter"/>）
/// 与命令入参解析（<c>logs.query.level</c> / <c>logs.level.level</c>）**全部经这里**——
/// 不存在第二份名称表，写码侧与读侧不会漂移。
/// 解析**只认名称**（大小写不敏感），不认数字（<c>Enum.TryParse</c> 会接受 "3" 这类历史形状——零兼容，不猜意图）；
/// 非法名称返回 false，由调用方报 <c>LP.VAL.003</c>。
/// </summary>
public static class LogLevels
{
    /// <summary>级别 → 线上名称（小写）。</summary>
    public static string Name(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        LogLevel.Fatal => "fatal",
        _ => "info",
    };

    /// <summary>线上名称 → 级别（大小写不敏感）。非法名称（含数字形态）= false，绝不猜意图。</summary>
    public static bool TryParse(string? name, out LogLevel level)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn": level = LogLevel.Warn; return true;
            case "error": level = LogLevel.Error; return true;
            case "fatal": level = LogLevel.Fatal; return true;
            default: level = LogLevel.Info; return false;
        }
    }

    /// <summary>全部合法名称（错误详情与目录说明复用，避免各处再抄一遍清单）。</summary>
    public static IReadOnlyList<string> Names { get; } = ["trace", "debug", "info", "warn", "error", "fatal"];
}

/// <summary>
/// 级别的 JSON 形态（读写成小写名称，与 JSONL 文件一致）。
/// 非法名称 → <see cref="JsonException"/>（绝不静默退回缺省级别——那会把"传错了"读成"按 info 过滤"）。
/// </summary>
public sealed class LogLevelJsonConverter : JsonConverter<LogLevel>
{
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && LogLevels.TryParse(reader.GetString(), out var level))
            return level;

        throw new JsonException($"log level is not a valid name; expected one of {string.Join(" / ", LogLevels.Names)}");
    }

    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options)
        => writer.WriteStringValue(LogLevels.Name(value));
}

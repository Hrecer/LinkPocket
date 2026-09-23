using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkPocket.Contracts;

/// <summary>
/// 脱敏（**唯一实现**）：日志与审计共用的敏感信息掩码器——"可被 AI / 脚本消费的读面"（日志文件、
/// <c>logs.query</c>、<c>audit.query</c> 的 args）不能把 URL 里的 token / 密码类内容原样落盘。
///
/// <para><b>为什么在契约层</b>：两个调用方分别是不引 Diagnostics 的 <c>LinkPocket.Engine</c>（审计入参快照）
/// 与 Diagnostics 的日志管道。实现项目 <c>LinkPocket.Diagnostics</c> 只被组合根引用（架构约束），
/// 于是"两侧都能到达"的位置只有契约层；两份实现必然走样，所以这里是唯一一份。</para>
///
/// <para><b>规则</b>：键名命中 <see cref="IsSensitiveKey"/>（大小写不敏感）→ 值整体掩码为 <c>***</c>；
/// 文本里的 <c>key=value</c> 形态（URL 查询串等）→ 只掩码 value；超长（消息按选项 / 字段值 500 字符）→ 截断并标记。
/// JSON 文本见 <see cref="RedactJson(string)"/>（结构化遍历，键名与结构保持可解析）。
/// 文本脱敏只做最小必要变换：非敏感内容原样保留，键名与 JSON 结构不动（读侧仍可解析）。</para>
/// </summary>
public static class LogRedactor
{
    private const string Mask = "***";
    private const int MaxFieldLength = 500;
    private const string TruncatedMarker = "...(truncated)";

    /// <summary>文本里的 <c>key=value</c>（前缀限 <c>? &amp; ; 空白 引号</c> 或行首；值不含引号与分隔符）。
    /// 引号入前缀是为了覆盖"值以敏感键开头"的形态（JSON 字符串值 / 日志里的引用串），
    /// 且不会误伤 JSON 键——键后面跟的是 <c>:</c> 而不是 <c>=</c>。</summary>
    private static readonly Regex SensitivePair = new(
        @"(?<prefix>[?&;\s""]|^)(?<key>[A-Za-z_][A-Za-z0-9_.-]*)(?<sep>=)(?<value>[^&\s;""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>JSON 的 <c>"key": 值</c>，值只取**标量**（字符串 / 数字 / true / false / null）。
    /// **只服务纯文本兜底**（无法解析的残缺文本）：能解析的 JSON 一律走结构化遍历，见 <see cref="RedactJson(JsonElement)"/>。</summary>
    private static readonly Regex JsonScalarPair = new(
        @"(?<head>""(?<key>(?:[^""\\]|\\.)*)""\s*:\s*)(?<value>""(?:[^""\\]|\\.)*""|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveKeyName = new(
        @"(^|[_.-])(token|key|apikey|api_key|access_key|accesskey|secret|password|passwd|pwd|auth|authorization|signature|sig|session|sessionid|credential|ticket)($|[_.-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>整体脱敏（掩码 + 截断）；<paramref name="maskSensitive"/> = false 时只截断（关脱敏的降级路径）。</summary>
    public static LogRecord Redact(LogRecord record, int maxMessageLength, bool maskSensitive)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            Message = Clean(record.Message, maxMessageLength, maskSensitive),
            Error = CleanError(record.Error, maxMessageLength, maskSensitive),
            Scope = CleanMap(record.Scope, maskSensitive),
            Props = CleanMap(record.Props, maskSensitive),
        };
    }

    /// <summary>
    /// JSON 脱敏（**主口径**：调用方手里已有 <see cref="JsonElement"/> 时用它，省掉一次解析——引擎的入参快照就是这种情形）。
    /// 逐个属性判定：键名敏感的**标量值**整体掩码（<c>"token":"abc"</c> / <c>"session_id":12</c> → <c>"***"</c>）；
    /// 其余**字符串值**在**解码后的文本**上做键值对掩码——这一步必须在解码后做：JSON 文本里 URL 的 <c>&amp;</c>
    /// 是 <c>\u0026</c>，纯文本规则看不见它后面的键值对（实测踩中，见 开发文档）。
    /// 容器值不被整块吞掉（吞掉会产出半截 JSON），而是下钻由内部各键各自判定。
    /// <b>输出是紧凑 JSON</b>（键序不变；未改动的字符串沿用原文，改动过的重新编码），**不截断**——长度裁量归调用方。
    /// </summary>
    public static string RedactJson(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendElement(builder, element, keyName: null);
        return builder.ToString();
    }

    /// <summary>JSON 文本脱敏（便捷入口）：合法 JSON 走结构化路径；半截 / 非法文本退到纯文本规则尽力掩码，**绝不抛**。</summary>
    public static string RedactJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json ?? string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            return RedactJson(document.RootElement);
        }
        catch (JsonException)
        {
            return MaskRawText(json);
        }
    }

    private static void AppendElement(StringBuilder builder, JsonElement element, string? keyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendElement(builder, property.Value, property.Name);
                }
                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0) builder.Append(',');
                    AppendElement(builder, item, keyName);   // 数组元素沿用父键名（"tokens":["a","b"] 逐个掩码）
                }
                builder.Append(']');
                break;

            case JsonValueKind.String:
                var raw = element.GetString() ?? string.Empty;
                var cleaned = IsSensitiveKey(keyName ?? string.Empty) ? Mask : Clean(raw, maxLength: 0, maskSensitive: true);
                builder.Append(string.Equals(cleaned, raw, StringComparison.Ordinal)
                    ? element.GetRawText()   // 没改动 → 沿用原文（含原转义），审计快照的字节尽可能稳定
                    : JsonSerializer.Serialize(cleaned));
                break;

            default:
                // 数字 / true / false / null：键名敏感同样掩码（凭证偶尔以数字出现）
                builder.Append(IsSensitiveKey(keyName ?? string.Empty) ? $"\"{Mask}\"" : element.GetRawText());
                break;
        }
    }

    /// <summary>纯文本兜底（仅用于无法解析的 JSON）：能掩多少掩多少，绝不抛。</summary>
    private static string MaskRawText(string text)
    {
        var result = text;
        if (result.IndexOf('"') >= 0)
            result = JsonScalarPair.Replace(result, m => IsSensitiveKey(m.Groups["key"].Value)
                ? $"{m.Groups["head"].Value}\"{Mask}\""
                : m.Value);
        if (result.IndexOf('=') >= 0)
            result = SensitivePair.Replace(result, m => IsSensitiveKey(m.Groups["key"].Value)
                ? $"{m.Groups["prefix"].Value}{m.Groups["key"].Value}={Mask}"
                : m.Value);
        return result;
    }

    /// <summary>键名是否敏感（props / scope / JSON 键的值整体掩码判据）。</summary>
    public static bool IsSensitiveKey(string name) => SensitiveKeyName.IsMatch(name);

    private static string Clean(string text, int maxLength, bool maskSensitive)
    {
        var result = text;
        if (maskSensitive && result.IndexOf('=') >= 0)
            result = SensitivePair.Replace(result, m => IsSensitiveKey(m.Groups["key"].Value)
                ? $"{m.Groups["prefix"].Value}{m.Groups["key"].Value}={Mask}"
                : m.Value);
        if (maxLength > 0 && result.Length > maxLength)
            result = string.Concat(result.AsSpan(0, maxLength), TruncatedMarker);
        return result;
    }

    private static LogError? CleanError(LogError? error, int maxLength, bool maskSensitive)
    {
        if (error is null) return null;
        return error with
        {
            Message = Clean(error.Message, maxLength, maskSensitive),
            Inner = error.Inner is { } inner ? inner with { Message = Clean(inner.Message, maxLength, maskSensitive) } : null,
        };
    }

    private static IReadOnlyDictionary<string, object?>? CleanMap(
        IReadOnlyDictionary<string, object?>? map, bool maskSensitive)
    {
        if (map is null || map.Count == 0) return map;

        Dictionary<string, object?>? copy = null;
        foreach (var (key, value) in map)
        {
            var cleaned = value;
            if (maskSensitive && IsSensitiveKey(key)) cleaned = Mask;
            else if (value is string s)
            {
                var next = Clean(s, MaxFieldLength, maskSensitive);
                if (!ReferenceEquals(next, s)) cleaned = next;
            }
            if (!Equals(cleaned, value))
            {
                copy ??= new Dictionary<string, object?>(map, StringComparer.Ordinal);
                copy[key] = cleaned;
            }
        }
        return copy ?? map;
    }
}

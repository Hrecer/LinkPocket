using System.Text.RegularExpressions;
using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// 日志脱敏（**唯一实现**，管道内的单点 choke point）：掩码敏感键的查询串值与字段值，
/// 并截断超长文本。目的：日志文件与内存环（<c>logs.query</c>）是"可被 AI/脚本消费"的面，
/// 不能把 URL 里的 token / 密码类内容原样落盘。
/// 规则：键名命中 <see cref="SensitiveKeys"/>（大小写不敏感）→ 值整体掩码；
/// 文本里的 <c>key=value</c> 形态 → 只掩码 value；超长（消息按选项 / 字段值 500 字符）→ 截断并标记。
/// </summary>
public static class LogRedactor
{
    private const string Mask = "***";
    private const int MaxFieldLength = 500;
    private const string TruncatedMarker = "…(已截断)";

    private static readonly Regex SensitivePair = new(
        @"(?<prefix>[?&;\s]|^)(?<key>[A-Za-z_][A-Za-z0-9_.-]*)(?<sep>=)(?<value>[^&\s;""']+)",
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

    /// <summary>键名是否敏感（props / scope 的值整体掩码判据）。</summary>
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

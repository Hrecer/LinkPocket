using System.Globalization;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Cli;

/// <summary>
/// 把 <c>--key value</c> 绑定为引擎入参对象。标量按描述符参数类型转换（整数 / 布尔 / 小数）；
/// 集合参数接受 JSON 数组字面量或逗号分隔串；带 Schema 片段（复杂/嵌套，如查询过滤、批脚本）
/// 与 JsonElement 参数按 JSON 解析；描述符里查不到的名字按字面量猜（先试 JSON 字面量，再按字符串）。
/// 类型不符的入参交给引擎在校验阶段报错（LP.VAL.002）——此处不静默兜底。
/// </summary>
public static class ArgBinder
{
    public static JsonElement Bind(CommandDescriptor? descriptor, CliArgs args, ISet<string> reserved)
    {
        var specs = descriptor?.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ParamSpec>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in args.Named)
        {
            if (reserved.Contains(name)) continue;
            if (values.Count == 0)
            {
                result[name] = true;   // bare flag => boolean true
                continue;
            }

            specs.TryGetValue(name, out var spec);
            result[name] = values.Count == 1
                ? Coerce(values[0], spec)
                : values.Select(v => Coerce(v, spec)).ToArray();
        }

        return JsonSerializer.SerializeToElement(result, ResultText.PlainOptions);
    }

    private static object? Coerce(string raw, ParamSpec? spec)
    {
        if (spec?.Schema is not null) return ParseJson(raw, spec.Name);

        var typeName = spec?.TypeName;
        if (typeName is not null && typeName.Contains('<')) return CoerceCollection(raw, typeName, spec!.Name);
        if (typeName is null) return Guess(raw);

        return typeName switch
        {
            "Int32" or "Int64" => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? l
                : raw,
            "Boolean" => ParseBool(raw, spec!.Name),
            "Double" or "Single" or "Decimal" =>
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : raw,
            "String" => raw,
            "JsonElement" => ParseJson(raw, spec!.Name),
            _ => Guess(raw),
        };
    }

    /// <summary>集合参数：<c>--ids '[a,b]'</c> 走 JSON；<c>--ids a,b</c> 走逗号分隔（元素按元素类型转换）。</summary>
    private static object CoerceCollection(string raw, string typeName, string paramName)
    {
        if (raw.TrimStart().StartsWith('[')) return ParseJson(raw, paramName);

        var elementType = ElementTypeName(typeName);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Coerce(v, new ParamSpec(paramName, elementType, string.Empty, false)))
            .ToArray();
    }

    private static string ElementTypeName(string typeName)
    {
        var open = typeName.IndexOf('<');
        return open >= 0 && typeName.EndsWith('>') ? typeName[(open + 1)..^1].Trim() : "String";
    }

    private static object? Guess(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed is "true" or "false") return bool.Parse(trimmed);
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith('"')) return ParseJson(raw, "value");
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }

    private static bool ParseBool(string raw, string paramName) => raw.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new ArgumentException($"parameter '{paramName}' expects a boolean, got '{raw}'"),
    };

    private static JsonElement ParseJson(string raw, string paramName)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"parameter '{paramName}' expects JSON but failed to parse: {ex.Message}");
        }
    }
}

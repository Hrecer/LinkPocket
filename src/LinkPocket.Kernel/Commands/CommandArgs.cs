using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Kernel.Commands;

/// <summary>
/// 命令入参读取器（方案 3.2/4.2）：全部模块 Handler 共用的 JSON 参数提取原语。
/// 校验类错误（LP.VAL.001/002）在此抛出——参数在进写闸前全量校验完毕（零副作用承诺）。
/// 参数命名 = snake_case；读取大小写不敏感（与 <see cref="LinkPocket.Contracts.EngineJson"/> 序列化口径一致）。
/// </summary>
public static class CommandArgs
{
    // —— 字符串 ——

    /// <summary>必填字符串参数；缺失/非字符串/空串即抛 REQUIRED_PARAM（Details 含参数名）。</summary>
    public static string RequireString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value))
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        throw new EngineException(RequiredError(name));
    }

    /// <summary>可选字符串参数；缺失/JSON null 返回 null（调用方按"不改该项"语义处理）；
    /// 显式传入非字符串值 → LP.VAL.002（类型错不得静默吃默认值）。</summary>
    public static string? OptionalString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => value.GetString(),
                _ => throw TypeError(name, "string", value.ValueKind),
            }
            : null;

    // —— 布尔 / 整数 ——

    public static bool OptionalBool(JsonElement args, string name, bool defaultValue = false)
        => OptionalBoolOrNull(args, name) ?? defaultValue;

    /// <summary>可选布尔参数：缺失/JSON null 返回 null；显式非布尔值 → LP.VAL.002。</summary>
    public static bool? OptionalBoolOrNull(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw TypeError(name, "boolean", value.ValueKind),
        };
    }

    public static int OptionalInt(JsonElement args, string name, int defaultValue = 0)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            _ => throw TypeError(name, "integer", value.ValueKind),
        };
    }

    public static int RequireInt(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
            return number;

        throw new EngineException(RequiredError(name));
    }

    /// <summary>必填布尔参数；缺失或非布尔即抛 REQUIRED_PARAM。</summary>
    public static bool RequireBool(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        throw new EngineException(RequiredError(name));
    }

    // —— 集合 / 原样 ——

    /// <summary>字符串数组参数；缺失/JSON null 返回空数组（批量命令的缺省口径）；
    /// 显式传入非数组值 → LP.VAL.002（类型错不得静默变成"空批量"）。</summary>
    public static IReadOnlyList<string> StringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return [];
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw TypeError(name, "string[]", value.ValueKind);

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                items.Add(text);
        }

        return items;
    }

    /// <summary>取原始 JSON 片段（复杂参数，如 links.query 的 filter/sort）；缺失返回 null。</summary>
    public static JsonElement? Raw(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
           && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    private static EngineError RequiredError(string name)
        => EngineErrors.Of(
            EngineErrors.RequiredParam,
            $"缺少必填参数「{name}」",
            JsonSerializer.SerializeToElement(new { @param = name }));

    /// <summary>类型不符（LP.VAL.002）：显式传入的值与参数声明类型不一致——必须报错，不得静默取默认值。</summary>
    private static EngineException TypeError(string name, string expected, JsonValueKind actual)
        => new(EngineErrors.Of(
            EngineErrors.TypeMismatch,
            $"参数「{name}」类型不符：期望 {expected}，实际 {actual}",
            JsonSerializer.SerializeToElement(new { @param = name, expected, actual = actual.ToString() })));
}

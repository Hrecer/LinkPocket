using System.Text.Encodings.Web;
using System.Text.Json;

namespace LinkPocket.Cli;

/// <summary>
/// 控制台渲染：结果统一按 **snake_case JSON** 落形（与引擎 wire 同一口径，机器面稳定），
/// 列表型载荷另给一张通用表格（人来读的命令默认走表格，<c>--json</c> 强制 JSON）。
/// </summary>
public static class ResultText
{
    /// <summary>结果序列化口径：与引擎 wire 同一套（snake_case + 中文/URL 不转义 + 缩进）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>入参绑定用（不加缩进、不加命名策略：字典键原样透出）。</summary>
    public static readonly JsonSerializerOptions PlainOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>任意引擎返回值 → snake_case JSON 元素（运行时类型序列化，DTO / record / 匿名对象皆可）。</summary>
    public static JsonElement ToElement(object? value)
        => JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), Options);

    public static string ToText(JsonElement element) => JsonSerializer.Serialize(element, Options);

    /// <summary>缺省最多渲染多少行（<c>--all</c> 关闭上限）。</summary>
    private const int DefaultRowLimit = 100;

    private const int MaxCellWidth = 48;

    /// <summary>
    /// 通用表格渲染：对象含数组字段（links / items / tree_links / sub_folders …）时按"标量行 + 各数组成表"分区；
    /// 顶层就是数组时直接成表。返回 false = 形状不适合表格，调用方应回退到 JSON。
    /// </summary>
    public static bool TryRenderTable(JsonElement data, TextWriter writer, bool all)
    {
        var limit = all ? int.MaxValue : DefaultRowLimit;

        if (data.ValueKind == JsonValueKind.Array)
            return RenderRows(data, writer, limit, null);

        if (data.ValueKind != JsonValueKind.Object) return false;

        var arrays = data.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        if (arrays.Count == 0) return false;

        var scalars = data.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Array).ToList();
        foreach (var scalar in scalars)
            writer.WriteLine($"{scalar.Name}: {Scalar(scalar.Value)}");

        foreach (var array in arrays)
        {
            writer.WriteLine();
            RenderRows(array.Value, writer, limit, array.Name);
        }
        return true;
    }

    private static bool RenderRows(JsonElement array, TextWriter writer, int limit, string? label)
    {
        var rows = array.EnumerateArray().ToList();
        var shown = Math.Min(rows.Count, limit);
        writer.WriteLine(label is null ? $"rows: {rows.Count}" : $"{label}: {rows.Count}");

        var objects = rows.Where(r => r.ValueKind == JsonValueKind.Object).ToList();
        if (objects.Count == 0)
        {
            for (var i = 0; i < shown; i++) writer.WriteLine($"  {Scalar(rows[i])}");
            if (shown < rows.Count) writer.WriteLine($"  ... {rows.Count - shown} more rows (use --all)");
            return true;
        }

        var columns = objects[0].EnumerateObject().Select(p => p.Name).ToList();
        if (columns.Count == 0) return false;

        var widths = columns.Select(c => c.Length).ToArray();
        var cells = new List<string[]>();
        for (var i = 0; i < shown; i++)
        {
            var row = new string[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                var text = Truncate(Cell(rows[i], columns[c]), MaxCellWidth);
                row[c] = text;
                if (text.Length > widths[c]) widths[c] = text.Length;
            }
            cells.Add(row);
        }

        writer.WriteLine("  " + string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))));
        writer.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
            writer.WriteLine("  " + string.Join("  ", row.Select((text, i) => text.PadRight(widths[i]))));

        if (shown < rows.Count) writer.WriteLine($"  ... {rows.Count - shown} more rows (use --all to print every row)");
        return true;
    }

    private static string Cell(JsonElement row, string column)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(column, out var value) ? Scalar(value) : "";

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };

    private static string Truncate(string text, int max)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "...";
    }
}

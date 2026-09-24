using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinkPocket.I18n;

/// <summary>
/// 文案数据的**唯一加载器**：两份 JSON（<c>Strings/zh-CN.json</c> / <c>Strings/en.json</c>，**嵌入资源**）
/// → 取词表。口径见 内部资产/文档/I18N.md §4.2：
/// <list type="bullet">
/// <item>一行一键、两份文件**键序逐行一致**（加载即校验：不一致就抛，不挑一份用）；</item>
/// <item>允许 <c>//</c> 注释（why 注释跟着它解释的键走）→ 解析必须带 <c>CommentHandling = Skip</c>；</item>
/// <item>空文本 / 键重复 / 资源缺失 → <b>抛</b>（错就报错，不静默兜底）。</item>
/// </list>
/// 文案是数据、不住在代码里；改文案只改 JSON（见 §10 扩展指南）。
/// </summary>
public static class StringTables
{
    /// <summary>嵌入资源逻辑名后缀：<c>&lt;RootNamespace&gt;.Strings.&lt;语言码&gt;.json</c>（按后缀找，禁硬编码全名）。</summary>
    private const string ResourceSuffix = ".Strings.";

    private static readonly Lazy<IReadOnlyList<KeyValuePair<string, string>>> ZhCn = new(() => LoadFile(AppLocale.ZhCn));
    private static readonly Lazy<IReadOnlyList<KeyValuePair<string, string>>> En = new(() => LoadFile(AppLocale.En));

    /// <summary>键序（= 文案文件里的行序；两份文件已在加载时校验一致）。</summary>
    public static IReadOnlyList<string> Keys { get; } = BuildKeys();

    /// <summary>取词表（键 → 文本）。同语言重复取用同一份实例。</summary>
    public static IReadOnlyDictionary<string, string> For(AppLocale locale)
    {
        var rows = locale == AppLocale.En ? En.Value : ZhCn.Value;
        var map = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var (key, text) in rows) map[key] = text;
        return map;
    }

    /// <summary>加载时顺手校验"两份文件键序一致"——错就报错，不让"半套文案"进运行期。</summary>
    private static IReadOnlyList<string> BuildKeys()
    {
        var zh = ZhCn.Value;
        var en = En.Value;
        if (zh.Count != en.Count)
            throw new InvalidOperationException(
                $"copy files disagree on row count: zh-CN has {zh.Count}, en has {en.Count} (see I18N.md section 4.2)");
        for (var i = 0; i < zh.Count; i++)
        {
            if (zh[i].Key == en[i].Key) continue;
            throw new InvalidOperationException(
                $"copy files disagree on key order at row {i + 1}: zh-CN has \"{zh[i].Key}\", en has \"{en[i].Key}\" (see I18N.md section 4.2)");
        }
        return zh.Select(pair => pair.Key).ToArray();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> LoadFile(AppLocale locale)
    {
        var code = locale.CodeOf();
        var expected = ResourceSuffix + code + ".json";
        var assembly = typeof(StringTables).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(expected, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"copy resource not found: \"{expected}\" (actual: {string.Join(", ", assembly.GetManifestResourceNames())})"
                + " - is Strings/*.json declared as EmbeddedResource in the csproj?");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,   // 文案文件里的 `//` 注释（见 WARNINGS 140）
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"copy file \"{code}.json\" is not valid JSON: {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
            throw new InvalidOperationException($"copy file \"{code}.json\" must be a JSON object (key -> text)");

        var rows = new List<KeyValuePair<string, string>>(obj.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            if (value is not JsonValue node || !node.TryGetValue<string>(out var copy))
                throw new InvalidOperationException($"copy entry \"{key}\" in {code}.json must be a string");
            if (string.IsNullOrWhiteSpace(copy))
                throw new InvalidOperationException($"empty copy text: {key} ({code}.json)");
            if (!seen.Add(key))
                throw new InvalidOperationException($"duplicate copy key: {key} ({code}.json)");
            rows.Add(new KeyValuePair<string, string>(key, copy));
        }
        return rows;
    }
}

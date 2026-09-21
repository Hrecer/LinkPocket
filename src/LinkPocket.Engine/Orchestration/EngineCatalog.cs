using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 引擎目录（IEngineCatalog / 扩展机制）：命令元数据的机械导出。
/// Manifest = registry 全量 + 批三命令（batch.* 由 wire 直路由，不进 registry）；
/// Export 三格式 = AI FunctionCalling 工具清单 / 轻量 OpenAPI / Markdown 文档，
/// 全部由 Descriptor 单一事实源生成，杜绝文档漂移。
/// </summary>
public sealed class EngineCatalog : IEngineCatalog
{
    private readonly CommandRegistry _registry;
    private readonly bool _includeBatch;

    public EngineCatalog(CommandRegistry registry, bool includeBatch = true)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _includeBatch = includeBatch;
    }

    public EngineManifest Manifest(string? category = null)
    {
        var commands = _registry.Describe(category);
        if (_includeBatch && category is null or "batch")
        {
            // 批三命令 Category 恒为 "batch"：外层已限定 category ∈ {null, "batch"}，
            // 直接全量并入（原 Where 恒真，纯死条件）
            commands = commands.Concat(BatchEngine.Descriptors)
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        }
        return new EngineManifest(DateTimeOffset.Now, commands);
    }

    public string Export(ManifestFormat format, string? category = null)
        => format switch
        {
            ManifestFormat.FunctionCalling => ExportFunctionCalling(category),
            ManifestFormat.OpenApiLite => ExportOpenApiLite(category),
            ManifestFormat.MarkdownDocs => ExportMarkdown(category),
            _ => throw new EngineException(EngineErrors.Of(EngineErrors.EnumOutOfRange,
                $"unknown export format {format}")),
        };

    /// <summary>AI FunctionCalling 工具清单（OpenAI tools 兼容形态；engine.describe 的直接升级面）。</summary>
    private string ExportFunctionCalling(string? category)
    {
        var tools = Manifest(category).Commands.Select(d =>
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var p in d.Parameters)
            {
                properties[p.Name] = MapParameter(p);
                if (p.Required) required.Add(p.Name);
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = d.Name,
                    ["description"] = $"{d.Description} (category {d.Category}; caps {d.Caps})",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                },
            };
        });
        return JsonSerializer.Serialize(tools, DocOptions);
    }

    /// <summary>轻量 OpenAPI：每命令一个 path，POST = 变更 / GET = 查询。</summary>
    private string ExportOpenApiLite(string? category)
    {
        var paths = new Dictionary<string, object>();
        foreach (var d in Manifest(category).Commands)
        {
            var properties = new Dictionary<string, object>();
            foreach (var p in d.Parameters)
                properties[p.Name] = MapParameter(p);

            paths[$"/{d.Name.Replace('.', '/')}"] = BuildOperation(d, properties);
        }

        var document = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0-lite",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "LinkPocket Engine",
                ["version"] = typeof(EngineCatalog).Assembly.GetName().Version?.ToString() ?? "2.0",
                ["description"] = "LinkPocket bookmark manager engine: one JSON-RPC surface for commands, queries and orchestration",
            },
            ["paths"] = paths,
        };
        return JsonSerializer.Serialize(document, DocOptions);
    }

    /// <summary>单个操作的 OpenAPI 形态：查询走 parameters（query 位置参数，GET 不接受 requestBody——
    /// Swagger UI/codegen 会直接忽略 GET 上的 requestBody，查询参数必须进 query 参数列表）；变更走 requestBody。</summary>
    private static Dictionary<string, object?> BuildOperation(CommandDescriptor d, Dictionary<string, object> properties)
    {
        var operation = new Dictionary<string, object?>
        {
            ["operationId"] = d.Name,
            ["summary"] = d.Description,
            ["tags"] = new[] { d.Category },
        };

        if (d.IsQuery)
        {
            operation["parameters"] = d.Parameters.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["in"] = "query",
                ["description"] = p.Description,
                ["schema"] = new Dictionary<string, object?> { ["type"] = MapJsonType(p.TypeName) },
            }).ToArray();
        }
        else
        {
            operation["requestBody"] = new Dictionary<string, object?>
            {
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = properties,
                        },
                    },
                },
            };
        }
        return operation;
    }

    private string ExportMarkdown(string? category)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# LinkPocket Engine Command Catalog");
        sb.AppendLine();
        sb.AppendLine($"> Generated at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; produced mechanically from command descriptors, do not edit by hand.");
        sb.AppendLine();
        foreach (var group in Manifest(category).Commands.GroupBy(d => d.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Command | Kind | Caps | Params | Description |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in group)
            {
                var kind = d.IsQuery ? "Query" : d.IsDestructive ? "Mutation (destructive)" : "Mutation";
                var @params = d.Parameters.Count == 0
                    ? "—"
                    : string.Join("、", d.Parameters.Select(p => $"{p.Name}({p.TypeName}{(p.Required ? "" : ", optional")})"));
                sb.AppendLine($"| `{d.Name}` | {kind} | {d.Caps} | {@params} | {d.Description} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// 单个参数在 AI 工具清单/OpenAPI 中的 JSON Schema 形态：标量 = { type }；
    /// 集合（IReadOnlyList&lt;T&gt; / List&lt;T&gt;）= { type: "array", items: { type: 元素类型 } }（此前集合
    /// 被一律归为 "object"，AI 调用方无法得知它是数组）。
    /// </summary>
    private static Dictionary<string, object> MapParameter(ParamSpec p)
    {
        if (TryItemType(p.TypeName, out var itemTypeName))
        {
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?> { ["type"] = itemTypeName },
                ["description"] = p.Description,
            };
        }
        return new Dictionary<string, object>
        {
            ["type"] = MapJsonType(p.TypeName),
            ["description"] = p.Description,
        };
    }

    /// <summary>集合类型名（ParamSpec 规范化后形如 IReadOnlyList&lt;string&gt;）→ 元素 JSON 类型；非集合返回 false。</summary>
    private static bool TryItemType(string typeName, out string itemType)
    {
        itemType = "object";
        var open = typeName.IndexOf('<');
        if (open < 0 || !typeName.EndsWith('>')) return false;
        var elementTypeName = typeName[(open + 1)..^1].Trim();
        if (elementTypeName.Contains('<')) return true;   // 嵌套泛型：保守整体视为对象
        itemType = MapJsonType(elementTypeName);
        return true;
    }

    private static string MapJsonType(string typeName) => typeName switch
    {
        "String" => "string",
        "Int32" or "Int64" => "integer",
        "Boolean" => "boolean",
        "Double" or "Single" or "Decimal" => "number",
        "JsonElement" => "object",
        _ => "object",
    };

    private static readonly JsonSerializerOptions DocOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

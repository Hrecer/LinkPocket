using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 引擎目录（方案 4.5 IEngineCatalog / 4.6 扩展机制）：命令元数据的机械导出。
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
            var batch = BatchEngine.Descriptors
                .Where(d => category == null || string.Equals(d.Category, category, StringComparison.Ordinal));
            commands = commands.Concat(batch)
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
                $"未知导出格式 {format}")),
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
                properties[p.Name] = new Dictionary<string, object?>
                {
                    ["type"] = MapJsonType(p.TypeName),
                    ["description"] = p.Description,
                };
                if (p.Required) required.Add(p.Name);
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = d.Name,
                    ["description"] = $"{d.Description}（类别 {d.Category}；能力 {d.Caps}）",
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
                properties[p.Name] = new Dictionary<string, object?> { ["type"] = MapJsonType(p.TypeName), ["description"] = p.Description };

            paths[$"/{d.Name.Replace('.', '/')}"] = new Dictionary<string, object?>
            {
                [d.IsQuery ? "get" : "post"] = new Dictionary<string, object?>
                {
                    ["operationId"] = d.Name,
                    ["summary"] = d.Description,
                    ["tags"] = new[] { d.Category },
                    ["requestBody"] = new Dictionary<string, object?>
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
                    },
                },
            };
        }

        var document = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0-lite",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "LinkPocket Engine",
                ["version"] = typeof(EngineCatalog).Assembly.GetName().Version?.ToString() ?? "2.0",
                ["description"] = "LinkPocket 书签管理器引擎：命令/查询/编排统一 JSON-RPC 面",
            },
            ["paths"] = paths,
        };
        return JsonSerializer.Serialize(document, DocOptions);
    }

    private string ExportMarkdown(string? category)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# LinkPocket 引擎命令目录");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}；由命令描述符机械生成，勿手改。");
        sb.AppendLine();
        foreach (var group in Manifest(category).Commands.GroupBy(d => d.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| 命令 | 类型 | 能力 | 参数 | 说明 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in group)
            {
                var kind = d.IsQuery ? "查询" : d.IsDestructive ? "变更（破坏性）" : "变更";
                var @params = d.Parameters.Count == 0
                    ? "—"
                    : string.Join("、", d.Parameters.Select(p => $"{p.Name}({p.TypeName}{(p.Required ? "" : "，可选")})"));
                sb.AppendLine($"| `{d.Name}` | {kind} | {d.Caps} | {@params} | {d.Description} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string MapJsonType(string typeName) => typeName switch
    {
        "String" => "string",
        "Int32" or "Int64" => "integer",
        "Boolean" => "boolean",
        _ => "object",
    };

    private static readonly JsonSerializerOptions DocOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

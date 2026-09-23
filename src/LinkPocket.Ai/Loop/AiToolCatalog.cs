using System.Text.Json.Nodes;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 工具目录（模型可见的工具面）：从引擎目录（<c>engine.describe</c>）裁剪 + 精选 schema。
/// 三层暴露（功能书 §3.3）：**Tier 1 默认** / **Tier 2 需在设置里显式开启"高级工具"** / **永不暴露**。
/// schema 质量：被点名"塌陷"的复杂参数（查询过滤 / 批脚本 / 暂存算子）在此给出**精选 JSON Schema**；
/// 其余按 <see cref="ParamSpec"/> 的类型名机械映射（P3 的 E10 会把元数据补进描述符，届时此处只剩覆盖表）。
/// </summary>
public sealed class AiToolCatalog
{
    /// <summary>永不暴露（清库重置：对用户无正收益、误触发代价不可接受；界面照常提供人工操作）。</summary>
    private static readonly HashSet<string> NeverExposed = new(StringComparer.Ordinal)
    {
        "maintenance.reinit",
    };

    /// <summary>Tier 2（低频 / 影响面大：显式开启后仍受审批链约束）。</summary>
    private static readonly HashSet<string> Advanced = new(StringComparer.Ordinal)
    {
        "folders.copy", "folders.delete", "folders.sort", "links.copy_batch", "links.export",
        "links.visit_record", "links.visit_batch", "trash.purge", "trash.purge_batch",
        "bookmarks.inspect", "bookmarks.import", "bookmarks.export",
        "backup.inspect", "backup.export", "backup.import",
        "audit.prune", "logs.level", "favicon.prefetch",
        "undo.undo", "undo.redo", "undo.clear", "macro.save", "macro.delete",
    };

    /// <summary><c>{ref.path}</c> 能取到的返回形状（写进工具描述；只列 AI 常用的批步骤命令）。</summary>
    private static readonly Dictionary<string, string> ReturnHints = new(StringComparer.Ordinal)
    {
        ["folders.create"] = "{id, name}",
        ["folders.find"] = "an array of {id, name}",
        ["folders.contents"] = "{folders: [], links: [], breadcrumb: []}",
        ["links.create"] = "{id, url, title}",
        ["links.query"] = "{items: [], total, page, page_count}",
        ["links.list"] = "{items: [], total, page, page_count}",
        ["links.find_by_url"] = "an array of {id, url, title}",
        ["batch.run"] = "{batch_id, ok, steps: [{ref, command, ok, data}]}",
        ["batch.dry_run"] = "{batch_id, ok, steps: [{ref, command, ok, data}]}",
        ["dedup.scan"] = "{groups: [{url, links: []}]}",
        ["staging.transform"] = "{items_before, items_after, preview_json}",
    };

    /// <summary>精选 schema（覆盖机械映射；键 = 命令名）。</summary>
    private static readonly Dictionary<string, string> CuratedSchemas = new(StringComparer.Ordinal)
    {
        ["links.query"] = """
        {"type":"object","properties":{
          "filter":{"type":"array","description":"Filter conditions; at most one condition per field. Fields: folder_id(eq,isnull), title(contains), url(contains,starts), description(contains), created_at|updated_at(gt,gte,lt,lte,between), last_visited_at(gt,gte,lt,lte,between,isnull), visit_count(gt,gte,lt,lte,between), is_important(eq).","items":{"type":"object","properties":{"field":{"type":"string"},"op":{"type":"string","enum":["eq","ne","contains","starts","gt","gte","lt","lte","between","in","isnull"]},"value":{}},"required":["field","op"]}},
          "sort":{"type":"array","description":"Sort keys (title, url, created_at, updated_at, last_visited_at, visit_count); default = title asc then id.","items":{"type":"object","properties":{"field":{"type":"string"},"dir":{"type":"string","enum":["asc","desc"]}},"required":["field","dir"]}},
          "page":{"type":"object","description":"Page window; size = 0 means all rows (up to 10000).","properties":{"index":{"type":"integer"},"size":{"type":"integer"}}},
          "fields":{"type":"array","description":"Projection; note the folder field is named list_id here.","items":{"type":"string","enum":["id","url","title","description","favicon_url","list_id","last_visited_at","visit_count","is_important","created_at","updated_at"]}}
        }}
        """,
        ["staging.transform"] = """
        {"type":"object","properties":{
          "staging_id":{"type":"string"},
          "ops":{"type":"array","description":"Pure-function pipeline, applied in order. reencode must come first if used.","items":{"type":"object","properties":{
            "op":{"type":"string","enum":["reencode","filter_links","rename_folder","map_field","strip_prefix","dedupe"]},
            "args":{"type":"object","description":"reencode{from}; filter_links{field,op:eq|contains|starts,value}; rename_folder{from,to}; map_field{from,to}; strip_prefix{field,prefix}; dedupe{by}"}},"required":["op"]}},
          "dry_run":{"type":"boolean"}
        },"required":["staging_id","ops"]}
        """,
    };

    private readonly Dictionary<string, CommandDescriptor> _byName;

    public AiToolCatalog(EngineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _byName = manifest.Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>本次会话是否暴露该命令（永不过露 / Tier 分层）。</summary>
    public bool IsExposed(string command, bool advancedTools)
        => _byName.ContainsKey(command) && !NeverExposed.Contains(command)
           && (advancedTools || !Advanced.Contains(command));

    public CommandDescriptor? Descriptor(string command) => _byName.GetValueOrDefault(command);

    /// <summary>模型可见的工具清单（Tier 1 + 可选 Tier 2）。</summary>
    public IReadOnlyList<AiToolSpec> Build(bool advancedTools)
        => _byName.Values
            .Where(c => IsExposed(c.Name, advancedTools))
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(ToSpec)
            .ToArray();

    private static AiToolSpec ToSpec(CommandDescriptor descriptor)
    {
        var hint = ReturnHints.TryGetValue(descriptor.Name, out var returns) ? $" Returns {{ref}} fields: {returns}." : "";
        var description = $"{descriptor.Description} (category {descriptor.Category}; caps {descriptor.Caps}).{hint}";
        var schema = CuratedSchemas.TryGetValue(descriptor.Name, out var curated)
            ? curated.Trim()
            : BuildSchema(descriptor.Parameters);
        return new AiToolSpec(descriptor.Name, description, schema);
    }

    /// <summary>按参数类型名机械映射 JSON Schema（复杂形状靠精选表覆盖）。</summary>
    private static string BuildSchema(IReadOnlyList<ParamSpec> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            var fieldSchema = TypeSchema(parameter.TypeName);
            fieldSchema["description"] = parameter.Description;
            properties[parameter.Name] = fieldSchema;
            if (parameter.Required) required.Add(parameter.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema.ToJsonString();
    }

    private static JsonObject TypeSchema(string typeName) => typeName switch
    {
        "String" => new JsonObject { ["type"] = "string" },
        "Int32" or "Int64" => new JsonObject { ["type"] = "integer" },
        "Boolean" => new JsonObject { ["type"] = "boolean" },
        "Double" or "Decimal" => new JsonObject { ["type"] = "number" },
        "IReadOnlyList<String>" or "List<String>" or "String[]" =>
            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        "IReadOnlyList<Int32>" or "List<Int32>" or "Int32[]" =>
            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
        "IReadOnlyList<Boolean>" or "List<Boolean>" or "Boolean[]" =>
            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "boolean" } },
        _ => new JsonObject { ["type"] = "object" },
    };
}

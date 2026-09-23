using System.Text.Json.Nodes;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 工具目录（模型可见的工具面）：从引擎目录（<c>engine.describe</c>）裁剪 + 描述符元数据。
/// 三层暴露（功能书 §3.3）：**Tier 1 默认** / **Tier 2 需在设置里显式开启"高级工具"** / **永不暴露**。
/// schema 质量：复杂参数（查询过滤 / 批脚本 / 暂存算子）的精选 JSON Schema 已进描述符
/// （<c>ParamSpec.Schema</c> ← <c>ParamSchemas</c> 常量，与 catalog 三件同一事实源）；
/// 标量枚举（<c>ParamSpec.EnumValues</c>）同样透出——本类只做"裁剪 + 拼装"，不再自持第二份 schema。
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
        return new AiToolSpec(descriptor.Name, description, BuildSchema(descriptor.Parameters));
    }

    /// <summary>按参数元数据组装 JSON Schema：Schema 片段（<c>ParamSchemas</c>）优先，
    /// 标量带 EnumValues 时透出 enum，其余按类型名机械映射（与 catalog 的 MapParameter 同口径）。</summary>
    private static string BuildSchema(IReadOnlyList<ParamSpec> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            var fieldSchema = FieldSchema(parameter);
            if (!fieldSchema.ContainsKey("description")) fieldSchema["description"] = parameter.Description;
            properties[parameter.Name] = fieldSchema;
            if (parameter.Required) required.Add(parameter.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema.ToJsonString();
    }

    /// <summary>单参数 schema：描述符携带 Schema 片段（复杂参数的精选形状）则整体透出；
    /// 标量/集合按类型映射并叠加 EnumValues（集合时 enum 落在 items 上）。</summary>
    private static JsonObject FieldSchema(ParamSpec parameter)
    {
        if (parameter.Schema is not null)
            return (JsonObject)JsonNode.Parse(parameter.Schema)!;

        var schema = TypeSchema(parameter.TypeName);
        if (parameter.EnumValues is not null)
        {
            var values = new JsonArray();
            foreach (var value in parameter.EnumValues) values.Add(value);
            if (schema.TryGetPropertyValue("items", out var items) && items is JsonObject itemSchema)
                itemSchema["enum"] = values;
            else
                schema["enum"] = values;
        }
        return schema;
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

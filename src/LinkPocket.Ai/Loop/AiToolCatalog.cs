using System.Text.Json;
using System.Text.Json.Nodes;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 一条命令在本会话的暴露状态（"不可用"的三种原因必须能区分，见 <see cref="AiToolCatalog.ExposureOf"/>）。
/// </summary>
public enum AiToolExposure
{
    /// <summary>在本次暴露集内，可走权限链。</summary>
    Exposed = 0,
    /// <summary>目录里没有这个命令名（模型拼错/凭直觉编的名字）。</summary>
    UnknownCommand = 1,
    /// <summary>命令存在，但属 Tier 2：需用户在设置里显式开启"高级工具"。</summary>
    AdvancedToolsRequired = 2,
    /// <summary>永久不暴露给 AI（界面照常提供人工操作）。</summary>
    NeverExposed = 3,
}

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

    /// <summary>
    /// Tier 2（低频 / 影响面大：显式开启后仍受审批链约束）。
    /// <para><b>2026-09-26 按产品决定收敛</b>：门后只留**真正不可逆**的 5 条。原先 23 条里那些
    /// "可撤销的普通写"（复制文件夹/复制链接/排序/宏增删/撤销重做）、"只读或只写文件的"
    /// （导出、导入前预检、备份导出、日志级别、图标预取）与"轻度计数写"（访问记录）都已放回默认层——
    /// 它们的误触发代价小或可撤销，锁在门后只会让 AI 连日常整理都做不了（实测：用户让 AI 删空文件夹被拒）。
    /// 留下的 5 条各自**没有任何回退路径**：永久删除（两处）、按保留期清审计历史、从备份导入（`replace=true`
    /// 会先清空全库）、清空撤销栈（清完连"撤销"这个安全网都没了）。</para>
    /// </summary>
    private static readonly HashSet<string> Advanced = new(StringComparer.Ordinal)
    {
        "trash.purge", "trash.purge_batch", "backup.import", "audit.prune", "undo.clear",
    };

    /// <summary>
    /// **同一条命令里"安全档"与"危险档"并存**时的规则（当前只有 <c>folders.delete</c>）：
    /// 只有该参数取到安全值（或不传、走引擎默认）才算默认可用，其余档位归 Tier 2。
    /// <para><c>folders.delete</c>：默认 <c>cascade=trash_links</c> = 整棵子树**进回收站**（可还原、
    /// 且登记了撤销逆向）；<c>delete_all</c>（物理删除）与 <c>move_to_list</c>（链接已转移、空子树已删）
    /// 都是**不可逆**（处理器明确不发撤销载荷）→ 这两档要开"高级工具"。</para>
    /// <para>⚠️ 连带事实：<c>folders.delete</c> 的描述符**不带 Destructive**（删除确认按设计放在界面层），
    /// 所以在"自动应用"模式下它不会弹审批卡——这正是"安全档默认给、危险档留门后"这条规则存在的理由。</para>
    /// </summary>
    private static readonly Dictionary<string, (string Param, string SafeValue)> SafeVariants = new(StringComparer.Ordinal)
    {
        ["folders.delete"] = ("cascade", "trash_links"),
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

    /// <summary>本次会话是否暴露该命令（永不过露 / Tier 分层 / 参数档位；按引擎默认档判定）。</summary>
    public bool IsExposed(string command, bool advancedTools)
        => ExposureOf(command, advancedTools) == AiToolExposure.Exposed;

    /// <summary>
    /// 暴露分类（<see cref="IsExposed"/> 的细化版）：三种"不可用"原因必须能分开，
    /// 否则模型分不清"我拼错了名字"和"这条命令要用户去开高级开关"——
    /// 实测现场：模型把 `folders.trash`（根本不存在）与 `folders.delete`（存在但未开高级开关）
    /// 收到同一个 `step_not_exposed`，于是向用户报告"引擎没有开放删除文件夹的接口"（错误结论）。
    /// </summary>
    /// <param name="argsJson">调用入参（判定 <see cref="SafeVariants"/> 的档位用；缺省 = 引擎默认档 = 安全档）。</param>
    public AiToolExposure ExposureOf(string command, bool advancedTools, string? argsJson = null)
    {
        if (!_byName.ContainsKey(command)) return AiToolExposure.UnknownCommand;
        if (NeverExposed.Contains(command)) return AiToolExposure.NeverExposed;
        if (advancedTools) return AiToolExposure.Exposed;
        if (Advanced.Contains(command)) return AiToolExposure.AdvancedToolsRequired;
        if (SafeVariants.TryGetValue(command, out var rule) && !TakesSafeValue(argsJson, rule))
            return AiToolExposure.AdvancedToolsRequired;
        return AiToolExposure.Exposed;
    }

    /// <summary>该命令是否有"安全档"（<see cref="SafeVariants"/>）：被拦时给模型一句可执行的话，别让它只好放弃。</summary>
    public string? SafeVariantHint(string command, bool advancedTools)
        => !advancedTools && SafeVariants.TryGetValue(command, out var rule)
            ? $"'{command}' is available by default as long as {rule.Param} stays '{rule.SafeValue}' "
              + $"(that variant is reversible). Only the other {rule.Param} values need \"Advanced tools\"."
            : null;

    /// <summary>入参是否取到安全档：不传该参数（= 走引擎默认）或值等于安全值 → 安全；解析失败也放过（交给引擎报错，这里不越权判死）。</summary>
    private static bool TakesSafeValue(string? argsJson, (string Param, string SafeValue) rule)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return true;
        try
        {
            using var document = JsonDocument.Parse(argsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return true;
            if (!document.RootElement.TryGetProperty(rule.Param, out var value)) return true;
            if (value.ValueKind != JsonValueKind.String) return true;
            return string.Equals(value.GetString(), rule.SafeValue, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// 命令名写错时的候选：**同域命令按名列出**（域名对但动作写错时，一眼就能看到正确的那条），
    /// 域名也不对时退回全目录里编辑距离最近的几个。只用于**提示**，不作任何放行依据。
    /// 模型常凭直觉拼命令名（现场见过 <c>folders.trash</c> / <c>folders.remove</c> / <c>trash.delete_folder</c>），
    /// 只回一句"未暴露"它会继续试别的假名字；把真实存在的命令列给它，一轮就能改对。
    /// </summary>
    public IReadOnlyList<string> Suggestions(string command, int max = 12)
    {
        if (command.Length == 0 || max <= 0) return [];
        var separator = command.IndexOf('.');
        var domain = separator > 0 ? command[..separator] : command;

        var inDomain = _byName.Keys
            .Where(name => name.StartsWith(domain + ".", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (inDomain.Count > 0) return inDomain.Take(max).ToList();

        return _byName.Keys
            .OrderBy(name => Distance(command, name))
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>Levenshtein 距离（短名，O(n·m) 可忽略）。</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

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

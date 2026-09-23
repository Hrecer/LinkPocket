namespace LinkPocket.Contracts;

/// <summary>
/// 参数级 JSON Schema 片段（P3-6：目录导出与 AI 工具清单的**嵌套元数据单一事实源**）。
/// 描述符（ParamSpec.Schema）引用这里的片段 → <c>EngineCatalog.MapParameter</c>（catalog 三件）
/// 与 <c>AiToolCatalog.BuildSchema</c>（模型可见工具面）共用同一份——杜绝"目录塌陷、模型靠猜"，
/// 也杜绝两处各写一份 schema 走样。片段键优先于机械类型映射；缺 <c>description</c> 时由
/// ParamSpec.Description 补齐。全部机器面英文（i18n 决策 7）。
/// </summary>
public static class ParamSchemas
{
    /// <summary>links.query.filter：条件数组（字段/操作符白名单进 schema，模型不用背说明文字）。</summary>
    public const string LinkQueryFilter =
        """
        {"type":"array","description":"Filter conditions; at most one condition per field. Fields: id(eq,in), folder_id(eq,isnull), title(contains), url(contains,starts), description(contains), created_at|updated_at(gt,gte,lt,lte,between), last_visited_at(gt,gte,lt,lte,between,isnull), visit_count(gt,gte,lt,lte,between), is_important(eq).","items":{"type":"object","properties":{"field":{"type":"string"},"op":{"type":"string","enum":["eq","ne","contains","starts","gt","gte","lt","lte","between","in","isnull"]},"value":{}},"required":["field","op"]}}
        """;

    /// <summary>links.query.sort：排序键数组（字段 = 排序白名单，方向 = asc/desc）。</summary>
    public const string LinkQuerySort =
        """
        {"type":"array","description":"Sort keys (title, url, created_at, updated_at, last_visited_at, visit_count); default = title asc then id.","items":{"type":"object","properties":{"field":{"type":"string"},"dir":{"type":"string","enum":["asc","desc"]}},"required":["field","dir"]}}
        """;

    /// <summary>links.query.page：分页窗口（size = 0 全量，上限 10000）。</summary>
    public const string LinkQueryPage =
        """
        {"type":"object","description":"Page window; size = 0 means all rows (up to 10000).","properties":{"index":{"type":"integer"},"size":{"type":"integer"}}}
        """;

    /// <summary>links.query.fields：投影字段数组（枚举 = 投影白名单；文件夹字段在链接投影里名为 list_id）。</summary>
    public const string LinkQueryFields =
        """
        {"type":"array","description":"Projection; note the folder field is named list_id here.","items":{"type":"string","enum":["id","url","title","description","favicon_url","list_id","last_visited_at","visit_count","is_important","created_at","updated_at"]}}
        """;

    /// <summary>
    /// 批脚本（batch.run / batch.dry_run / macro.save 的 script 参数）：完整 JSON Schema + 可复制示例
    /// （功能书 §3.3.2：机器可读、含 {ref} 用法与 on_error 的正确拼写）。
    /// scope 枚举 = 功能书 §3.2① 的小写形态；on_error 枚举 = ENGINE-API L2 的 Abort | Continue | SkipAndLog
    /// （引擎经 JsonStringEnumConverter 读入：大小写不敏感、**下划线写法不被接受**，如 skip_and_log 会失败）。
    /// </summary>
    public const string BatchScript =
        """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Script name (shown in reports)"},
          "scope":{"type":"string","enum":["transactional","independent"],"description":"transactional (default) = one unit of work, any Abort rolls the whole batch back; independent = each step commits on its own"},
          "steps":{"type":"array","minItems":1,"description":"Steps run in order. Step args may reference earlier results: {ref}, {ref.path}, {ref.path[n]}, {ref.path[*].field}, {ref.path.length}.","items":{"type":"object","properties":{
            "ref":{"type":"string","description":"Step reference name used by {ref...} templates in later steps"},
            "command":{"type":"string","description":"Engine command name (e.g. links.move_batch)"},
            "args":{"type":"object","description":"Command arguments; template tokens are resolved before dispatch"},
            "on_error":{"type":"string","enum":["Abort","Continue","SkipAndLog"],"description":"Abort (default) stops the batch; Continue records the failure and runs on; SkipAndLog marks the failed step Skipped"}
          },"required":["command"]}}
        },"required":["steps"],"example":{"name":"move-all-github","scope":"transactional","steps":[
          {"ref":"q","command":"links.query","args":{"filter":[{"field":"url","op":"contains","value":"github"}],"page":{"index":1,"size":0}}},
          {"ref":"m","command":"links.move_batch","args":{"link_ids":"{q.items[*].id}"},"on_error":"Abort"}
        ]}}
        """;

    /// <summary>dedup.plan / dedup.apply 的 group_urls：URL 字符串数组（缺省 = 全部重复组）。</summary>
    public const string DedupGroupUrls =
        """
        {"type":"array","items":{"type":"string"}}
        """;

    /// <summary>dedup.plan / dedup.apply 的 explicit_keep：{url: link_id} 字典（keep_explicit 策略的保留者表）。</summary>
    public const string DedupExplicitKeep =
        """
        {"type":"object","additionalProperties":{"type":"string"}}
        """;

    /// <summary>staging.transform 的 ops：六算子纯函数管道（顺序应用；reencode 必须最先）。</summary>
    public const string StagingOps =
        """
        {"type":"array","description":"Pure-function pipeline, applied in order. reencode must come first if used.","items":{"type":"object","properties":{
          "op":{"type":"string","enum":["reencode","filter_links","rename_folder","map_field","strip_prefix","dedupe"]},
          "args":{"type":"object","description":"reencode{from}; filter_links{field,op:eq|contains|starts,value}; rename_folder{from,to}; map_field{from,to}; strip_prefix{field,prefix}; dedupe{by}"}
        },"required":["op"]}}
        """;
}

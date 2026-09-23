namespace LinkPocket.Contracts;

/// <summary>受影响实体引用（类型 + ID）。</summary>
public sealed record EntityRef(string Type, string Id);

/// <summary>
/// 字段级差异：一次变更在**某个实体的某个字段**上的 before → after（AI 逐条变更审计的权威来源）。
/// <c>Before</c>/<c>After</c> 为 <c>null</c> = **该时刻字段不适用**（创建前不存在 / 删除后已离开主表）；
/// JSON null（<see cref="System.Text.Json.JsonValueKind.Null"/>）= 字段值本身为空。值一律经 <see cref="FieldValue"/> 产出。
/// </summary>
public sealed record FieldChange(string Type, string Id, string Field,
    System.Text.Json.JsonElement? Before, System.Text.Json.JsonElement? After);

/// <summary>字段值工厂：把常见值类型转成 diff 用的 JSON 表示（null 字符串 → JSON null，不是"不适用"）。</summary>
public static class FieldValue
{
    private static readonly System.Text.Json.JsonElement NullValue =
        System.Text.Json.JsonSerializer.SerializeToElement<string?>(null);

    /// <summary>字符串值（null = 空值 → JSON null）。</summary>
    public static System.Text.Json.JsonElement Str(string? value)
        => value is null ? NullValue : System.Text.Json.JsonSerializer.SerializeToElement(value);

    /// <summary>布尔值。</summary>
    public static System.Text.Json.JsonElement Bool(bool value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);
}

/// <summary>
/// 变更集：本调用的受影响实体、发布的事件、人类可读摘要与字段级差异。
/// HumanSummary 示例：「将移动 12 个链接到「工作」（2 个同名自动编号）」。
/// Diff = 字段级 before/after 列表（创建 = 全字段新值；删除 = After 为"不适用"的原值快照）。
/// </summary>
public sealed record ChangeSet(
    IReadOnlyList<EntityRef> Touched,
    IReadOnlyList<string> Events,
    string? HumanSummary,
    /// <summary>
    /// 非致命问题的结构化上报（缺省空）：命令整体成功，但**部分可选项没做成**时如实记在这里，
    /// 例如 <c>links.create</c> 的元数据抓取失败。绝不静默吞掉（观测面纪律：失败要暴露）。
    /// </summary>
    IReadOnlyList<string>? Warnings = null,
    /// <summary>字段级差异（缺省空 = 未上报字段差异；消费方按"仅实体级"如实标注，见 AI-ASSISTANT §7.8）。</summary>
    IReadOnlyList<FieldChange>? Diff = null)
{
    public static readonly ChangeSet Empty = new([], [], null);

    /// <summary>单实体单事件（补 warnings / diff 参数，避免「一遇 Warnings/Diff 就手写 new」）。</summary>
    public static ChangeSet Of(EntityRef touched, string evt, string? summary = null,
        IReadOnlyList<string>? warnings = null, IReadOnlyList<FieldChange>? diff = null)
        => new([touched], [evt], summary, warnings, diff);

    /// <summary>多实体 / 多事件（批量命令的常见形态，替代冗长手写构造函数）。</summary>
    public static ChangeSet OfMany(IReadOnlyList<EntityRef> touched, IReadOnlyList<string> events,
        string? summary = null, IReadOnlyList<string>? warnings = null, IReadOnlyList<FieldChange>? diff = null)
        => new(touched, events, summary, warnings, diff);
}

/// <summary>
/// 命令执行成功结果（处理器返回；引擎包装为强类型 <see cref="CommandResult{T}"/> 给消费者）。
/// Undo = 处理器回填的**逆向步骤**（撤销这个动作要执行什么）——可空：
/// 为 null 时引擎退回"描述符声明的 UndoInverse + 原参数"；两者都没有则本命令不入撤销栈。
/// 处理器必须在同一个事务内读到旧值后回填（如移动前的父目录）——引擎无法从原参数反推旧值。
/// </summary>
public sealed record CommandResult(
    object? Data,
    ChangeSet? Changes,
    string? AuditRef,
    IReadOnlyList<UndoInverseStep>? Undo = null)
{
    public static CommandResult Ok(object? data, ChangeSet? changes = null,
        IReadOnlyList<UndoInverseStep>? undo = null)
        => new(data, changes, null, undo);
}

/// <summary>命令执行成功结果（强类型视图）。</summary>
public sealed record CommandResult<T>(
    bool Ok,
    T? Data,
    ChangeSet? Changes,
    string? AuditRef);

namespace LinkPocket.Contracts;

/// <summary>受影响实体引用（类型 + ID）。</summary>
public sealed record EntityRef(string Type, string Id);

/// <summary>
/// 变更集：本调用的受影响实体、发布的事件与人类可读摘要。
/// HumanSummary 示例：「将移动 12 个链接到「工作」（2 个同名自动编号）」。
/// </summary>
public sealed record ChangeSet(
    IReadOnlyList<EntityRef> Touched,
    IReadOnlyList<string> Events,
    string? HumanSummary,
    /// <summary>
    /// 非致命问题的结构化上报（缺省空）：命令整体成功，但**部分可选项没做成**时如实记在这里，
    /// 例如 <c>links.create</c> 的元数据抓取失败。绝不静默吞掉（观测面纪律：失败要暴露）。
    /// </summary>
    IReadOnlyList<string>? Warnings = null)
{
    public static readonly ChangeSet Empty = new([], [], null);

    /// <summary>单实体单事件（补 warnings 参数，避免「一遇 Warnings 就手写 new」）。</summary>
    public static ChangeSet Of(EntityRef touched, string evt, string? summary = null,
        IReadOnlyList<string>? warnings = null)
        => new([touched], [evt], summary, warnings);

    /// <summary>多实体 / 多事件（批量命令的常见形态，替代冗长手写构造函数）。</summary>
    public static ChangeSet OfMany(IReadOnlyList<EntityRef> touched, IReadOnlyList<string> events,
        string? summary = null, IReadOnlyList<string>? warnings = null)
        => new(touched, events, summary, warnings);
}

/// <summary>命令执行成功结果（处理器返回；引擎包装为强类型 <see cref="CommandResult{T}"/> 给消费者）。</summary>
public sealed record CommandResult(
    object? Data,
    ChangeSet? Changes,
    string? AuditRef)
{
    public static CommandResult Ok(object? data, ChangeSet? changes = null)
        => new(data, changes, null);
}

/// <summary>命令执行成功结果（强类型视图）。</summary>
public sealed record CommandResult<T>(
    bool Ok,
    T? Data,
    ChangeSet? Changes,
    string? AuditRef);

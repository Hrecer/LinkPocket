namespace LinkPocket.Contracts;

/// <summary>受影响实体引用（类型 + ID）。</summary>
public sealed record EntityRef(string Type, string Id);

/// <summary>
/// 变更集（方案 3.1）：本调用的受影响实体、发布的事件与人类可读摘要。
/// HumanSummary 示例：「将移动 12 个链接到「工作」（2 个同名自动编号）」。
/// </summary>
public sealed record ChangeSet(
    IReadOnlyList<EntityRef> Touched,
    IReadOnlyList<string> Events,
    string? HumanSummary)
{
    public static readonly ChangeSet Empty = new([], [], null);

    public static ChangeSet Of(EntityRef touched, string evt, string? summary = null)
        => new([touched], [evt], summary);
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

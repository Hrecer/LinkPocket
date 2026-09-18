namespace LinkPocket.Contracts;

/// <summary>调用方身份（CallOptions.Caller）。</summary>
public enum CallerKind
{
    Ui,
    Batch,
    Macro,
    Agent,
    Test,
}

/// <summary>调用方引用：{ Kind, SessionId }，贯穿审计/日志/错误/事件。</summary>
public sealed record CallerRef(CallerKind Kind, string? SessionId = null)
{
    /// <summary>缺省调用方（未显式指定 Caller 时）：桌面界面会话——未登记会话零约束。</summary>
    public static readonly CallerRef Ui = new(CallerKind.Ui, null);

    public static readonly CallerRef Test = new(CallerKind.Test, "test");
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{SessionId ?? "-"}";
}

/// <summary>
/// 每次调用的选项：
/// DryRun = 预演（返回 ChangeSet 与影响面，零副作用）；
/// ConfirmToken = 破坏性命令的二次确认令牌（CONFIRM_REQUIRED 的 Details 中下发，60s 有效）；
/// IdempotencyKey = 24h 窗口内重复调用返回首次结果；
/// CorrelationId = 缺省自动生成，贯穿审计/日志/错误/事件。
/// </summary>
public sealed record CallOptions(
    bool DryRun = false,
    string? ConfirmToken = null,
    string? IdempotencyKey = null,
    string? CorrelationId = null,
    CallerRef? Caller = null);

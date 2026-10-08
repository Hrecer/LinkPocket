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

    /// <summary>
    /// **外部进程**身份（CLI / 外部 Agent 网关）：agent 类但**不带会话**（会话由应用内助手
    /// <c>Begin</c> 后携带，见 <c>ISessionManager</c>）。两个外部入口都用它，于是
    /// "这条审计行是不是别的进程写的"有了逐字可判的判据——跨进程变更流（<see cref="IChangeFeed"/>）按它过滤。
    /// </summary>
    public static readonly CallerRef ExternalAgent = new(CallerKind.Agent, null);

    public static readonly CallerRef Test = new(CallerKind.Test, "test");

    /// <summary>是否**外部进程**身份（CLI / 网关）：界面自己的写是 <see cref="Ui"/>、
    /// 应用内助手是带会话的 agent，两者都不是外部进程。</summary>
    public bool IsExternalProcess => Kind == CallerKind.Agent && SessionId is null;

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{SessionId ?? "-"}";
}

/// <summary>
/// 每次调用的选项：
/// DryRun = 预演（返回 ChangeSet 与影响面，零副作用）；
/// ConfirmToken = 破坏性命令的二次确认令牌（CONFIRM_REQUIRED 的 Details 中下发，60s 有效）；
/// IdempotencyKey = 24h 窗口内重复调用返回首次结果；
/// CorrelationId = 缺省自动生成，贯穿审计/日志/错误/事件；
/// UndoGroupId = **撤销分组**：同一次用户动作拆成的多次顶层调用（如"一次粘贴多选"）携带同一组 ID，
/// 撤销栈把它们合并为**一条**记录——一次 Ctrl+Z 撤销整个动作（Windows 资源管理器口径）；
/// BatchId = **批运行键**（audit_log batch_id 列）：独立批的步骤走完整顶层管道，
/// 经此选项携带批 ID，使 <c>audit.query {batch_id}</c> 能取齐该批父条目与每一步
/// （事务批的嵌套步骤由上下文透传，不经本选项）。
/// </summary>
public sealed record CallOptions(
    bool DryRun = false,
    string? ConfirmToken = null,
    string? IdempotencyKey = null,
    string? CorrelationId = null,
    CallerRef? Caller = null,
    string? UndoGroupId = null,
    string? BatchId = null)
{
    /// <summary>生效调用方的**唯一出处**（未显式指定 = <see cref="CallerRef.Ui"/>）：
    /// 引擎管道与客户端调用记录都取它——否则"引擎按 ui 跑、日志记成 null"这种两套口径迟早漂移。</summary>
    public static CallerRef CallerOf(CallOptions? options) => options?.Caller ?? CallerRef.Ui;
}

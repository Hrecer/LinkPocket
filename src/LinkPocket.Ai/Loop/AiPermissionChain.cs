using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>工具调用的权限判定结果。</summary>
public enum AiToolDecision
{
    /// <summary>直接放行（只读工具 / 自动应用模式下的非破坏性写 / 会话级已允许）。</summary>
    Allow = 0,
    /// <summary>需要用户审批。</summary>
    Ask = 1,
    /// <summary>拒绝（不暴露的工具 / 只读模式下的写）。</summary>
    Deny = 2,
}

/// <summary>
/// 权限判定链（功能书 §8.1；顺序固定，写进测试）：
/// ① 工具是否在暴露集内 → 否则 Deny；② 只读模式 → 只放行查询；③ 破坏性 → 必须审批（任何模式都问）；
/// ④ 查询 → 放行；⑤ 会话级允许 → 放行；⑥ 自动应用 → 放行；⑦ 其余 → 审批。
/// <para><b>关键不变量</b>：审批只能把"要问"变成"允许"，永远不能把"禁止"变成"允许"。</para>
/// <para><b>每次判定都带显式原因</b>（<see cref="Reason"/>）：放行 / 拦截都要能回答"为什么"，
/// 调用方把它写进日志（分类 <c>ai.permission</c>），排查"模型为什么被拒"时不必回读代码。</para>
/// </summary>
public static class AiPermissionChain
{
    /// <summary>判定结果 + 机器面原因码（英文、只进日志与测试，不上屏）。</summary>
    public readonly record struct AiPermissionVerdict(AiToolDecision Decision, string Reason);

    public static AiToolDecision Decide(CommandDescriptor? descriptor, AiMode mode, bool sessionAllowed,
        bool exposed = true)
        => Evaluate(descriptor, mode, sessionAllowed, exposed).Decision;

    /// <summary>完整判定链（七步顺序固定）。<paramref name="exposed"/> = 该工具是否在本次暴露集内
    /// （永不暴露 / Tier 未启用 = false——这一步压过一切，见功能书 §8.1 的关键不变量）。</summary>
    public static AiPermissionVerdict Evaluate(CommandDescriptor? descriptor, AiMode mode, bool sessionAllowed,
        bool exposed = true)
    {
        if (!exposed) return new(AiToolDecision.Deny, "not_exposed");                  // ① 暴露集（硬禁止，审批压不过）
        if (descriptor is null) return new(AiToolDecision.Deny, "unknown_tool");       // ① 目录里没有
        if (mode == AiMode.ReadOnly)                                                   // ② 只读会话
            return descriptor.IsQuery
                ? new(AiToolDecision.Allow, "readonly_query")
                : new(AiToolDecision.Deny, "readonly_write");
        if (descriptor.IsDestructive) return new(AiToolDecision.Ask, "destructive_needs_approval");   // ③ 破坏性必问
        if (descriptor.IsQuery) return new(AiToolDecision.Allow, "query");             // ④ 读
        if (sessionAllowed) return new(AiToolDecision.Allow, "session_allowance");     // ⑤ 本次会话已允许
        return mode == AiMode.AutoApply                                                // ⑥/⑦
            ? new(AiToolDecision.Allow, "auto_apply")
            : new(AiToolDecision.Ask, "confirm_each");
    }
}

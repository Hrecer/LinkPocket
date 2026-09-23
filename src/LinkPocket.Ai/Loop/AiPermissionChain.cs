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
/// </summary>
public static class AiPermissionChain
{
    public static AiToolDecision Decide(CommandDescriptor? descriptor, AiMode mode, bool sessionAllowed)
    {
        if (descriptor is null) return AiToolDecision.Deny;                    // ① 不在目录里
        if (mode == AiMode.ReadOnly)
            return descriptor.IsQuery ? AiToolDecision.Allow : AiToolDecision.Deny;   // ② 只读会话
        if (descriptor.IsDestructive) return AiToolDecision.Ask;               // ③ 破坏性必问
        if (descriptor.IsQuery) return AiToolDecision.Allow;                   // ④ 读
        if (sessionAllowed) return AiToolDecision.Allow;                       // ⑤ 本次会话已允许
        return mode == AiMode.AutoApply ? AiToolDecision.Allow : AiToolDecision.Ask;  // ⑥/⑦
    }
}

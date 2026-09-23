using LinkPocket.Contracts;

namespace LinkPocket.Views;

/// <summary>
/// AI 侧状态 / 错误码 → 文案键的**唯一映射**（UI.Ai 与 UI.Settings 共用，避免各写一份）。
/// 界面只按码取词，**从不显示英文技术文案**（HumanSummary / Message 一律不上屏）。
/// </summary>
public static class AiKeyMap
{
    /// <summary>服务商状态徽标 / 连通性结果 → 键。</summary>
    public static string Status(AiProviderStatus status) => status switch
    {
        AiProviderStatus.Verified => "ai.status.verified",
        AiProviderStatus.Failed => "ai.status.failed",
        AiProviderStatus.Configured => "ai.status.configured",
        _ => "ai.status.notConfigured",
    };

    /// <summary>LP.AI.* 错误码 → 键（未收录即通用文案）。</summary>
    public static string Error(string? code) => code switch
    {
        AiErrors.ProviderNotConfigured => "ai.err.providerNotConfigured",
        AiErrors.ProviderUnreachable => "ai.err.providerUnreachable",
        AiErrors.AuthFailed => "ai.err.authFailed",
        AiErrors.UpstreamRateLimited => "ai.err.upstreamRateLimited",
        AiErrors.ModelNotFound => "ai.err.modelNotFound",
        AiErrors.ModelListUnsupported => "ai.err.modelListUnsupported",
        AiErrors.BadResponse => "ai.err.badResponse",
        AiErrors.ContextOverflow => "ai.err.contextOverflow",
        AiErrors.ToolCallInvalid => "ai.err.toolCallInvalid",
        AiErrors.TurnCancelled => "ai.err.turnCancelled",
        AiErrors.QuotaExhausted => "ai.err.quotaExhausted",
        AiErrors.CredentialStoreFailed => "ai.err.credentialStoreFailed",
        AiErrors.SessionStoreFailed => "ai.err.sessionStoreFailed",
        AiErrors.UnsupportedCapability => "ai.err.unsupportedCapability",
        AiErrors.AiDataStoreFailed => "ai.err.dataStoreFailed",
        _ => "ai.err.generic",
    };

    /// <summary>工具调用状态 → 键。</summary>
    public static string ToolState(AiToolCallState state) => state switch
    {
        AiToolCallState.Pending => "ai.tool.state.pending",
        AiToolCallState.AwaitingApproval => "ai.tool.state.awaiting",
        AiToolCallState.Running => "ai.tool.state.running",
        AiToolCallState.Completed => "ai.tool.state.completed",
        AiToolCallState.Rejected => "ai.tool.state.rejected",
        AiToolCallState.Skipped => "ai.tool.state.skipped",
        _ => "ai.tool.state.failed",
    };
}

using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>
/// AI 层错误码（LP.AI.*）：与引擎 <see cref="EngineErrors"/> 同族同口径——码稳定、
/// Message 是**英文技术文案只进日志**、界面按「码 → 文案键」取词。
/// 生产者 = AI 运行时（不在引擎管道内），错误对象复用全站统一错误模型 <see cref="EngineError"/>。
/// </summary>
public static class AiErrors
{
    /// <summary>未选服务商 / 未配密钥。</summary>
    public const string ProviderNotConfigured = "LP.AI.001";
    /// <summary>网络不可达 / 超时（可重试）。</summary>
    public const string ProviderUnreachable = "LP.AI.002";
    /// <summary>密钥无效或无权限。</summary>
    public const string AuthFailed = "LP.AI.003";
    /// <summary>服务商限流（details 带 retry_after_ms；可重试）。</summary>
    public const string UpstreamRateLimited = "LP.AI.004";
    /// <summary>模型不存在 / 已下线。</summary>
    public const string ModelNotFound = "LP.AI.005";
    /// <summary>该服务商不提供模型列表（界面引导手填）。</summary>
    public const string ModelListUnsupported = "LP.AI.006";
    /// <summary>协议解析失败 / 响应残缺（可重试）。</summary>
    public const string BadResponse = "LP.AI.007";
    /// <summary>超出模型上下文预算。</summary>
    public const string ContextOverflow = "LP.AI.008";
    /// <summary>模型给出的工具参数不合法。</summary>
    public const string ToolCallInvalid = "LP.AI.009";
    /// <summary>用户停止。</summary>
    public const string TurnCancelled = "LP.AI.010";
    /// <summary>本地配额：单轮变更上限 / 调用频次 / 已有回合在跑（details.reason = turn_in_progress 等）。</summary>
    public const string QuotaExhausted = "LP.AI.011";
    /// <summary>凭据文件损坏 / 解密失败（提示重新输入密钥，绝不静默清空）。</summary>
    public const string CredentialStoreFailed = "LP.AI.012";
    /// <summary>会话文件损坏 / 不可写。</summary>
    public const string SessionStoreFailed = "LP.AI.013";
    /// <summary>模型不支持所选能力（工具调用 / 流式）。</summary>
    public const string UnsupportedCapability = "LP.AI.014";

    /// <summary>缺省可重试性（网络 / 上游限流 / 响应残缺 = 可重试）。</summary>
    public static bool IsRetryableByDefault(string code)
        => code is ProviderUnreachable or UpstreamRateLimited or BadResponse;

    /// <summary>工厂：与 <see cref="EngineErrors.Of"/> 同形；retryable 缺省按码判定。</summary>
    public static EngineError Of(string code, string message, JsonElement? details = null,
        bool? retryable = null, string? correlationId = null)
        => new(code, message, details, retryable ?? IsRetryableByDefault(code), correlationId ?? "unknown");
}

/// <summary>AI 层调用失败异常（与 <see cref="EngineException"/> 同形；UI 按 <see cref="EngineError.Code"/> 取词，不展示 Message）。</summary>
public sealed class AiException : Exception
{
    public EngineError Error { get; }

    public AiException(EngineError error) : base(error.Message) => Error = error;
}

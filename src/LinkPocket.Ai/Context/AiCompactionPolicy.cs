using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>压缩阈值判定结果（Reason 是机器面原因码，只进日志与测试）。</summary>
internal readonly record struct AiCompactionDecision(bool ShouldCompact, string Reason, int EstimatedTokens, int Threshold);

/// <summary>微压缩判定结果。</summary>
internal readonly record struct AiMicroCompactionDecision(bool Applied, string Reason, int EstimatedTokens,
    int Threshold, int ClearedCount, int TokensSaved);

/// <summary>
/// 压缩策略（纯函数，**唯一实现**；口径见功能书 §6.4）：
/// 窗口 = 模型声明的上下文窗口（未声明 = 偏好里的上下文预算）；输出预留 = min(maxOutputTokens ?? 4096, 8192)；
/// 阈值 = 窗口 − 预留 − 缓冲（下限 1000）；微压缩阈值 = min(阈值 × 90%, 阈值 − 2000)。
/// </summary>
internal static class AiCompactionPolicy
{
    /// <summary>未声明窗口时的保守缺省（与偏好缺省同值）。</summary>
    public const int FallbackWindowTokens = 32_000;

    /// <summary>输出侧最多预留多少（上下文窗口是输入+输出共享的）。</summary>
    public const int MaxOutputReserveTokens = 8_192;

    /// <summary>阈值缓冲：贴边压缩会与请求预算打架，留一段余量。</summary>
    public const int BufferTokens = 2_000;

    /// <summary>微压缩保留的最近工具结果**组**数（一组 = 一次助手工具调用产生的全部结果）。</summary>
    public const int KeepRecentToolResultGroups = 6;

    /// <summary>微压缩最小节省：不足即视为没省（不做无意义改写）。</summary>
    public const int MinTokensSaved = 256;

    /// <summary>摘要连续失败上限（熔断；成功一次清零）。</summary>
    public const int MaxConsecutiveSummaryFailures = 3;

    /// <summary>空闲触发的判定窗口：距上次助手完成超过它，回合开始也评估一次微压缩。</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(60);

    public static int WindowTokens(AiModelInfo model, AiPreferences preferences)
        => model.ContextWindow is > 0 ? model.ContextWindow.Value
            : preferences.ContextBudgetTokens > 0 ? preferences.ContextBudgetTokens
            : FallbackWindowTokens;

    public static int OutputReserveTokens(AiModelInfo model)
        => Math.Min(model.MaxOutputTokens ?? 4096, MaxOutputReserveTokens);

    public static int ThresholdTokens(AiModelInfo model, AiPreferences preferences)
        => Math.Max(1000, WindowTokens(model, preferences) - OutputReserveTokens(model) - BufferTokens);

    public static int MicroThresholdTokens(int threshold)
        => Math.Max(0, Math.Min(threshold * 9 / 10, threshold - BufferTokens));

    /// <summary>摘要判定：估算体量达到全量压缩阈值才做摘要（微压缩先行）。</summary>
    public static AiCompactionDecision ShouldSummarize(int estimatedTokens, AiModelInfo model, AiPreferences preferences,
        int consecutiveFailures)
    {
        var threshold = ThresholdTokens(model, preferences);
        if (consecutiveFailures >= MaxConsecutiveSummaryFailures)
            return new(false, "circuit_breaker", estimatedTokens, threshold);
        return estimatedTokens >= threshold
            ? new(true, "above_threshold", estimatedTokens, threshold)
            : new(false, "below_threshold", estimatedTokens, threshold);
    }

    /// <summary>微压缩判定：达到微压缩阈值，或空闲超窗（空闲只触发微压缩，不触发摘要）。</summary>
    public static AiMicroCompactionDecision ShouldMicroCompact(int estimatedTokens, int threshold,
        bool idle)
    {
        var micro = MicroThresholdTokens(threshold);
        if (idle) return new(true, "idle", estimatedTokens, micro, 0, 0);
        return estimatedTokens >= micro
            ? new(true, "token_pressure", estimatedTokens, micro, 0, 0)
            : new(false, "not_triggered", estimatedTokens, micro, 0, 0);
    }
}

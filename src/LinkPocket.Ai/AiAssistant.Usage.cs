using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>用量面（AiAssistant 的 partial）：本会话读数（会话文件逐轮求和）+ 近 N 天汇总（usage.json）。</summary>
public sealed partial class AiAssistant
{
    public Task<AiSessionUsage> GetSessionUsageAsync(string sessionId, CancellationToken ct = default)
    {
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        var latest = file.Turns.LastOrDefault(t => t.ContextTokens is > 0);
        return Task.FromResult(new AiSessionUsage(
            file.Turns.Count,
            file.ToolCalls.Count,
            file.Turns.Sum(t => (long)(t.InputTokens ?? 0)),
            file.Turns.Sum(t => (long)(t.OutputTokens ?? 0)),
            latest?.ContextTokens,
            latest?.ContextWindowTokens ?? 0));
    }

    public Task<AiUsageSummary> GetUsageSummaryAsync(int days = 7, CancellationToken ct = default)
        => Task.FromResult(_usage.Summary(days));

    /// <summary>
    /// 记一笔模型用量：逐轮读数由调用方并入回合记录（<see cref="TurnRun"/>），这里只写"天 × 模型 × 用途"汇总。
    /// 汇总文件写失败**只降级**（日志 + 不否定已完成的回合/摘要）——它是统计面，不是事实源。
    /// </summary>
    private void RecordUsage(string providerId, string modelId, AiUsagePurpose purpose, int? input, int? output)
    {
        if (input is null && output is null) return;
        try
        {
            _usage.Record(DateTimeOffset.UtcNow, providerId, modelId, purpose, input ?? 0, output ?? 0);
        }
        catch (AiException ex)
        {
            LpLog.Warn("usage rollup write failed (the reading is dropped, the turn is unaffected)", ex,
                category: "ai.usage");
        }
    }
}

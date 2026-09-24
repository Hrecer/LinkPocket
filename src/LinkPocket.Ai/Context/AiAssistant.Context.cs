using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 上下文压缩面（AiAssistant 的 partial；口径见功能书 §6.4）：
/// 每次模型请求前评估一次——先微压缩（本地零成本），仍超阈值再做 LLM 摘要（固定 9 段模板，
/// 最近 1 组回合逐字保留）；摘要连续失败 3 次熔断。压缩只改**模型面历史**（<see cref="AiSessionFile.Chat"/>），
/// 对话流与台账不缩水；每次压缩在对话流如实留一条提示条。
/// </summary>
public sealed partial class AiAssistant
{
    /// <summary>压缩评估与执行（在每次模型请求之前调用；不抛——压缩失败不该杀死回合）。
    /// 返回 = **本次将要发出去的那份历史**的上下文估算（压缩后重算；界面用量环按它如实读数）。</summary>
    private async Task<int> CompactContextIfNeededAsync(AiSessionFile file, TurnRun run, AiProviderInfo provider,
        AiModelInfo model, AiPreferences preferences, string systemPrompt, string? apiKey)
    {
        try
        {
            var threshold = AiCompactionPolicy.ThresholdTokens(model, preferences);
            var idle = IsIdle(file);
            var estimate = AiTokenEstimator.Estimate(systemPrompt) + AiTokenEstimator.EstimateChat(file.Chat);

            var micro = AiCompactionPolicy.ShouldMicroCompact(estimate, threshold, idle);
            if (micro.Applied)
            {
                var result = AiMicroCompactor.Apply(file.Chat, AiCompactionPolicy.KeepRecentToolResultGroups,
                    AiCompactionPolicy.MinTokensSaved);
                if (result.Applied)
                {
                    file.Chat.Clear();
                    file.Chat.AddRange(result.Messages);
                    Persist(file);
                    NotifyCompaction(file, run, new AiContextCompaction(run.TurnId, false, result.ClearedCount,
                        result.TokensSaved, 0));
                    LpLog.Debug($"micro compaction applied: cleared={result.ClearedCount} saved={result.TokensSaved} trigger={micro.Reason}",
                        category: "ai.context");
                    estimate = AiTokenEstimator.Estimate(systemPrompt) + AiTokenEstimator.EstimateChat(file.Chat);
                }
                else
                {
                    LpLog.Debug($"micro compaction skipped: {result.Reason}", category: "ai.context");
                }
            }

            var decision = AiCompactionPolicy.ShouldSummarize(estimate, model, preferences, file.CompactFailures);
            if (!decision.ShouldCompact)
            {
                if (decision.Reason == "circuit_breaker")
                    LpLog.Warn($"context summarization is stopped after {file.CompactFailures} consecutive failures",
                        category: "ai.context");
            }
            else
            {
                await SummarizeAsync(file, run, provider, model, apiKey).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is AiException or JsonException)
        {
            // 压缩是优化项：失败只如实留痕，不杀死回合、不假装压缩过
            file.CompactFailures++;
            Persist(file);
            LpLog.Warn($"context summarization failed ({file.CompactFailures}/{AiCompactionPolicy.MaxConsecutiveSummaryFailures})",
                ex, category: "ai.context");
            if (file.CompactFailures >= AiCompactionPolicy.MaxConsecutiveSummaryFailures)
                NotifyCompaction(file, run, new AiContextCompaction(run.TurnId, true, 0, 0, 0) { Failed = true });
        }

        return AiTokenEstimator.Estimate(systemPrompt) + AiTokenEstimator.EstimateChat(file.Chat);
    }

    /// <summary>LLM 摘要：把"除最近 1 组回合以外"的历史换成一条 user 角色的摘要消息。</summary>
    private async Task SummarizeAsync(AiSessionFile file, TurnRun run, AiProviderInfo provider, AiModelInfo model,
        string? apiKey)
    {
        var groups = GroupChatRounds(file.Chat);
        if (groups.Count < 2) return;   // 只有当前这一组：无可摘要的旧历史

        var preserve = groups[^1];
        var toSummarize = groups.Take(groups.Count - 1).SelectMany(g => g).ToArray();
        if (!toSummarize.Any(m => m.Role == "assistant")) return;

        var adapter = AiProtocols.For(provider.Protocol);
        var request = new AiChatRequest(model.Id, AiContextSummary.BuildPrompt(), toSummarize, [],
            Math.Min(model.MaxOutputTokens ?? 2048, 4096), Stream: false);
        var response = await _http.SendAsync(adapter.BuildChatRequest(provider, apiKey, request), run.Cts.Token)
            .ConfigureAwait(false);
        var completion = adapter.ParseCompletion(response.Body);
        RecordUsage(provider.Id, model.Id, AiUsagePurpose.Summary, completion.InputTokens, completion.OutputTokens);
        run.InputTokens += completion.InputTokens ?? 0;
        run.OutputTokens += completion.OutputTokens ?? 0;

        var formatted = AiContextSummary.Format(completion.Text);
        if (formatted.Length == 0)
            throw new AiException(AiErrors.Of(AiErrors.BadResponse, "the summarizer returned an empty summary"));

        file.Chat.Clear();
        file.Chat.Add(new AiChatMessage("user", AiContextSummary.BuildSummaryMessage(formatted)));
        file.Chat.AddRange(preserve);
        file.CompactFailures = 0;
        Persist(file);
        NotifyCompaction(file, run, new AiContextCompaction(run.TurnId, true, 0, 0, toSummarize.Length));
        LpLog.Debug($"context summary applied: summarized={toSummarize.Length} kept={preserve.Count}",
            category: "ai.context");
    }

    /// <summary>按"助手回合"分组（一条助手消息起一组；工具结果与用户消息随组）。</summary>
    internal static List<List<AiChatMessage>> GroupChatRounds(IReadOnlyList<AiChatMessage> messages)
    {
        var groups = new List<List<AiChatMessage>>();
        List<AiChatMessage>? current = null;
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && current is { Count: > 0 })
            {
                groups.Add(current);
                current = null;
            }
            current ??= [];
            current.Add(message);
        }
        if (current is { Count: > 0 }) groups.Add(current);
        return groups;
    }

    /// <summary>空闲判定：距上一次回合结束超过空闲窗口（没有历史回合 = 不空闲）。</summary>
    private static bool IsIdle(AiSessionFile file)
    {
        var lastEnded = file.Turns.LastOrDefault(t => t.EndedAt is not null)?.EndedAt;
        return lastEnded is { } ended && DateTimeOffset.UtcNow - ended > AiCompactionPolicy.IdleThreshold;
    }

    private void NotifyCompaction(AiSessionFile file, TurnRun run, AiContextCompaction compaction)
        => Notified?.Invoke(new AiNotification(AiNotificationKind.ContextCompacted, file.Summary.SessionId,
            TurnId: run.TurnId, Compaction: compaction));
}

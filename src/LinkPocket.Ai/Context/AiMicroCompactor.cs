namespace LinkPocket.Ai;

/// <summary>微压缩结果（Applied = false 时 Messages 原样返回）。</summary>
internal sealed record AiMicroCompactionOutcome(bool Applied, string Reason, int ClearedCount, int TokensSaved,
    IReadOnlyList<AiChatMessage> Messages);

/// <summary>
/// 微压缩（纯函数、**唯一实现**；口径见功能书 §6.4）：
/// 按"助手回合"**整组**处理——组 = 一条带工具调用的助手消息 + 其后的全部工具结果；
/// 把最旧若干组的工具结果**内容**替换为占位符（保留最近 N 组）；**绝不删**承载 `tool_calls` 的助手消息
/// （拆散配对会被上游拒绝，见 WARNINGS 136），也不动用户 / 助手文本与已清空过的结果。
/// </summary>
internal static class AiMicroCompactor
{
    /// <summary>占位符（英文机器面：只回灌给模型，不上屏）。</summary>
    public const string ClearedPlaceholder = "[Old tool result content cleared]";

    public static AiMicroCompactionOutcome Apply(IReadOnlyList<AiChatMessage> messages, int keepRecentGroups,
        int minTokensSaved)
    {
        var groups = CollectResultGroups(messages);
        var keep = Math.Max(1, keepRecentGroups);
        var clearCount = groups.Count - keep;
        if (clearCount <= 0)
            return new(false, "nothing_to_clear", 0, 0, messages);

        var toClear = groups.Take(clearCount).SelectMany(g => g).ToHashSet();
        var rewritten = new List<AiChatMessage>(messages.Count);
        var cleared = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (toClear.Contains(index) && message.Role == "tool" && message.Text != ClearedPlaceholder)
            {
                rewritten.Add(message with { Text = ClearedPlaceholder });
                cleared++;
            }
            else
            {
                rewritten.Add(message);
            }
        }

        if (cleared == 0) return new(false, "nothing_to_clear", 0, 0, messages);

        var before = AiTokenEstimator.EstimateChat(messages);
        var after = AiTokenEstimator.EstimateChat(rewritten);
        var saved = Math.Max(0, before - after);
        return saved < minTokensSaved
            ? new(false, "below_min_savings", 0, 0, messages)
            : new(true, "applied", cleared, saved, rewritten);
    }

    /// <summary>可清空结果的**组**集合：每组 = 一条带工具调用的助手消息之后、到下一条助手消息之前的工具结果下标。</summary>
    private static List<List<int>> CollectResultGroups(IReadOnlyList<AiChatMessage> messages)
    {
        var groups = new List<List<int>>();
        List<int>? current = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                if (current is { Count: > 0 }) groups.Add(current);
                current = [];
                continue;
            }
            if (message.Role != "tool" || message.Text == ClearedPlaceholder) continue;
            if (current is null)
            {
                groups.Add([index]);
                continue;
            }
            current.Add(index);
        }
        if (current is { Count: > 0 }) groups.Add(current);
        return groups;
    }
}

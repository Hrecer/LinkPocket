namespace LinkPocket.Ai;

/// <summary>
/// 本地 token 估算（**唯一实现**）：字符数 ÷ 3，中日韩字符按 2 倍权重计入。
/// 用途 = 上下文预算判定与用量估算（与两处读数同源）；**不引 tokenizer 库**——估算是保守近似，
/// 宁可早压一点，不可溢出（溢出是硬失败，早压只是多一次摘要）。
/// </summary>
internal static class AiTokenEstimator
{
    private const int Divisor = 3;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        foreach (var ch in text)
            if (IsCjk(ch))
                cjk++;
        var other = text.Length - cjk;
        return (cjk * 2 + other + Divisor - 1) / Divisor;
    }

    /// <summary>估算一段模型历史的体量（正文 + 工具调用名与参数；工具 schema 不计）。</summary>
    public static int EstimateChat(IEnumerable<AiChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += Estimate(message.Text);
            if (message.ToolCalls is not { Count: > 0 } calls) continue;
            foreach (var call in calls) total += Estimate(call.Name) + Estimate(call.ArgumentsJson);
        }
        return total;
    }

    private static bool IsCjk(char ch)
        => ch is >= '\u4e00' and <= '\u9fff'      // 基本汉字
        or >= '\u3000' and <= '\u303f'            // CJK 标点
        or >= '\u3040' and <= '\u30ff'            // 假名
        or >= '\uff00' and <= '\uffef';           // 全角
}

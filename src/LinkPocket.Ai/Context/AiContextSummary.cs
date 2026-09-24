namespace LinkPocket.Ai;

/// <summary>
/// 上下文摘要的提示词与产物流形（**唯一实现**；口径见功能书 §6.4）：
/// 固定 9 段模板 + "只许文本、不得调用工具"的硬前言；产出要求 `<analysis>` + `<summary>` 两段，
/// 只取 `<summary>`；摘要以 user 角色消息注入模型历史（写明是"被压缩的早期对话摘要"）。
/// </summary>
internal static class AiContextSummary
{
    private const string NoToolsPreamble = """
        CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.
        - You already have all the context you need in the conversation above.
        - Tool calls will be REJECTED and will waste your only turn.
        - Your entire response must be plain text: an <analysis> block followed by a <summary> block.

        """;

    private const string Trailer =
        "\n\nREMINDER: Do NOT call any tools. Respond with plain text only - "
        + "an <analysis> block followed by a <summary> block.";

    private const string Body = """
        Your task is to summarize the earlier part of this bookmark-assistant conversation so work can continue
        without losing context. Be thorough and factual; never invent ids, names or values you did not see.

        First wrap your reasoning in <analysis> tags. Then produce a <summary> with EXACTLY these nine sections:

        1. Primary intent: the user's explicit requests and intents.
        2. Objects and folders involved: folders / links / trash units touched or referenced (kind, id, name, canonical path when known).
        3. Changes already made and their results: which command touched which entity, what changed (old -> new), success / failure / rejected / dry-run.
        4. Failures and fixes: errors encountered, what was corrected, and how.
        5. Current state: what holds right now (entities created, pending approvals, open questions).
        6. All user messages: every user message, VERBATIM (do not paraphrase, do not drop any).
        7. Pending tasks: what the user asked for that is not done yet.
        8. Next step: the single next action to continue.
        9. Security constraints: every security-relevant instruction the user or the app stated, VERBATIM
           (destructive operations, credential handling, data that must not be touched).

        Keep ids and paths exactly as written. Prefer concrete facts over narrative.
        """;

    /// <summary>摘要请求提示词（system 位）。</summary>
    public static string BuildPrompt()
        => NoToolsPreamble + Body + Trailer;

    /// <summary>产物流形：去掉 `<analysis>`、把 `<summary>` 解包成 "Summary:" 段；空产出返回空串。</summary>
    public static string Format(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return "";

        text = System.Text.RegularExpressions.Regex.Replace(text, "<analysis>[\\s\\S]*?</analysis>", "");
        var match = System.Text.RegularExpressions.Regex.Match(text, "<summary>([\\s\\S]*?)</summary>");
        if (match.Success)
            text = System.Text.RegularExpressions.Regex.Replace(text, "<summary>[\\s\\S]*?</summary>",
                "Summary:\n" + match.Groups[1].Value.Trim());
        return System.Text.RegularExpressions.Regex.Replace(text, "\n{3,}", "\n\n").Trim();
    }

    /// <summary>注入模型历史的摘要消息（user 角色）。</summary>
    public static string BuildSummaryMessage(string formattedSummary)
        => "This session is being continued from a previous conversation that ran out of context. "
           + "The summary below covers the earlier portion of the conversation.\n\n"
           + formattedSummary
           + "\n\nContinue from where it left off without asking further questions about the summary itself.";
}

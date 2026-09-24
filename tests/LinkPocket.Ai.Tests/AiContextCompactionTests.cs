using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 上下文压缩（P4-1）：微压缩（清旧工具结果、保留最近组、不动用户/助手文本）、
/// LLM 摘要（9 段模板、user 角色注入、最近一组逐字保留）、熔断（连续失败 3 次）。
/// 断言口径 = **会话文件（模型面历史）与模型请求体**（黑盒；文件形状即契约，见功能书 §9.1）。
/// </summary>
public sealed class AiContextCompactionTests
{
    private const string Placeholder = "[Old tool result content cleared]";

    /// <summary>预置一份"8 组助手工具调用"的长历史（每组结果 800 字符）。</summary>
    private static List<AiTestHost.ChatLine> LongToolHistory(int groups = 8, int size = 800)
    {
        var chat = new List<AiTestHost.ChatLine> { new("user", "用户原始意图：整理书签") };
        for (var i = 0; i < groups; i++)
        {
            chat.Add(new AiTestHost.ChatLine("assistant", $"第 {i} 步结论",
                [new AiToolCallRequest($"call-{i}", "folders.create", """{"name":"x"}""")]));
            chat.Add(new AiTestHost.ChatLine("tool", new string((char)('A' + i), size), ToolCallId: $"call-{i}"));
        }
        return chat;
    }

    [Fact]
    public async Task 微压缩_清空最旧组的工具结果_保留最近六组_用户与助手文本原样()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("done")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        // 微压缩后仍超阈值 → 接着走摘要；摘要是"清空后的历史"的消费者，这里给一份可行产出
        host.Transport.EnqueueCompletion(AiTestHost.Completion("<summary>1. Primary intent: 整理书签</summary>"));
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId, LongToolHistory());

        var compactions = new List<AiContextCompaction>();
        host.Assistant.Notified += n => { if (n.Compaction is { } c) compactions.Add(c); };

        await host.Assistant.SendAsync(sessionId, "继续");

        // 微压缩确实跑了：最旧 2 组被清、最近 6 组保留
        var micro = compactions.First(c => !c.IsSummary);
        Assert.Equal(2, micro.ClearedToolResults);
        Assert.True(micro.TokensSaved >= 256);

        // 摘要请求吃到的正是"清空后的历史"（占位符在，被清组原文不在，最近一组逐字在）
        var summaryBody = AiTestHost.BodyText(host.Transport.Captured
            .Single(r => r.BodyJson!.Contains("CRITICAL: Respond with TEXT ONLY", StringComparison.Ordinal)));
        Assert.Contains(Placeholder, summaryBody);
        Assert.DoesNotContain(new string('A', 800), summaryBody);
        Assert.Contains(new string('G', 800), summaryBody);
        Assert.Contains("用户原始意图：整理书签", summaryBody);                // 用户消息逐字进摘要材料
        Assert.Contains("第 0 步结论", summaryBody);                          // 助手结论逐字进摘要材料

        // 压缩后的 Chat：摘要消息打头 + **最近一组**（第 7 组的结论与结果）逐字保留
        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        var chat = file.GetProperty("Chat").EnumerateArray().ToArray();
        Assert.Contains("This session is being continued", chat[0].GetProperty("Text").GetString());
        var texts = AiTestHost.ChatTexts(file);
        Assert.Contains("第 7 步结论", texts);
        Assert.Contains(new string('H', 800), texts);
        Assert.DoesNotContain("第 0 步结论", texts);                    // 更早的回合已被摘要取代
        Assert.DoesNotContain(Placeholder, texts);                      // 占位符随摘要一起退场（只留在摘要材料里）
    }

    [Fact]
    public async Task 摘要_超阈值时生成摘要_以用户角色注入_最近一组逐字保留()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("继续完成")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        host.Transport.EnqueueCompletion(AiTestHost.Completion(
            "<analysis>略</analysis><summary>1. Primary intent: 整理书签</summary>"));
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId,
        [
            new AiTestHost.ChatLine("user", "原始意图：整理书签"),
            new AiTestHost.ChatLine("assistant", new string('B', 3_000)),
            new AiTestHost.ChatLine("user", "继续"),
            new AiTestHost.ChatLine("assistant", new string('C', 3_000)),
        ]);

        var compactions = new List<AiContextCompaction>();
        host.Assistant.Notified += n => { if (n.Compaction is { } c) compactions.Add(c); };

        await host.Assistant.SendAsync(sessionId, "再来");

        // 摘要请求（非流式）：无工具前言 + 9 段模板都在
        var summaryRequest = host.Transport.Captured
            .Single(r => r.BodyJson!.Contains("CRITICAL: Respond with TEXT ONLY", StringComparison.Ordinal));
        var summaryBody = AiTestHost.BodyText(summaryRequest);
        Assert.Contains("All user messages", summaryBody);                     // 用户消息逐字
        Assert.Contains("Security constraints", summaryBody);                  // 安全约束逐字
        Assert.DoesNotContain("\"tools\":", summaryRequest.BodyJson!);          // 摘要请求不带工具

        // 注入形态：Chat 第一条 = user 角色的摘要消息；最近一组（含 assistants 的 3000 字）逐字保留
        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        var chat = file.GetProperty("Chat").EnumerateArray().ToArray();
        Assert.Equal("user", chat[0].GetProperty("Role").GetString());
        Assert.Contains("This session is being continued", chat[0].GetProperty("Text").GetString());
        Assert.Contains("Summary:", chat[0].GetProperty("Text").GetString());   // <analysis> 已剥掉、summary 已解包
        var texts = AiTestHost.ChatTexts(file);
        Assert.Contains(new string('C', 3_000), texts);
        Assert.DoesNotContain(new string('B', 3_000), texts);                   // 旧历史被摘要替换

        Assert.Contains(compactions, c => c.IsSummary && c.MessagesSummarized > 0);
    }

    [Fact]
    public async Task 摘要失败_计数入会话文件_且不杀死回合()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("仍然完成")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        host.Transport.EnqueueCompletion(AiTestHost.Completion(""));            // 空摘要 = 如实失败
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId,
        [
            new AiTestHost.ChatLine("assistant", new string('B', 3_000)),
            new AiTestHost.ChatLine("user", "继续"),
            new AiTestHost.ChatLine("assistant", new string('C', 3_000)),
        ]);

        await host.Assistant.SendAsync(sessionId, "再来");                       // 不抛 = 压缩失败没有杀死回合

        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        Assert.Equal(1, file.GetProperty("CompactFailures").GetInt32());
        Assert.Contains(new string('B', 3_000), AiTestHost.ChatTexts(file));     // 历史未被改写（摘要没成功就不动）
        var detail = await host.Assistant.GetSessionAsync(sessionId);
        Assert.Equal(AiTurnState.Completed, detail.Turns[^1].State);
    }

    [Fact]
    public async Task 摘要熔断_连续失败达上限后不再发起摘要请求()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("完成")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        // 熔断计数已达上限：不排任何非流式回复——真的发起摘要请求就会抛（NotSupportedException）
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId,
        [
            new AiTestHost.ChatLine("assistant", new string('B', 3_000)),
            new AiTestHost.ChatLine("user", "继续"),
            new AiTestHost.ChatLine("assistant", new string('C', 3_000)),
        ], compactFailures: 3);

        await host.Assistant.SendAsync(sessionId, "再来");

        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        Assert.Equal(3, file.GetProperty("CompactFailures").GetInt32());
        Assert.DoesNotContain(host.Transport.Captured,
            r => r.BodyJson!.Contains("CRITICAL: Respond with TEXT ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 微压缩_节省不足即不改写()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("done")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        // 8 组但每组结果只有 20 字符：清了也省不到最小节省 → 不改写
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId, LongToolHistory(groups: 8, size: 20));

        await host.Assistant.SendAsync(sessionId, "继续");

        var texts = AiTestHost.ChatTexts(AiTestHost.ReadSessionFile(host.DataRoot, sessionId));
        Assert.DoesNotContain(Placeholder, texts);
    }
}

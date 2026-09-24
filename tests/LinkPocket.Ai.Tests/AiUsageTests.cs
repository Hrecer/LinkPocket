using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 用量统计（P4-4）：两协议流式 usage 采集（OpenAI 的 usage 块 / Anthropic 的 message_start+message_delta）、
/// 按轮记入会话文件、按「天 × 模型 × 用途」落 usage.json、汇总读数与损坏语义。
/// </summary>
public sealed class AiUsageTests
{
    [Fact]
    public async Task 流式用量_OpenAI的usage块_记入回合与汇总文件()
    {
        using var host = AiTestHost.NewHost([[
            AiTestHost.UsageChunk(120, 30),
            AiTestHost.TextChunk("答"),
            "data: [DONE]",
        ]]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.ReadOnly);

        await host.Assistant.SendAsync(sessionId, "问");

        var usage = await host.Assistant.GetSessionUsageAsync(sessionId);
        Assert.Equal(1, usage.Turns);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        // 上下文占用读数（界面用量环的数据源）：本地估算 > 0；模型未声明窗口 → 偏好缺省窗口
        Assert.True(usage.ContextTokens > 0);
        Assert.Equal(32_000, usage.ContextWindowTokens);

        var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.DataRoot, "usage.json"))).RootElement;
        var entry = file.GetProperty("Entries").EnumerateArray().Single();
        Assert.Equal("gw", entry.GetProperty("ProviderId").GetString());
        Assert.Equal("test-model", entry.GetProperty("ModelId").GetString());
        Assert.Equal((int)AiUsagePurpose.Turn, entry.GetProperty("Purpose").GetInt32());
        Assert.Equal(120, entry.GetProperty("InputTokens").GetInt64());
        Assert.Equal(30, entry.GetProperty("OutputTokens").GetInt64());
    }

    [Fact]
    public async Task 流式用量_Anthropic两处给_输入输出都采到()
    {
        using var host = AiTestHost.NewHost([AiTestHost.AnthropicStream("好")]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.ReadOnly,
            protocol: AiProtocol.AnthropicMessages, baseUrl: "https://gw.example.com");

        await host.Assistant.SendAsync(sessionId, "问");

        var usage = await host.Assistant.GetSessionUsageAsync(sessionId);
        Assert.Equal(88, usage.InputTokens);
        Assert.Equal(21, usage.OutputTokens);
    }

    [Fact]
    public async Task 摘要用量_按用途分账()
    {
        using var host = AiTestHost.NewHost([[
            AiTestHost.UsageChunk(70, 9),
            AiTestHost.TextChunk("完成"),
            "data: [DONE]",
        ]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply, contextWindow: 2_000);
        host.Transport.EnqueueCompletion(AiTestHost.Completion("<summary>1. Primary intent: 整理</summary>"));
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        AiTestHost.SeedSessionFile(host.DataRoot, sessionId,
        [
            new AiTestHost.ChatLine("assistant", new string('B', 3_000)),
            new AiTestHost.ChatLine("user", "继续"),
            new AiTestHost.ChatLine("assistant", new string('C', 3_000)),
        ]);

        await host.Assistant.SendAsync(sessionId, "再来");

        var entries = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.DataRoot, "usage.json")))
            .RootElement.GetProperty("Entries").EnumerateArray().ToArray();
        Assert.Contains(entries, e => e.GetProperty("Purpose").GetInt32() == (int)AiUsagePurpose.Summary);
        Assert.Contains(entries, e => e.GetProperty("Purpose").GetInt32() == (int)AiUsagePurpose.Turn);
    }

    [Fact]
    public void 用量汇总_按天聚合_且损坏如实暴露()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiUsageStore(root);
            var today = DateTimeOffset.Now;
            store.Record(today, "gw", "m1", AiUsagePurpose.Turn, 100, 20);
            store.Record(today, "gw", "m1", AiUsagePurpose.Turn, 50, 10);
            store.Record(today, "gw", "m2", AiUsagePurpose.Summary, 30, 5);
            store.Record(today.AddDays(-3), "gw", "m1", AiUsagePurpose.Turn, 7, 3);
            store.Record(today.AddDays(-40), "gw", "m1", AiUsagePurpose.Turn, 999, 999);   // 窗口外

            var summary = store.Summary(7);
            Assert.Equal(7, summary.Days);
            Assert.Equal(4, summary.Calls);                    // 40 天前那条不计入
            Assert.Equal(187, summary.InputTokens);
            Assert.Equal(38, summary.OutputTokens);
            Assert.Equal(2, summary.Items.Count(i => i.Calls > 0));   // 今天 + 三天前各一天

            File.WriteAllText(store.FilePath, "{ broken");
            var error = Assert.Throws<AiException>(() => store.Summary(7));
            Assert.Equal(AiErrors.AiDataStoreFailed, error.Error.Code);
            Assert.Throws<AiException>(() =>
                store.Record(today, "gw", "m1", AiUsagePurpose.Turn, 1, 1));
        }
        finally
        {
            AiTestEnv.Drop(root);
        }
    }
}

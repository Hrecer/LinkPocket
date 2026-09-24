using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// AI 页时间线的投影金标准：回合分隔行（用时 / 变更数 / 折叠）、会话行（状态 / 时间 / 徽标）、
/// 轮次导航轨、模型下拉、上下文用量环、右栏收起。断言口径 = 可观测投影，不测实现细节。
/// </summary>
public class AiTimelineTests
{
    private static (AiViewModel Vm, StubAiAssistant Stub) NewVm(AiSessionDetail? detail = null)
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(new AiModelSelection("gw", "m1"), null, "gw", "m1"),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        if (detail is not null) stub.Details["s-1"] = detail;
        return (new AiViewModel(stub), stub);
    }

    /// <summary>两轮：t-1 有用户消息 / 助手消息 / 一条工具调用 + 一条变更；t-2 只有两条消息。</summary>
    private static AiSessionDetail TwoTurns()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-3);
        var summary = new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1",
            start, start, 4, 1, null);
        var turns = new List<AiTurn>
        {
            new("t-1", 1, AiTurnState.Completed, start, start.AddSeconds(12), null, 1, 1, 1, false),
            new("t-2", 2, AiTurnState.Completed, start.AddMinutes(1), start.AddMinutes(1).AddSeconds(30),
                null, 0, 0, 1, false),
        };
        var messages = new List<AiMessage>
        {
            new("m-1", 1, AiRole.User, "第一问", start, "t-1"),
            new("m-2", 2, AiRole.Assistant, "第一答", start.AddSeconds(2), "t-1"),
            new("m-3", 4, AiRole.User, "第二问", start.AddMinutes(1), "t-2"),
            new("m-4", 5, AiRole.Assistant, "第二答", start.AddMinutes(1).AddSeconds(2), "t-2"),
        };
        var calls = new List<AiToolCall>
        {
            new("c-1", 3, "t-1", "folders.create", AiToolCallState.Completed, "{\"name\":\"工作\"}", "{}",
                "1 change(s)", null, 12, false, "ai:t-1", null, start.AddSeconds(1)),
        };
        var changes = new List<AiChange>
        {
            new("ch-1", 6, "t-1", "c-1", "folders.create", AiChangeKind.Create, "folder", "f-1", "工作",
                "@root/工作", true, [new AiFieldChange("name", null, JsonSerializer.SerializeToElement("工作"))],
                AiChangeOutcome.Applied, null, false, AiChangeSource.EngineDiff, false, 0,
                start.AddSeconds(1), "ai:t-1", null),
        };
        return new AiSessionDetail(summary, messages, calls, changes, [], turns);
    }

    [Fact]
    public async Task 时间线_按回合分组_分隔行在前_条目按序()
    {
        var (vm, _) = NewVm(TwoTurns());

        await vm.LoadAsync();

        Assert.Equal(new[]
        {
            AiFeedItem.ItemKind.TurnHeader, AiFeedItem.ItemKind.UserMessage, AiFeedItem.ItemKind.AssistantMessage,
            AiFeedItem.ItemKind.ToolCall, AiFeedItem.ItemKind.TurnHeader, AiFeedItem.ItemKind.UserMessage,
            AiFeedItem.ItemKind.AssistantMessage,
        }, vm.Feed.Select(i => i.Kind));
        Assert.Equal(new[] { 1, 2 }, vm.Turns.Select(t => t.TurnIndex));
        Assert.True(vm.ShowRail);
        Assert.True(vm.Turns[^1].IsActive);   // 进页停在最新一轮
        Assert.False(vm.IsConversationEmpty);
    }

    [Fact]
    public async Task 时间线_分隔行文案_用时与变更数()
    {
        var (vm, _) = NewVm(TwoTurns());

        await vm.LoadAsync();

        Assert.Equal("ai.turn.workedChanges", vm.Turns[0].HeaderValue.Key);   // 用时 12 秒 · 变更 1 项
        Assert.Equal("ai.turn.worked", vm.Turns[1].HeaderValue.Key);          // 无变更 → 不带变更段
        Assert.Equal("ai.rail.tip", vm.Turns[0].RailTipValue.Key);            // 悬停提示：第 N 轮 + 时间 + 首条消息
        Assert.Equal("第一问", vm.Turns[0].PreviewText);
    }

    [Fact]
    public async Task 时间线_折叠该轮_条目隐藏_新条目同样隐藏()
    {
        var (vm, stub) = NewVm(TwoTurns());
        await vm.LoadAsync();

        vm.ToggleTurn(vm.Turns[0]);

        Assert.True(vm.Turns[0].IsCollapsed);
        Assert.True(vm.Feed.First(i => i.ItemId == "m-1").HiddenByTurn);
        Assert.True(vm.Feed.First(i => i.ItemId == "c-1").HiddenByTurn);
        Assert.False(vm.Feed.First(i => i.ItemId == "m-3").HiddenByTurn);   // 另一轮不受影响

        // 折叠期间该轮又来了新条目（工具状态变化）→ 同样隐藏
        stub.RaiseNotify(new AiNotification(AiNotificationKind.ToolCallChanged, "s-1", TurnId: "t-1",
            ToolCall: new AiToolCall("c-2", 7, "t-1", "links.update", AiToolCallState.Running, "{}", "{}",
                null, null, 0, false, null, null, DateTimeOffset.UtcNow)));
        Assert.True(vm.Feed.First(i => i.ItemId == "c-2").HiddenByTurn);

        vm.ToggleTurn(vm.Turns[0]);
        Assert.All(vm.Feed.Where(i => i.TurnId == "t-1"), i => Assert.False(i.HiddenByTurn));
    }

    [Fact]
    public async Task 时间线_新轮第一条消息_先补分隔行再排条目()
    {
        var (vm, stub) = NewVm(TwoTurns());
        await vm.LoadAsync();
        var before = vm.Feed.Count;

        stub.RaiseNotify(new AiNotification(AiNotificationKind.MessageAdded, "s-1", TurnId: "t-3",
            Message: new AiMessage("m-5", 7, AiRole.User, "第三问", DateTimeOffset.UtcNow, "t-3")));

        Assert.Equal(before + 2, vm.Feed.Count);   // 分隔行 + 用户消息
        Assert.Equal(AiFeedItem.ItemKind.TurnHeader, vm.Feed[^2].Kind);
        Assert.Equal("第三问", vm.Turns[^1].PreviewText);
        Assert.Equal(3, vm.Turns.Count);
    }

    [Fact]
    public async Task 会话行_运行状态与变更徽标()
    {
        var running = new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 4, 3, AiTurnState.ToolRunning);
        var (vm, stub) = NewVm();
        stub.Sessions.Clear();
        stub.Sessions.Add(running);

        await vm.LoadAsync();

        Assert.True(vm.Sessions[0].IsRunning);
        Assert.True(vm.Sessions[0].HasChanges);
        Assert.Equal("count.itemsN", vm.Sessions[0].ChangeCountValue.Key);
        Assert.Contains("2026", vm.Sessions[0].TimeValue.Resolve());   // 时间走时钟投影（本地格式）
    }

    [Fact]
    public async Task 模型下拉_列已启用模型_选择写偏好()
    {
        var (vm, stub) = NewVm();
        stub.Preferences = new AiPreferences("gw", "m1", AiMode.ConfirmEach);
        stub.Providers.Add(new AiProviderInfo("gw", "网关", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1", AiProviderStatus.Verified, null, null, null,
            [
                new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: true, null, null, true, true),
                new AiModelInfo("m2", "m2", AiModelSource.Manual, Enabled: true, null, null, true, true),
                new AiModelInfo("m3", "m3", AiModelSource.Manual, Enabled: false, null, null, true, true),
            ]));

        await vm.LoadAsync();
        StaPump.PumpFor(20);

        Assert.Equal(new[] { "gw/m1", "gw/m2" }, vm.Models.Select(m => m.Label));   // 停用的不进清单
        Assert.Equal("gw/m1", vm.SelectedModel?.Label);
        Assert.Equal("gw/m1", vm.ModelLabelValue.Resolve());

        vm.SelectedModel = vm.Models[1];
        StaPump.PumpFor(20);

        Assert.Equal("m2", Assert.Single(stub.SavePreferencesCalls).ModelId);
    }

    [Fact]
    public async Task 用量环_有读数按比例_没读数空环()
    {
        var (vm, stub) = NewVm();
        stub.SessionUsage = new AiSessionUsage(3, 5, 120, 60, ContextTokens: 8000, ContextWindowTokens: 32000);

        await vm.LoadAsync();
        StaPump.PumpFor(20);

        Assert.True(vm.HasContextUsage);
        Assert.Equal(25, vm.ContextUsagePercent, 1);
        Assert.Equal("ai.usage.context", vm.UsageTipValue.Key);

        stub.SessionUsage = new AiSessionUsage(0, 0, 0, 0);
        await vm.OpenSessionAsync("s-1");
        StaPump.PumpFor(20);

        Assert.False(vm.HasContextUsage);
        Assert.Equal(0, vm.ContextUsagePercent);
        Assert.Equal("ai.usage.context.none", vm.UsageTipValue.Key);
    }

    [Fact]
    public void 右栏_可收起_开关提示跟随()
    {
        var (vm, _) = NewVm();

        Assert.False(vm.IsPanelCollapsed);
        Assert.Equal("ai.panel.collapse", vm.PanelToggleKey);

        vm.TogglePanel();

        Assert.True(vm.IsPanelCollapsed);
        Assert.Equal("ai.panel.expand", vm.PanelToggleKey);
    }

    [Fact]
    public void 助手消息_Markdown面_收尾整篇跟上()
    {
        var item = AiFeedItem.ForAssistant(new AiMessage("m-1", 1, AiRole.Assistant, "",
            DateTimeOffset.UtcNow, "t-1", IsStreaming: true));

        item.AppendDelta("## 标题");
        item.AppendDelta(" 与正文");

        Assert.Equal("## 标题 与正文", item.Text);   // 原文（复制 / 存为技能用）始终完整
        item.Finalize(new AiMessage("m-1", 1, AiRole.Assistant, "## 标题 与正文", DateTimeOffset.UtcNow, "t-1"));
        Assert.Equal("## 标题 与正文", item.RenderText);
        Assert.False(item.IsStreaming);
    }

    [Fact]
    public void 工具行_展开收起_图标跟随命令域()
    {
        var item = AiFeedItem.ForTool(new AiToolCall("c-1", 1, "t-1", "links.move_batch",
            AiToolCallState.Completed, "{\"ids\":[]}", "{\"ids\":[]}", "2 change(s)", null, 8, false,
            null, null, DateTimeOffset.UtcNow));

        Assert.Equal("link-variant", item.IconKind);
        Assert.True(item.HasArgs);
        Assert.False(item.IsExpanded);

        item.ToggleExpand();

        Assert.True(item.IsExpanded);
    }
}

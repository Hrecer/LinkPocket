using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 回合折叠与用量的**显示回归网**（2026-09-26 用户实测的三个显示 bug）：
/// ① 一轮结束必须**当场收起**工作段（不是"状态收了、画面还摊着"，逼用户点两下）；
/// ② 折叠上一轮**不许吞掉下一轮刚发的用户消息**；
/// ③ 草稿提升为正式会话后，用量读数**不再被草稿闸门误清**（面板/环要有读数）。
/// </summary>
public class AiTurnVisibilityTests
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

    /// <summary>两轮（都已完成）：t-1 有用户消息 / 助手正文 / 一条工具调用；t-2 只有两条消息。</summary>
    private static AiSessionDetail TwoTurns()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-3);
        var summary = new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1", start, start, 4, 1, null);
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
        return new AiSessionDetail(summary, messages, calls, [], [], turns);
    }

    [Fact]
    public async Task 轮次结束_工作段当场收起_不用手动点两次()
    {
        var (vm, stub) = NewVm(TwoTurns());
        await vm.LoadAsync();
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);

        // 新一轮开始：用户消息 + 一条运行中的工具调用（运行中 = 锁死展开，工作段可见）
        stub.RaiseNotify(new AiNotification(AiNotificationKind.MessageAdded, "s-1", TurnId: "t-3",
            Message: new AiMessage("m-5", 7, AiRole.User, "第三问", started, "t-3")));
        stub.RaiseNotify(new AiNotification(AiNotificationKind.ToolCallChanged, "s-1", TurnId: "t-3",
            ToolCall: new AiToolCall("c-2", 8, "t-3", "folders.create", AiToolCallState.Running, "{}", "{}",
                null, null, 0, false, null, null, started)));
        var header = vm.Turns[^1];
        var tool = vm.Feed.First(i => i.ItemId == "c-2");
        Assert.True(header.IsLockedOpen);
        Assert.False(header.IsCollapsed);
        Assert.False(tool.HiddenByTurn);

        // 一轮结束：分隔行收起，工作条目必须**当场跟着隐藏**——
        // 缺这一同步时画面还摊着，第一下点击执行的其实是"展开"（毫无反应），第二下才真合。
        stub.RaiseNotify(new AiNotification(AiNotificationKind.TurnChanged, "s-1", TurnId: "t-3",
            Turn: new AiTurn("t-3", 3, AiTurnState.Completed, started, DateTimeOffset.UtcNow, null, 1, 0, 1, false)));

        Assert.False(header.IsLockedOpen);
        Assert.True(header.IsCollapsed);
        Assert.True(tool.HiddenByTurn);                                    // ← 回归核心
        Assert.False(vm.Feed.First(i => i.ItemId == "m-5").HiddenByTurn);  // 用户消息不受折叠影响
    }

    [Fact]
    public async Task 折叠上一轮_不吞下一轮的用户消息()
    {
        var (vm, _) = NewVm(TwoTurns());
        await vm.LoadAsync();

        var first = vm.Turns[0];                                   // t-1
        var secondUser = vm.Feed.First(i => i.ItemId == "m-3");    // 第二轮"第二问"= 上一轮之后发的那句话
        vm.ToggleTurn(first);   // 展开
        vm.ToggleTurn(first);   // 收起

        Assert.True(first.IsCollapsed);
        Assert.True(vm.Feed.First(i => i.ItemId == "c-1").HiddenByTurn);    // 本轮工作照常收起
        Assert.False(vm.Feed.First(i => i.ItemId == "m-1").HiddenByTurn);   // 本轮用户消息常显
        Assert.False(vm.Feed.First(i => i.ItemId == "m-2").HiddenByTurn);   // 助手正文常显（永远不收）
        Assert.False(secondUser.HiddenByTurn);                              // ← 回归核心：下一轮的用户消息不被一起吞
    }

    [Fact]
    public async Task 草稿提升为正式会话_用量读数不再被误清()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();

        await vm.NewSessionAsync();          // 新建 = 草稿：还没被用起来，读数清空（不编造"已用这么多"）
        StaPump.PumpFor(20);
        Assert.False(vm.HasContextUsage);

        stub.SessionUsage = new AiSessionUsage(1, 2, 300, 80, ContextTokens: 8000, ContextWindowTokens: 32000);
        vm.ComposerText = "第一句话";
        await vm.SendAsync();                // 首发即提升为正式会话
        Assert.Equal("s-draft1", Assert.Single(stub.SendCalls).SessionId);

        // 回合收尾（真实链路：TurnChanged → OnTurnSettled → RefreshUsageAsync）
        stub.RaiseNotify(new AiNotification(AiNotificationKind.TurnChanged, "s-draft1", TurnId: "t-1",
            Turn: new AiTurn("t-1", 1, AiTurnState.Completed, DateTimeOffset.UtcNow.AddSeconds(-2),
                DateTimeOffset.UtcNow, null, 0, 0, 1, false)));
        StaPump.PumpFor(20);

        Assert.True(vm.HasContextUsage);     // ← 回归核心：修前每次刷新都被判成"还是草稿"而清空
        Assert.Equal(25, vm.ContextUsagePercent, 1);
    }
}

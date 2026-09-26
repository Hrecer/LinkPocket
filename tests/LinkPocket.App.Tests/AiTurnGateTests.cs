using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 单回合门禁与串台回归网（2026-09-26 用户现场）：
/// ① 旧会话在跑时，它的流式增量/消息**不许**串进新建的草稿（此前只在部分通知分支有会话过滤）；
/// ② 回合在跑时**不允许发送**（按钮灰 + 回车路径如实拒绝、输入保留），**绝不自动停别人的回合**；
/// ③ "停止"按钮取消的是**正在跑的那个回合**（它常不属于当前会话）——传 null。
/// </summary>
public class AiTurnGateTests
{
    private static (AiViewModel Vm, StubAiAssistant Stub) NewVm()
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(new AiModelSelection("gw", "m1"), null, "gw", "m1"),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话A", AiMode.ConfirmEach, "gw", "m1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        return (new AiViewModel(stub), stub);
    }

    private static string? LastNoticeKey(AiViewModel vm)
        => vm.Feed.LastOrDefault(i => i.Kind == AiFeedItem.ItemKind.Notice)?.NoticeValue.Key;

    [Fact]
    public async Task 旧会话在跑_其流式增量不串进新草稿()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();

        // 会话 A 的回合在跑（流式）
        stub.TurnRunning = true;
        stub.RaiseNotify(new AiNotification(AiNotificationKind.TurnChanged, "s-1", TurnId: "t-1",
            Turn: new AiTurn("t-1", 1, AiTurnState.Streaming, DateTimeOffset.UtcNow, null, null, 0, 0, 1, false)));
        StaPump.PumpFor(20);
        Assert.True(vm.IsTurnRunning);

        await vm.NewSessionAsync();   // 切到新草稿（对话区清空）
        StaPump.PumpFor(20);
        var before = vm.Feed.Count;

        // A 的流式增量与消息到达（旧会话不属于当前视图）
        stub.RaiseNotify(new AiNotification(AiNotificationKind.StreamDelta, "s-1",
            MessageId: "m-old", TextDelta: "旧会话正在输出的内容"));
        stub.RaiseNotify(new AiNotification(AiNotificationKind.MessageAdded, "s-1",
            Message: new AiMessage("m-old", 9, AiRole.Assistant, "旧会话内容", DateTimeOffset.UtcNow, "t-1")));
        StaPump.PumpFor(20);

        Assert.Equal(before, vm.Feed.Count);
        Assert.DoesNotContain(vm.Feed, i => i.ItemId == "m-old");
    }

    [Fact]
    public async Task 回合在跑_不允许发送_输入保留且如实提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.TurnRunning = true;
        stub.RaiseNotify(new AiNotification(AiNotificationKind.TurnChanged, "s-1", TurnId: "t-1",
            Turn: new AiTurn("t-1", 1, AiTurnState.Streaming, DateTimeOffset.UtcNow, null, null, 0, 0, 1, false)));
        StaPump.PumpFor(20);

        vm.ComposerText = "新会话的第一句";
        Assert.False(vm.CanSend);                 // 按钮置灰（视图按它禁用）
        await vm.SendAsync();

        Assert.Empty(stub.SendCalls);             // 没有发出去（也没有自动停别人的回合）
        Assert.Empty(stub.CancelCalls);
        Assert.Equal("新会话的第一句", vm.ComposerText);   // 输入保留
        Assert.Equal("ai.send.turnBusy", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 停止按钮_取消任意在跑的回合()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.TurnRunning = true;   // 回合在跑（模拟：它属于别的会话）
        await vm.StopAsync();
        Assert.Contains(null, stub.CancelCalls);   // 传 null = 取消任意在跑的那个
    }
}

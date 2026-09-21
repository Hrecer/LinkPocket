using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Services;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// UiEventHub 金标准（行为契约）：后端数据变更抵达界面的唯一 300ms 防抖通道。
/// 断言口径 = 可观测结果（RefreshRequested 触发次数与时机），用真实 Dispatcher 泵走真实计时器。
/// </summary>
public class UiEventHubTests
{
    private static DomainEvent Evt()
        => new("links.changed", DateTimeOffset.Now, null, "app-tests", CallerRef.Ui);

    [Fact]
    public Task 窗口内多次事件被合并_尾沿触发恰好一次刷新() => StaPump.RunAsync(async () =>
    {
        var bus = new InMemoryEventBus();
        var hub = new UiEventHub();
        hub.Attach(bus);

        var fires = 0;
        hub.RefreshRequested += () => fires++;

        // 三次连发，间隔 60ms &lt; 300ms 防抖窗口 → 计时不断重启（尾沿触发）
        for (var i = 0; i < 3; i++)
        {
            await bus.PublishAsync(Evt());
            await Task.Delay(60);
        }

        // 距最后一次事件 150ms &lt; 300ms：窗口未走完，不触发
        await Task.Delay(150);
        Assert.Equal(0, fires);

        // 越过 300ms 尾沿：恰好在一次（合并，不是随事件各来一次）
        await Task.Delay(350);
        Assert.Equal(1, fires);
    });

    [Fact]
    public Task 触发完成后的下一波事件_重新走完整防抖窗口() => StaPump.RunAsync(async () =>
    {
        var bus = new InMemoryEventBus();
        var hub = new UiEventHub();
        hub.Attach(bus);

        var fires = 0;
        hub.RefreshRequested += () => fires++;

        // 第一波：单事件 → 窗口走完触发一次
        await bus.PublishAsync(Evt());
        await Task.Delay(150);
        Assert.Equal(0, fires);
        await Task.Delay(250);                 // 总计已过 400ms > 300ms
        Assert.Equal(1, fires);

        // 第二波：两事件紧挨着（间隔 ~0，仍在一个窗口内）→ 再次合并为一次
        await bus.PublishAsync(Evt());
        await bus.PublishAsync(Evt());
        await Task.Delay(700);
        Assert.Equal(2, fires);
    });

    [Fact]
    public Task 未接入事件源时_永不触发刷新() => StaPump.RunAsync(async () =>
    {
        var hub = new UiEventHub();   // 未 Attach 任何事件总线
        var fires = 0;
        hub.RefreshRequested += () => fires++;

        await Task.Delay(400);
        Assert.Equal(0, fires);
    });
}
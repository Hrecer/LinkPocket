using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 外部进程写入 → Shell 的**精确提示**（行为契约 §1.5.1）：变更流把精确变更交给枢纽后，
/// Shell 要做两件可观测的事——① 观测面留痕一行；② 在**浏览页**触发一次共享通知栈提示。
///
/// <para>为什么只在浏览页触发：那是书签数据的所在地，也是提示的语义落点；其它页面不打断用户
/// （它们各自在切入时做入口对齐刷新）。提示的视觉与计时由 Shell 的 <c>NoticeBar</c> 自持，
/// 这里只断言 VM 在没有 UI 端口时暴露的兜底状态。</para>
/// </summary>
public class ExternalChangeNoticeTests
{
    [Fact]
    public Task 外部变更在浏览页给一次提示条() => StaPump.RunAsync(async () =>
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var hub = new UiEventHub();
            var vm = new MainViewModel(client, hub, new UiPortProvider());

            Assert.False(vm.BrowserViewModel.IsNoticeOpen);

            hub.NotifyExternalChanges([Change("links.move_batch"), Change("folders.create")]);

            Assert.True(vm.BrowserViewModel.IsNoticeOpen);
            Assert.Equal(Loc.K("browser.status.externalChange", 2).Resolve(), vm.BrowserViewModel.NoticeText);

            // 提示的计时/动画由 Shell 的 NoticeBar 自持；这里的 UiPortProvider 没有 UI 端口，
            // 只断言 VM 暴露的兜底状态（有端口时文案直接进共享通知栈）。
            hub.NotifyExternalChanges([]);
            await Task.Yield();
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    });

    [Fact]
    public Task 拿不到精确变更时不提示_只刷新() => StaPump.RunAsync(async () =>
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var hub = new UiEventHub();
            var vm = new MainViewModel(client, hub, new UiPortProvider());

            hub.NotifyExternalChange();   // 退化口径：探针报了提交但变更流没有外部行

            Assert.False(vm.BrowserViewModel.IsNoticeOpen);   // 没有精确信息 → 不编一条提示
            await Task.Yield();
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    });

    private static ExternalChange Change(string command) => new(
        Id: 1, At: DateTimeOffset.Now, Command: command, Caller: CallerRef.ExternalAgent.ToString(),
        Success: true, DryRun: false, IsNested: false, BatchId: null,
        Touched: [new EntityRef("link", "L1")], Events: ["links.changed"], HumanSummary: "Moved 1 link(s)");
}
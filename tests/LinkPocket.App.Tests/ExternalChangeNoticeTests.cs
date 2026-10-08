using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 外部进程写入 → Shell 的**精确提示**（行为契约 §1.5.1）：变更流把精确变更交给枢纽后，
/// Shell 要做两件可观测的事——① 观测面留痕一行；② 在**浏览页**给一次 MD3 提示条。
///
/// <para>为什么只在浏览页：那是书签数据的所在地，也是唯一承载提示条的页面；其它页面不打断用户
/// （它们各自在切入时做入口对齐刷新）。默认页就是浏览页，所以用缺省构造即可覆盖。</para>
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

            // 提示条数秒后由**视图**计时器收起（VM 不引 Dispatcher，保持可单测）——此处只断言 VM 暴露的那一半
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
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LinkPocket.App.Tests;

/// <summary>
/// WPF 单测的 STA + Dispatcher 泵：xUnit 测试线程默认为 MTA 线程池，无法创建
/// DispatcherTimer / 调用 CommandManager。本工具在专用 STA 线程上起一个运行中的
/// Dispatcher，把测试体排入后泵起来，直至测试体完成再关泵。
/// </summary>
internal static class StaPump
{
    public static Task RunAsync(Func<Task> body)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "lp-app-tests-sta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// 泵消息 N 毫秒。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只 <c>UpdateLayout()</c></b>：<c>FrameworkElement.LayoutUpdated</c> 由布局管理器在
    /// <b>一次布局回合结束时经 Dispatcher 排入</b>，而直接调 <c>UpdateLayout()</c> 只跑完测量与排列、
    /// 从不排那个通知 —— 于是"依赖布局后通知"的行为（<c>LocFit</c> 就在那条路径上）在测试里
    /// <b>一次都不会被执行</b>，而断言会以"什么都没发生"的形式静默通过或假红。
    /// 真实窗口有消息循环，所以这个坑只在测试里出现（见 <c>WARNINGS</c> 的"渲染检查静默空跑"同族教训）。
    /// </remarks>
    public static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() => frame.Continue = false), DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
}
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
}
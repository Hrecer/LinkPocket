using System;
using System.Windows.Threading;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// UI 事件枢纽（事件流终点）：后端数据变更抵达界面的**唯一防抖通道**。
/// - 事件源 = 新引擎领域事件总线（<see cref="IEventBus"/>）：UI 切换完成后经
///   <c>Hub.Attach(engine.Events)</c> 接入；ChangeSet 增量投递 + 300ms 防抖刷新由此生效。
/// - 防抖：任一事件 → 300ms 计时重启（尾沿触发），期间再来事件只顺延——
///   与原 MainViewModel 内联定时器逐字节等价；触发 <see cref="RefreshRequested"/> 一次。
/// - 枢纽不认识页面：刷什么由订阅方（各页 VM / Shell）按当前活跃视图自行决定。
/// - ⚠️ 不变量（数据闸纪律）：事件在持闸期间同步抵达，本枢纽只重启计时器（异步路径），
///   <see cref="RefreshRequested"/> 处理器内**不得**同步回派命令（会自锁）。
/// - 线程模型：进程内引擎在 UI 线程同步推送 → 计时器创建/启停落在 UI 线程；
///   未来若接入跨线程源，须先在 Attach 处经 Dispatcher 封装。
/// </summary>
public sealed class UiEventHub
{
    /// <summary>防抖窗口（行为等价：300ms）。</summary>
    public const int DebounceMilliseconds = 300;

    private DispatcherTimer? _timer;

    /// <summary>防抖后的刷新通知（UI 线程触发）。</summary>
    public event Action? RefreshRequested;

    /// <summary>接入引擎事件源（领域事件总线；枢纽只关心"有变更"，不消费事件负载）。</summary>
    public void Attach(IEventBus bus)
        => bus.Subscribe(_ => OnEvent());

    private void OnEvent()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                var handlers = RefreshRequested;
                if (handlers == null) return;

                // 逐个隔离：一个订阅方抛异常不得让其余页面的刷新一起失效
                //（否则症状是"某次数据变更之后，某一页再也不刷新"，且现场没有任何痕迹）
                foreach (var handler in handlers.GetInvocationList())
                {
                    try { ((Action)handler)(); }
                    catch (Exception ex) { LpLog.Error("a refresh handler failed (the other pages continue)", ex); }
                }
            };
        }

        _timer.Stop();
        _timer.Start();
    }
}

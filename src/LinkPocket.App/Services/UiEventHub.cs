using System;
using System.Windows.Threading;
using LinkPocket.Api;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// UI 事件枢纽（方案 2.3 事件流终点 / 阶段 8 定稿）：后端数据变更抵达界面的**唯一防抖通道**。
/// - 双源汇入：旧协议链（<see cref="ILinkPocketTransport.EventReceived"/>）与新引擎
///   （<see cref="IEventBus"/>，阶段 9 逐页切换后接入）都汇入同一条流；
///   过渡期两源并存时，防抖合并语义天然去重（同批变更只刷一次）。
/// - 防抖：任一事件 → 300ms 计时重启（尾沿触发），期间再来事件只顺延——
///   与原 MainViewModel 内联定时器逐字节等价；触发 <see cref="RefreshRequested"/> 一次。
/// - 枢纽不认识页面：刷什么由订阅方（各页 VM / Shell）按当前活跃视图自行决定。
/// - ⚠️ 不变量（数据闸纪律）：事件在持闸期间同步抵达，本枢纽只重启计时器（异步路径），
///   <see cref="RefreshRequested"/> 处理器内**不得**同步回派协议命令（会自锁）。
/// - 线程模型：现有事件源都在 UI 线程同步触发（进程内转发）→ 计时器创建/启停落在 UI 线程；
///   未来若接入跨线程源，须先在 Attach 处经 Dispatcher 封装。
/// </summary>
public sealed class UiEventHub
{
    /// <summary>防抖窗口（行为等价定稿：300ms）。</summary>
    public const int DebounceMilliseconds = 300;

    private DispatcherTimer? _timer;

    /// <summary>防抖后的刷新通知（UI 线程触发）。</summary>
    public event Action? RefreshRequested;

    /// <summary>接入旧协议链事件源（传输层 EventReceived，负载为 JSON 字符串）。</summary>
    public void Attach(ILinkPocketTransport transport)
        => transport.EventReceived += OnLegacyEvent;

    /// <summary>接入新引擎事件源（领域事件总线；枢纽只关心"有变更"，不消费事件负载）。</summary>
    public void Attach(IEventBus bus)
        => bus.Subscribe(_ => OnEvent());

    private void OnLegacyEvent(object? sender, string e) => OnEvent();

    private void OnEvent()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                RefreshRequested?.Invoke();
            };
        }

        _timer.Stop();
        _timer.Start();
    }
}

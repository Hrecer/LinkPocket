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

    /// <summary>
    /// 防抖窗口内聚合出的**精确变更批次**（在 <see cref="RefreshRequested"/> 之前触发）。
    /// <para><see cref="ChangeBatch.Precise"/> = false 表示拿不到“改了哪些实体/什么事件”，
    /// 订阅方必须退化为整体刷新；精确是优化，正确性不依赖它。</para>
    /// </summary>
    public event Action<ChangeBatch>? ChangeBatchReady;

    private readonly HashSet<EntityRef> _pendingTouched = new();
    private readonly HashSet<string> _pendingEvents = new(StringComparer.Ordinal);
    private bool _pendingPrecise = true;
    private bool _hasPending;

    /// <summary>防抖后的刷新通知（UI 线程触发）。</summary>
    public event Action? RefreshRequested;

    /// <summary>
    /// **外部进程**写入的精确变更（<see cref="NotifyExternalChanges"/> 时立即触发，不等防抖）：
    /// 订阅方据此做"到底改了什么"的精确反应（例如给一次全站共享的通知栈提示）。
    /// ⚠️ 与 <see cref="RefreshRequested"/> 同一条纪律：处理器内不得同步回派命令（会自锁）。
    /// </summary>
    public event Action<IReadOnlyList<ExternalChange>>? ExternalChangesObserved;

    /// <summary>接入引擎事件源：解析事件负载里的精确变更（Touched/Events）并汇入防抖批次。</summary>
    public void Attach(IEventBus bus)
        => bus.Subscribe(e =>
        {
            var view = e.Data is { } data ? ChangeSetPayload.Parse(data.GetRawText()) : null;
            Merge(view?.Touched ?? [], view?.Events ?? [],
                precise: view is not null && view.Touched.Count > 0);
            OnEvent();
        });

    /// <summary>
    /// **外部进程**（CLI / 外部 Agent 经 MCP）写库后的通知入口：走**同一条** 300ms 防抖通道，
    /// 与进程内领域事件**完全同权**——刷什么、怎么刷由订阅方按活跃视图自行决定（见 BEHAVIOR-CONTRACT §1.5）。
    /// 由 <see cref="ExternalChangeWatcher"/> 轮询跨进程变更探针后调用（UI 线程）。
    /// </summary>
    public void NotifyExternalChange() => NotifyExternalChanges([]);

    /// <summary>
    /// 带**精确变更**的外部写入通知：先投递 <see cref="ExternalChangesObserved"/>（订阅方可立即做精确反应），
    /// 再汇入同一条防抖刷新通道。变更列表为空（拿不到精确信息）= 只刷新，与 <see cref="NotifyExternalChange"/> 等价。
    /// </summary>
    public void NotifyExternalChanges(IReadOnlyList<ExternalChange> changes)
    {
        // 先把精确变更并入防抖批次：外部变更与进程内事件共用同一条刷新通道。
        if (changes.Count == 0)
        {
            Merge([], [], precise: false);   // 探针只说明“有提交”，不知道改了什么
        }
        else
        {
            var effective = changes.Where(c => c.IsEffective).ToArray();
            var touched = effective.SelectMany(c => c.Touched).ToArray();
            var events = effective.SelectMany(c => c.Events)
                .Distinct(StringComparer.Ordinal).ToArray();
            Merge(touched, events, precise: touched.Length > 0);
        }

        if (changes.Count > 0 && ExternalChangesObserved is { } observers)
        {
            // 与刷新处理器同一隔离口径：一个订阅方抛异常不得让别的订阅方与刷新一起失效
            foreach (var observer in observers.GetInvocationList())
            {
                try { ((Action<IReadOnlyList<ExternalChange>>)observer)(changes); }
                catch (Exception ex) { LpLog.Error("an external-change observer failed", ex); }
            }
        }

        OnEvent();
    }

    private void OnEvent()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();

                // 批次先于刷新投递：页面订阅方据此做精确增量，未订阅者行为不变。
                var batch = new ChangeBatch(
                    [.. _pendingTouched],
                    [.. _pendingEvents],
                    _hasPending && _pendingPrecise);
                _pendingTouched.Clear();
                _pendingEvents.Clear();
                _pendingPrecise = true;
                _hasPending = false;

                if (ChangeBatchReady is { } batchHandlers)
                {
                    foreach (var handler in batchHandlers.GetInvocationList())
                    {
                        try { ((Action<ChangeBatch>)handler)(batch); }
                        catch (Exception ex) { LpLog.Error("a change-batch handler failed", ex); }
                    }
                }

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

    private void Merge(IReadOnlyList<EntityRef> touched, IReadOnlyList<string> events, bool precise)
    {
        foreach (var entity in touched) _pendingTouched.Add(entity);
        foreach (var name in events) _pendingEvents.Add(name);
        if (!precise) _pendingPrecise = false;
        _hasPending = true;
    }
}

/// <summary>
/// 一次防抖窗口内的变更批次：Touched/Events 来自进程内 ChangeSet 或外部审计变更流；
/// <paramref name="Precise"/> = false（拿不到具体实体）时必须整体刷新。
/// </summary>
public sealed record ChangeBatch(
    IReadOnlyList<EntityRef> Touched,
    IReadOnlyList<string> Events,
    bool Precise);

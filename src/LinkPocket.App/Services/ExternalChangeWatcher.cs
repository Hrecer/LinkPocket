using System;
using System.Windows.Threading;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 外部写入观察者：让界面"感知"**别的进程**（CLI / 外部 Agent 经 MCP）对同一个库的写入。
/// <para>为什么需要它：<see cref="UiEventHub"/> 的唯一事件源是**进程内**引擎领域事件总线；
/// 外部进程写库不会产生任何进程内事件 —— 症状是"用命令行改完数据，界面要手动刷新"。</para>
/// <para>做法：定时（活跃 1s / 失焦 5s）轮询 <see cref="LibraryChangeProbe"/>（<c>PRAGMA data_version</c>），
/// 一旦发现"别的连接提交过"就调用 <see cref="UiEventHub.NotifyExternalChange"/> —— 与进程内事件
/// **同权**地汇入同一条 300ms 防抖通道，因此**不新增任何刷新策略**（刷新仍按活跃视图路由）。</para>
/// <para>成本上界：1 条 pragma/秒（微秒级）+ 每次外部提交触发 1 次活跃页重查；
/// 一次 1.6 万条的事务批 = 1 个事务 = data_version 只跳一次 = 只刷一次。</para>
/// <para>失败口径（遵守"观测组件禁止自愈与静默兜底"）：轮询异常**如实记日志并停表**——
/// 探针是长连接，失败通常意味着连接已坏，继续轮询只会刷屏；界面本身不受影响（仍可手动刷新）。</para>
/// </summary>
public sealed class ExternalChangeWatcher : IDisposable
{
    /// <summary>窗口活跃时的轮询间隔。</summary>
    public const int ActiveIntervalMilliseconds = 1000;

    /// <summary>窗口失焦/最小化时的轮询间隔（省电；外部写入仍会被发现，只是晚一点）。</summary>
    public const int IdleIntervalMilliseconds = 5000;

    private readonly LibraryChangeProbe _probe;
    private readonly Action _invalidateQueryCache;
    private readonly UiEventHub _hub;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    /// <param name="databasePath">库文件路径（与引擎同一个库）。</param>
    /// <param name="invalidateQueryCache">先于刷新的**缓存失效**动作（`EngineClient.InvalidateQueryCache`）：
    /// 外部提交不产生进程内事件 → 事件驱动失效不触发 → 不先失效就只能读到旧值。</param>
    /// <param name="hub">目标枢纽（外部变更与进程内事件共用同一条防抖通道）。</param>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">库打不开（调用方在引擎装配之后构造，不应发生）。</exception>
    public ExternalChangeWatcher(string databasePath, Action invalidateQueryCache, UiEventHub hub)
    {
        _invalidateQueryCache = invalidateQueryCache ?? throw new ArgumentNullException(nameof(invalidateQueryCache));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _probe = new LibraryChangeProbe(databasePath);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ActiveIntervalMilliseconds) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>窗口活跃度切换（由 Shell 在 Activated/Deactivated 时调用）。</summary>
    public void SetActive(bool active)
        => _timer.Interval = TimeSpan.FromMilliseconds(active ? ActiveIntervalMilliseconds : IdleIntervalMilliseconds);

    /// <summary>开始观察（必须在 UI 线程调用——计时器要落在 Dispatcher 上）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Start();
    }

    /// <summary>轮询一次并（必要时）**先失效缓存、再通知枢纽**；测试可显式调用。</summary>
    public void Poll()
    {
        try
        {
            if (!_probe.HasChanged()) return;
            // 顺序不可颠倒：先失效缓存，再通知刷新 —— 否则刷新读到的仍是缓存旧值。
            _invalidateQueryCache();
            _hub.NotifyExternalChange();
        }
        catch (Exception ex)
        {
            _timer.Stop();   // 长连接坏了就不再轮询；界面仍可手动刷新
            LpLog.Error("external change probe failed (polling stopped; manual refresh still works)", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _probe.Dispose();
    }
}

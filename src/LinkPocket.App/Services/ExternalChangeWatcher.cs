using System;
using System.Linq;
using System.Windows.Threading;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 外部写入观察者：让界面"感知"**别的进程**（CLI / 外部 Agent 经 MCP）对同一个库的写入。
/// <para>为什么需要它：<see cref="UiEventHub"/> 的唯一事件源是**进程内**引擎领域事件总线；
/// 外部进程写库不会产生任何进程内事件 —— 症状是"用命令行改完数据，界面要手动刷新"。</para>
/// <para>做法：定时（活跃 1s / 失焦 5s）读一次**精确变更流**（<see cref="IChangeFeed"/>，附属库审计尾，
/// 一条 <c>MAX(id)</c> + 至多一次取行）：有外部行就按变更**实际发布的事件名**精确失效查询缓存，
/// 并把精确变更交给 <see cref="UiEventHub.NotifyExternalChanges"/>（与进程内事件**同权**地汇入同一条
/// 300ms 防抖通道，因此**不新增任何刷新策略**）；没有外部行再看 <see cref="LibraryChangeProbe"/>
/// （<c>PRAGMA data_version</c>）兜底——它说"别的连接提交过"就整体失效 + 通用刷新。</para>
/// <para>成本上界：1 条 pragma + 1 条 <c>MAX(id)</c> 每秒（都是微秒级）+ 每次外部提交触发 1 次活跃页重查；
/// 一次 1.6 万条的事务批 = 1 个事务 = 只刷一次。</para>
/// <para>**为什么变更流当主判据、探针当兜底**：只有变更流能回答"改了什么"（精确失效与精确提示的依据），
/// 但它的覆盖面限于"落了审计的外部身份写入"；探针覆盖面更宽（任何连接的任何提交，含本进程自己的写），
/// 却给不出内容。两者合起来 = 能精确就精确，不能精确就保守，**正确性不依赖精确**。</para>
/// <para>失败口径（遵守"观测组件禁止自愈与静默兜底"）：轮询异常**如实记日志并停表**——
/// 探针是长连接，失败通常意味着连接已坏，继续轮询只会刷屏；界面本身不受影响（仍可手动刷新）。</para>
/// </summary>
public sealed class ExternalChangeWatcher : IDisposable
{
    /// <summary>窗口活跃时的轮询间隔。</summary>
    public const int ActiveIntervalMilliseconds = 1000;

    /// <summary>窗口失焦/最小化时的轮询间隔（省电；外部写入仍会被发现，只是晚一点）。</summary>
    public const int IdleIntervalMilliseconds = 5000;

    /// <summary>单次轮询最多消费多少条外部变更（超出留到下一次；只影响提示条数，不影响正确性）。</summary>
    public const int MaxChangesPerPoll = 500;

    private readonly LibraryChangeProbe _probe;
    private readonly IChangeFeed? _changeFeed;
    private readonly Action<IReadOnlyList<string>> _invalidateQueryCache;
    private readonly UiEventHub _hub;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    /// <param name="databasePath">库文件路径（与引擎同一个库）。</param>
    /// <param name="changeFeed">精确变更流（附属库审计尾）；null = 无精确信息（一律整体失效 + 通用刷新）。</param>
    /// <param name="invalidateQueryCache">先于刷新的**缓存失效**动作（<c>EngineClient.InvalidateQueryCache</c>）：
    /// 外部提交不产生进程内事件 → 事件驱动失效不触发 → 不先失效就只能读到旧值。
    /// 参数 = 本次变更实际发布的事件名；空数组 = 整体清空（无法精确时的保守口径）。</param>
    /// <param name="hub">目标枢纽（外部变更与进程内事件共用同一条防抖通道）。</param>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">库打不开（调用方在引擎装配之后构造，不应发生）。</exception>
    public ExternalChangeWatcher(string databasePath, IChangeFeed? changeFeed,
        Action<IReadOnlyList<string>> invalidateQueryCache, UiEventHub hub)
    {
        _changeFeed = changeFeed;
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

    /// <summary>轮询一次并（必要时）**先精确失效缓存、再通知枢纽**；测试可显式调用。</summary>
    public void Poll()
    {
        try
        {
            // 变更流是**主判据**（每 tick 读一次审计尾：一条 MAX(id) + 至多一次取行，微秒级）。
            // 为什么不让探针当闸门：外部提交与"审计行落库"之间有一小段窗口（审计在建库提交之后写），
            // 探针恰好在窗口内跳一次，就会漏掉这条变更的**精确信息**（只剩保守失效）。
            var changes = _changeFeed?.Poll(MaxChangesPerPoll) ?? [];
            if (changes.Count > 0)
            {
                // 精确失效：只推进本次变更真正发布过的事件名（与进程内写同一条失效机制）。
                // 有一行"提交了数据却没报事件"（含嵌套子步骤）→ 精确不可信 → 整体失效（保守，绝不读到旧值）。
                var opaque = changes.Any(c => c.Success && !c.DryRun && c.Events.Count == 0);
                var events = opaque
                    ? []
                    : changes.SelectMany(c => c.Events).Distinct(StringComparer.Ordinal).ToArray();

                // 顺序不可颠倒：先失效缓存，再通知刷新 —— 否则刷新读到的仍是缓存旧值。
                _invalidateQueryCache(events);
                _hub.NotifyExternalChanges([.. changes.Where(c => c.IsEffective)]);
                return;
            }

            // 兜底：探针说"别的连接提交过"，但变更流里没有外部行（非外部身份的写 / 未落审计 / 流不可用）
            // → 拿不到精确信息，整体失效 + 通用刷新。与"本进程自己的写也会让 data_version 跳"同一口径（幂等）。
            if (!_probe.HasChanged()) return;
            _invalidateQueryCache([]);
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

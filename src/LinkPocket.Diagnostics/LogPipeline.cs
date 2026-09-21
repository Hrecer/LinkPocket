using System.Diagnostics;
using System.Threading.Channels;
using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// 日志管道（<see cref="ILogSink"/> 的**唯一实现**，也是 <see cref="LpLog"/> 装配的对象）：
/// 级别过滤 → 富化（序号 / 作用域）→ 脱敏 → 有界队列 → 后台泵 → fan-out 到下游落点。
///
/// 线程模型与保证：
/// - 调用方线程只做"判定 + 入队"（零 IO；不阻塞 UI，也不进引擎写闸关键路径）；
/// - 后台泵是唯一写落点的线程；落点异常逐点隔离（Failed + LastError），绝不回灌调用方；
/// - 队列满：普通记录**丢弃并计数**（Dropped）；error / fatal 走**直写旁路**（DirectWrites）保证崩溃现场落盘；
/// - <see cref="Flush"/> 等待队列排空后再刷落点（异常处理器 / 退出前使用）。
/// 观测面铁律：丢弃与失败**全部计数**、不静默、不自愈。
/// </summary>
public sealed class LogPipeline : ILogSink, ILogFileMaintenance, ILogQuerySource, IDisposable
{
    private readonly LoggingOptions _options;
    private readonly ILogSink[] _sinks;
    private readonly Channel<LogRecord> _queue;
    private readonly Task _pump;

    private long _sequence;
    private long _accepted;
    private long _filtered;
    private long _dropped;
    private long _written;
    private long _failed;
    private long _direct;
    private long _pending;
    private string? _lastError;
    private int _level;          // 运行期最低级别（logs.level 可改；初值 = 装配时的 options.MinimumLevel）
    private int _disposed;

    public LogPipeline(LoggingOptions options, params ILogSink[] sinks)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (sinks is null || sinks.Length == 0)
            throw new ArgumentException("at least one log sink is required", nameof(sinks));
        _sinks = sinks;
        _level = (int)options.MinimumLevel;
        _queue = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>装配快照（只读）。⚠️ **运行期级别不在这里**：装配后改的是 <see cref="Level"/>，
    /// 本属性只记录装配那一刻的口径（避免"读 Options 拿到过期级别"的误判）。</summary>
    public LoggingOptions Options => _options;

    /// <summary>当前生效的最低记录级别（运行期可经 <see cref="SetLevel"/> 切换；越界无意义——枚举即阈值）。</summary>
    public LogLevel Level => (LogLevel)Volatile.Read(ref _level);

    /// <summary>切换最低记录级别（进程内生效，不落库、不重启）；返回切换前的级别（调用方据此如实回报）。</summary>
    public LogLevel SetLevel(LogLevel level) => (LogLevel)Interlocked.Exchange(ref _level, (int)level);

    public bool IsEnabled(LogLevel level) => level >= Level;

    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!IsEnabled(record.Level))
        {
            Interlocked.Increment(ref _filtered);
            return;
        }

        Interlocked.Increment(ref _accepted);
        var prepared = record with { Sequence = Interlocked.Increment(ref _sequence) };
        prepared = LogRedactor.Redact(prepared, _options.MaxMessageLength, _options.Redact);

        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(prepared)) return;
        Interlocked.Decrement(ref _pending);

        Interlocked.Increment(ref _dropped);
        if (prepared.Level >= LogLevel.Error)
        {
            Interlocked.Increment(ref _direct);   // 队列满也不丢 error/fatal：同步直写（异常处理器路径）
            Dispatch(prepared);
        }
    }

    /// <summary>等队列排空（最多 <paramref name="timeout"/>）后逐落点刷盘。</summary>
    public void Flush(TimeSpan timeout)
    {
        WaitForPump(timeout);

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Flush(timeout);
            }
            catch (Exception ex)
            {
                RecordFailure(sink, ex);
            }
        }
    }

    /// <summary>
    /// 日志查询（<c>logs.query</c> 的**唯一实现**）：先等后台泵把已入队记录交给落点（否则"刚写的日志查不到"
    /// 会被读成丢数据），再按来源取——memory = 内存环（游标 / 级别 / 分类过滤）；file = 从最新 JSONL 文件
    /// 向前回读（**先刷盘**，否则文件内容落后于刚写的行）。
    /// <para><b>两源不合并</b>：seq 是进程内单调量，文件里的 seq 来自**另一次进程运行**，跨进程不可比——
    /// 与其做"看起来统一"的模糊合并（去重与排序口径只能靠猜），不如让调用方显式选来源。</para>
    /// </summary>
    public LogQueryResult Query(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Max(1, query.Limit);

        if (query.Source == LogSource.File)
        {
            Flush(TimeSpan.FromMilliseconds(500));   // 文件源：排空 + 刷盘，读到的是真的落了盘的内容
            var reader = _sinks.OfType<ILogFileReader>().FirstOrDefault();
            if (reader is null)
                return new LogQueryResult([], null, limit, Level, LogSource.File);

            var tail = reader.ReadTail(limit);
            var matched = tail.Items.Where(r => Matches(r, query)).Take(limit).ToArray();
            return new LogQueryResult(matched, null, limit, Level, LogSource.File,
                FilesRead: tail.FilesRead, SkippedLines: tail.Skipped, MoreAvailable: tail.MoreAvailable);
        }

        WaitForPump(TimeSpan.FromMilliseconds(500));   // 内存源：只等泵交付，不做 fsync
        var ring = _sinks.OfType<ILogRingSource>().FirstOrDefault();
        if (ring is null)
            return new LogQueryResult([], query.Cursor, limit, Level, LogSource.Memory);

        var snapshot = ring.Snapshot(query.Cursor, 0);
        var items = snapshot.Where(r => Matches(r, query)).Take(limit).ToArray();
        return new LogQueryResult(items, snapshot.Count == 0 ? query.Cursor : snapshot[^1].Sequence,
            limit, Level, LogSource.Memory);
    }

    /// <summary>过滤口径（唯一实现）：级别 + 分类 + 关联（corr 取记录首类字段，由调用上下文自动填）。
    /// 游标由来源各自处理——它只对内存源有意义（见 <see cref="Query"/>）。</summary>
    private static bool Matches(LogRecord record, LogQuery query)
    {
        if (query.MinimumLevel is { } minimum && record.Level < minimum) return false;
        if (!string.IsNullOrEmpty(query.Category)
            && !string.Equals(record.Category, query.Category, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(query.CorrelationId)
            && !string.Equals(record.CorrelationId, query.CorrelationId, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>等后台泵把已入队记录交付落点（最多 <paramref name="timeout"/>；不刷落点自身缓冲）。</summary>
    private void WaitForPump(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (Interlocked.Read(ref _pending) > 0 && watch.Elapsed < timeout)
            Thread.Sleep(1);
    }

    /// <summary>管道计数 + 下游落点读数的聚合快照（目录 / LastError 取首个非空）。</summary>
    public LogStats Stats
    {
        get
        {
            var downstream = _sinks.Select(s => s.Stats).ToArray();
            return new LogStats(
                Accepted: Interlocked.Read(ref _accepted),
                Filtered: Interlocked.Read(ref _filtered),
                Dropped: Interlocked.Read(ref _dropped),
                Written: Interlocked.Read(ref _written),
                Failed: Interlocked.Read(ref _failed),
                DirectWrites: Interlocked.Read(ref _direct),
                QueueDepth: _queue.Reader.Count,
                Level: Level,
                Directory: downstream.Select(s => s.Directory).FirstOrDefault(d => d is not null),
                LastError: _lastError ?? downstream.Select(s => s.LastError).FirstOrDefault(e => e is not null));
        }
    }

    /// <summary>日志文件清单（委托给具备维护能力的落点；无 = 空）。</summary>
    public IReadOnlyList<string> Files => FileMaintenance?.Files ?? [];

    /// <summary>清空日志文件（委托给具备维护能力的落点；无 = 0）。</summary>
    public int ClearFiles() => FileMaintenance?.ClearFiles() ?? 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queue.Writer.TryComplete();
        try { _pump.Wait(TimeSpan.FromSeconds(3)); }
        catch { /* 退出路径：尽力排空；失败已由泵内 RecordFailure 暴露 */ }

        foreach (var sink in _sinks)
        {
            if (sink is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch { /* 落点自身处置失败：不阻断其余落点 */ }
        }
    }

    private ILogFileMaintenance? FileMaintenance => _sinks.OfType<ILogFileMaintenance>().FirstOrDefault();

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync())
                Dispatch(record);
        }
        catch (Exception ex)
        {
            RecordFailure(null, ex);   // 泵异常：暴露计数，不崩进程（队列随 TryComplete 结束）
        }
    }

    private void Dispatch(LogRecord record)
    {
        try
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    sink.Write(record);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception ex)
                {
                    RecordFailure(sink, ex);   // 单落点失败不影响其余落点与调用方
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private void RecordFailure(ILogSink? sink, Exception ex)
    {
        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastError, $"{sink?.GetType().Name ?? "pump"}: {ex.GetType().Name}: {ex.Message}");
    }
}

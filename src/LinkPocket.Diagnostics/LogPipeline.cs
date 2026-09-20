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
public sealed class LogPipeline : ILogSink, ILogFileMaintenance, IDisposable
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
    private int _disposed;

    public LogPipeline(LoggingOptions options, params ILogSink[] sinks)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (sinks is null || sinks.Length == 0)
            throw new ArgumentException("至少需要一个日志落点", nameof(sinks));
        _sinks = sinks;
        _queue = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>管道选项（只读快照；装配后不可变）。</summary>
    public LoggingOptions Options => _options;

    public bool IsEnabled(LogLevel level) => level >= _options.MinimumLevel;

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
        var watch = Stopwatch.StartNew();
        while (Interlocked.Read(ref _pending) > 0 && watch.Elapsed < timeout)
            Thread.Sleep(1);

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
                Level: _options.MinimumLevel,
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

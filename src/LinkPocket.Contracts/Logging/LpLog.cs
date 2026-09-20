using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LinkPocket.Contracts;

/// <summary>
/// 全站日志**唯一入口**（静态门面）：UI / 引擎 / 模块 / 宿主一律经它写日志。
/// 这是契约层唯一豁免的静态入口（见 ARCHITECTURE §2）：契约只定义"怎么记"，
/// 实现（过滤 / 有界队列 / 落点）在 <c>LinkPocket.Diagnostics.LogPipeline</c>，由组合根 <c>Configure</c> 装配一次。
///
/// 纪律（与观测面铁律一致）：
/// - <b>未装配管道不静默</b>：记录被丢弃但**计数**（<see cref="UnconfiguredDrops"/>）并给一次性 Trace 提示；
/// - 任何路径**绝不抛异常**（观测失败不得回灌业务调用方）；
/// - 结构化上下文用 <see cref="BeginScope"/>（AsyncLocal：异步链自动携带；内层字段覆盖外层同名）。
/// </summary>
public static class LpLog
{
    /// <summary>缺省来源分类（未显式指定 category 的调用点）。</summary>
    public const string DefaultCategory = "app";

    private sealed record ScopeFrame((string Key, object? Value)[] Fields, ScopeFrame? Parent);

    private static readonly AsyncLocal<ScopeFrame?> CurrentScope = new();
    private static readonly AsyncLocal<CallFrame?> CurrentCall = new();
    private static ILogSink? _sink;
    private static long _unconfiguredDrops;
    private static long _writeFailures;
    private static int _notified;

    /// <summary>当前管道（未装配 = null）。</summary>
    public static ILogSink? Sink => Volatile.Read(ref _sink);

    /// <summary>未装配管道期间被丢弃的记录数（不静默：诊断面据此暴露）。</summary>
    public static long UnconfiguredDrops => Interlocked.Read(ref _unconfiguredDrops);

    /// <summary>门面兜底捕获的写入失败数（落点违反"不抛"约定时的最后防线）。</summary>
    public static long WriteFailures => Interlocked.Read(ref _writeFailures);

    /// <summary>装配 / 替换管道（组合根唯一调用点）。返回旧管道（调用方负责处置），未装过 = null。</summary>
    public static ILogSink? Configure(ILogSink? sink) => Interlocked.Exchange(ref _sink, sink);

    /// <summary>该级别是否会被记录（昂贵消息构造前可据此短路）。</summary>
    public static bool IsEnabled(LogLevel level) => Sink?.IsEnabled(level) ?? false;

    public static void Trace(string message, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Trace, category, message, null, null, member);

    public static void Debug(string message, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Debug, category, message, null, null, member);

    public static void Info(string message, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Info, category, message, null, null, member);

    public static void Warn(string message, Exception? ex = null, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Warn, category, message, ex, null, member);

    public static void Error(string message, Exception? ex = null, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Error, category, message, ex, null, member);

    public static void Fatal(string message, Exception? ex = null, string category = DefaultCategory, [CallerMemberName] string member = "")
        => Record(LogLevel.Fatal, category, message, ex, null, member);

    /// <summary>结构化入口（引擎 / 模块用）：显式级别 + 分类 + 附加字段（<c>at</c> 缺省填调用成员名）；
    /// <paramref name="elapsedMs"/> 落到记录的**首类字段**（JSONL 顶层 <c>ms</c>），不要塞进 props。</summary>
    public static void Write(LogLevel level, string category, string message, Exception? ex = null,
        IReadOnlyDictionary<string, object?>? props = null, [CallerMemberName] string member = "",
        long? elapsedMs = null)
        => Record(level, category, message, ex, props, member, elapsedMs);

    /// <summary>开一个日志作用域：期间记录携带这些字段（内层覆盖外层；异步链自动携带）。</summary>
    public static IDisposable BeginScope(params (string Key, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var previous = CurrentScope.Value;
        var frame = new ScopeFrame(fields, previous);
        CurrentScope.Value = frame;
        return new ScopeLease(frame, previous);
    }

    /// <summary>当前作用域快照（外层→内层合并；无作用域 = null）。管道富化调用。</summary>
    public static IReadOnlyDictionary<string, object?>? SnapshotScope()
    {
        var frame = CurrentScope.Value;
        if (frame is null) return null;
        var stack = new List<ScopeFrame>();
        for (var f = frame; f is not null; f = f.Parent) stack.Add(f);
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = stack.Count - 1; i >= 0; i--)
            foreach (var (key, value) in stack[i].Fields) merged[key] = value;
        return merged;
    }

    /// <summary>
    /// 开一个**调用上下文**：期间记录自动带上 correlation / 命令名 / 调用方，且落在记录的
    /// **首类字段**（<see cref="LogRecord.CorrelationId"/> / <see cref="LogRecord.Command"/> /
    /// <see cref="LogRecord.Caller"/>）——"一条用户动作的完整链路"（UI 调用记录 + 引擎里程碑 + 观测面 +
    /// 审计行）能被取齐的根据。
    /// <para>与 <see cref="BeginScope"/> 的分工：scope 是**自由字段**（进 scope 字典，用哪加哪）；
    /// 本上下文是**固定口径的三个字段**，调用链上任何记录一律自动携带——不靠"谁想起来谁手抄"，
    /// 那种分工迟早漏（见 `文档/WARNINGS.md` 32 的教训）。</para>
    /// </summary>
    public static IDisposable BeginCall(string correlationId, string? command = null, string? caller = null)
    {
        var frame = new CallFrame(correlationId, command, caller);
        CurrentCall.Value = frame;
        return new CallLease(frame);
    }

    /// <summary>调用上下文（记录的首类字段来源；仅门面内部与富化使用）。</summary>
    private sealed record CallFrame(string CorrelationId, string? Command, string? Caller);

    private sealed class CallLease(CallFrame frame) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 仅当自己仍是栈顶才清（乱序释放不误伤已建立的内层上下文）
            if (ReferenceEquals(CurrentCall.Value, frame)) CurrentCall.Value = null;
        }
    }

    /// <summary>刷盘（缺省最多等 2s）：异常处理器 / 退出前保证记录落地；未装配 = 无操作。</summary>
    public static void Flush(TimeSpan? timeout = null)
    {
        var sink = Sink;
        if (sink is null) return;
        try
        {
            sink.Flush(timeout ?? TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailures);
            NotifyOnce($"日志刷盘失败（已兜底，避免回灌业务）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>刷盘并卸下管道（进程退出前唯一收尾入口；幂等）。</summary>
    public static void Shutdown()
    {
        var sink = Configure(null);
        if (sink is null) return;
        try { sink.Flush(TimeSpan.FromSeconds(3)); }
        catch { /* 退出路径：尽力刷盘，不再上报（进程即将结束） */ }
        if (sink is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* 同上 */ }
        }
    }

    /// <summary>管道计数快照（未装配 = null，不填假值）。</summary>
    public static LogStats? Stats => Sink?.Stats;

    /// <summary>日志**读侧**入口（管道实现 <see cref="ILogQuerySource"/> 时非空；未装配 = null）。
    /// 调用方（<c>logs.query</c> / <c>logs.level</c>）必须据此**如实报错**（<c>LP.STATE.005</c>），
    /// 不得返回空列表假装"没有日志"——未装配与"没有记录"是两件事。</summary>
    public static ILogQuerySource? QuerySource => Sink as ILogQuerySource;

    /// <summary>清空日志文件（经落点的维护能力；落点无该能力 = 0）。设置页「清空日志」唯一入口。</summary>
    public static int ClearLogFiles() => Sink is ILogFileMaintenance m ? m.ClearFiles() : 0;

    /// <summary>日志文件清单（落点无维护能力 = 空）。</summary>
    public static IReadOnlyList<string> Files => Sink is ILogFileMaintenance m ? m.Files : [];

    private static void Record(LogLevel level, string category, string message, Exception? ex,
        IReadOnlyDictionary<string, object?>? props, string member, long? elapsedMs = null)
    {
        var sink = Sink;
        if (sink is null)
        {
            Interlocked.Increment(ref _unconfiguredDrops);
            NotifyOnce("日志管道未装配（组合根未调用 LpLog.Configure）：记录被丢弃并计数");
            return;
        }

        if (!sink.IsEnabled(level)) return;

        // 调用上下文 → **首类字段**（corr / cmd / caller）：跨源关联键（审计 / 日志 / 错误对象）由它自动成立
        var call = CurrentCall.Value;
        var record = new LogRecord(
            DateTimeOffset.UtcNow, level, category, message, Environment.CurrentManagedThreadId,
            CorrelationId: call?.CorrelationId,
            Caller: call?.Caller,
            Command: call?.Command,
            ElapsedMs: elapsedMs,
            Error: ex is null ? null : LogError.From(ex),
            Scope: SnapshotScope(),
            Props: MergeProps(props, member));

        try
        {
            sink.Write(record);
        }
        catch (Exception writeEx)
        {
            Interlocked.Increment(ref _writeFailures);
            NotifyOnce($"日志落点违反「不抛」约定（已兜底）：{writeEx.GetType().Name}: {writeEx.Message}");
        }
    }

    private static IReadOnlyDictionary<string, object?> MergeProps(
        IReadOnlyDictionary<string, object?>? props, string member)
    {
        if (props is null && string.IsNullOrEmpty(member))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        var merged = props is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(props, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(member) && !merged.ContainsKey("at")) merged["at"] = member;
        return merged;
    }

    private static void NotifyOnce(string message)
    {
        if (Interlocked.Exchange(ref _notified, 1) == 0)
            System.Diagnostics.Trace.TraceWarning("LinkPocket 日志：{0}", message);
    }

    private sealed class ScopeLease(ScopeFrame frame, ScopeFrame? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 仅当自己仍是栈顶才回退（乱序释放不误伤已建立的内层作用域）
            if (ReferenceEquals(CurrentScope.Value, frame)) CurrentScope.Value = previous;
        }
    }
}

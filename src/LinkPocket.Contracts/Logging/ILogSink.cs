namespace LinkPocket.Contracts;

/// <summary>
/// 日志统计（管道与落点的**唯一读数来源**；无数据的字段保持缺省，绝不填假值）。
/// 观测面铁律：计数是可观测性的一部分——过滤 / 丢弃 / 失败全部经它暴露（诊断面与 <c>logs.query</c> 消费）。
/// </summary>
public sealed record LogStats(
    long Accepted = 0,
    long Filtered = 0,
    long Dropped = 0,
    long Written = 0,
    long Failed = 0,
    long DirectWrites = 0,
    int QueueDepth = 0,
    LogLevel? Level = null,
    string? Directory = null,
    string? LastError = null);

/// <summary>
/// 日志落点（sink）契约：管道下游（文件 / 内存 / 控制台…）的唯一抽象。
/// **硬约束**：<see cref="Write"/> 与 <see cref="Flush"/> **不得把异常抛给调用方**——失败自行兜底、
/// 计入 <see cref="Stats"/>（Failed / LastError）后返回；观测失败绝不回灌业务调用方（ARCHITECTURE 不变量 #10）。
/// </summary>
public interface ILogSink
{
    /// <summary>该落点是否接受此级别（管道在入口据此短路；实现一般直接返回 true，过滤归管道）。</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>写入一条记录（实现必须线程安全；不得抛异常）。</summary>
    void Write(LogRecord record);

    /// <summary>把缓冲刷到最终介质（最多等待 <paramref name="timeout"/>；不得抛异常）。</summary>
    void Flush(TimeSpan timeout);

    /// <summary>落点计数快照（只读、无副作用）。</summary>
    LogStats Stats { get; }
}

/// <summary>
/// 日志**文件**的维护能力（可选实现，管道不强制）：设置页「清空日志」与 <c>logs.query</c> 使用。
/// 存在长开写句柄的落点必须经它删除文件（直接 File.Delete 会被句柄挡住 → 静默失效）。
/// </summary>
public interface ILogFileMaintenance
{
    /// <summary>当前日志文件清单（按名称升序 = 时间升序）。</summary>
    IReadOnlyList<string> Files { get; }

    /// <summary>释放写句柄 → 删除全部日志文件（含遗留 <c>*.log</c>）→ 下次写入时重开；返回删除文件数。</summary>
    int ClearFiles();
}

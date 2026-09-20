namespace LinkPocket.Contracts;

/// <summary>
/// 日志**读侧**契约（<c>logs.query</c> / <c>logs.level</c> 的唯一入口；实现 = <c>Diagnostics.LogPipeline</c>，
/// 经 <see cref="LpLog.QuerySource"/> 取得）。模块只依赖 Contracts，因此读侧能力必须从这里暴露——
/// 与 <see cref="ILogFileMaintenance"/>（写侧维护能力）同一路数：能力位可选实现，管道按能力位分发。
/// <para><b>未装配 ≠ 空结果</b>：门面未装配管道时 <see cref="LpLog.QuerySource"/> 为 null，
/// 调用方（<c>logs.*</c>）必须**如实报错**（<c>LP.STATE.005</c>），绝不返回空列表假装"没有日志"。</para>
/// </summary>
public interface ILogQuerySource
{
    /// <summary>当前生效的最低记录级别（运行期可改）。</summary>
    LogLevel Level { get; }

    /// <summary>运行期切换最低记录级别（进程内生效，不落库、不重启）；返回切换前的级别。</summary>
    LogLevel SetLevel(LogLevel level);

    /// <summary>按参数查询日志（同步、无副作用；不触碰业务数据）。</summary>
    LogQueryResult Query(LogQuery query);
}

/// <summary>内存环读侧能力（<c>MemoryLogSink</c> 实现；管道据此服务 <see cref="LogSource.Memory"/>）。</summary>
public interface ILogRingSource
{
    /// <summary>按游标取快照（<paramref name="afterSequence"/> = 0 从头；<paramref name="limit"/> = 0 取全部）；
    /// 返回顺序 = 写入顺序（时间升序）。</summary>
    IReadOnlyList<LogRecord> Snapshot(long afterSequence = 0, int limit = 0);
}

/// <summary>日志文件回读能力（<c>JsonlFileSink</c> 实现；管道据此服务 <see cref="LogSource.File"/>）。
/// 只回读 <c>.jsonl</c>（遗留 <c>.log</c> 是旧文本格式，不解析——清空日志会一并清理）。</summary>
public interface ILogFileReader
{
    /// <summary>从**最新文件**向前回读最多 <paramref name="maxRecords"/> 条已解析记录（返回按时间升序）。</summary>
    LogFileTail ReadTail(int maxRecords);
}

/// <summary>文件回读结果：命中记录 + 如实计数（读了几个文件 / 跳过几行 / 是否还有更早的行）。</summary>
public sealed record LogFileTail(
    IReadOnlyList<LogRecord> Items,
    int FilesRead,
    int Skipped,
    bool MoreAvailable);

namespace LinkPocket.Contracts;

/// <summary>日志读取来源（<c>logs.query</c> 的 source 参数）。</summary>
public enum LogSource
{
    /// <summary>进程内内存环（最近 N 条；零 IO，缺省）。</summary>
    Memory = 0,

    /// <summary>JSONL 日志文件（跨进程历史；从最新文件向前回读）。</summary>
    File = 1,
}

/// <summary>
/// 日志查询参数（<c>logs.query</c>）：过滤 + 游标 + 上限。
/// <para><b>游标口径</b>：<see cref="Cursor"/> 是**进程内单调序号**（管道分配），因此只对
/// <see cref="LogSource.Memory"/> 有意义——文件里的 seq 来自**另一次进程运行**，跨进程不可比，
/// 文件源忽略游标（跨进程排序一律按时间戳）。这条边界如实写在这里，不做"看起来统一"的假统一。</para>
/// </summary>
public sealed record LogQuery(
    long Cursor = 0,
    LogLevel? MinimumLevel = null,
    string? Category = null,
    LogSource Source = LogSource.Memory,
    int Limit = 200);

/// <summary>
/// 日志查询结果（<c>logs.query</c> 的返回；<see cref="Items"/> 按**时间升序**——与文件写入顺序一致，
/// 便于增量轮询与人工对照）。
/// </summary>
/// <param name="NextCursor">下次轮询应带的游标（内存源 = 本次已推进到的最大序号；文件源 = null，跨进程不可比）。</param>
/// <param name="Limit">本次生效的上限（回显实际值，调用方不必复算缺省）。</param>
/// <param name="MinimumLevel">当前生效的最低记录级别（回显，便于解读"为什么看不到 Debug"）。</param>
/// <param name="FilesRead">文件源实际回读的文件数（内存源 = 0）。</param>
/// <param name="SkippedLines">文件源中**无法解析而被跳过**的行数（观测面纪律：跳过要暴露，不静默）。</param>
/// <param name="MoreAvailable">文件源是否还有更早的行未回读（因达到上限而停）。</param>
public sealed record LogQueryResult(
    IReadOnlyList<LogRecord> Items,
    long? NextCursor,
    int Limit,
    LogLevel MinimumLevel,
    LogSource Source,
    int FilesRead = 0,
    int SkippedLines = 0,
    bool MoreAvailable = false);

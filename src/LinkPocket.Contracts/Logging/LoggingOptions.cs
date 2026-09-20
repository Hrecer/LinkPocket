namespace LinkPocket.Contracts;

/// <summary>
/// 日志管道装配选项（**组合根构造，唯一来源**；缺省值即产品缺省口径）。
/// 级别与目录支持宿主经环境变量覆盖（<c>LINKPOCKET_LOG_LEVEL</c> / <c>LINKPOCKET_LOG_DIR</c>，由宿主解析后填入）。
/// </summary>
public sealed record LoggingOptions
{
    /// <summary>最低记录级别（低于它的记录在管道入口被过滤，计入 Filtered）。</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Info;

    /// <summary>日志目录；null = <c>{AppContext.BaseDirectory}/logs</c>。</summary>
    public string? Directory { get; init; }

    /// <summary>单文件上限（字节）：达到即轮转（同日追加三位序号后缀，名称升序 = 时间升序）。</summary>
    public long MaxFileBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>文件保留天数（按文件名日期判定）。</summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>日志文件总字节上限（超出从最旧文件开始删）。</summary>
    public long RetentionMaxBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>有界队列容量：满时普通记录丢弃并计数；error / fatal 走直写旁路，不丢。</summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>内存环容量（<c>logs.query</c> 的进程内水位）。</summary>
    public int MemoryCapacity { get; init; } = 2_000;

    /// <summary>脱敏开关（URL 查询串与敏感键掩码；缺省开）。</summary>
    public bool Redact { get; init; } = true;

    /// <summary>单条消息上限（字符）：超出截断并标记。</summary>
    public int MaxMessageLength { get; init; } = 4_000;
}

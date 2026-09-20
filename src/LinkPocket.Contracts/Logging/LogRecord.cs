namespace LinkPocket.Contracts;

/// <summary>
/// 错误载荷（异常的**结构化投影**）：类型 / 消息 / 引擎错误码 / 完整堆栈 / 一层内部异常。
/// 一条记录一行 JSONL（堆栈整体转义在 <c>err.stack</c> 里），不再有多行文本块（旧 Logger 一个错误写 6 行）。
/// </summary>
public sealed record LogError(
    string Type,
    string Message,
    string? Code = null,
    string? StackTrace = null,
    LogError? Inner = null)
{
    /// <summary>从异常构造（<paramref name="code"/> = 引擎错误码，业务侧一般传 null）。</summary>
    public static LogError From(Exception ex, string? code = null)
        => new(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, code, ex.StackTrace,
            ex.InnerException is { } inner
                ? new LogError(inner.GetType().FullName ?? inner.GetType().Name, inner.Message, null, inner.StackTrace)
                : null);
}

/// <summary>
/// 一条日志记录（唯一记录形态；文件 / 内存 / 未来的任何落点都只消费它）。
/// 字段口径：
/// - <see cref="Sequence"/>：管道分配的单调整数（同进程内单调；跨进程由 ts 排序）；
/// - <see cref="Timestamp"/>：**UTC**（ISO-8601 往返格式落盘，跨源关联用同一时基）；
/// - <see cref="Category"/>：来源分类（如 ui / engine.observe / 模块名），缺省 "app"；
/// - <see cref="Props"/>：结构化附加字段；<c>at</c> = 调用成员名（门面自动填）。
/// </summary>
public sealed record LogRecord(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    int ThreadId,
    long Sequence = 0,
    string? CorrelationId = null,
    string? SessionId = null,
    string? Caller = null,
    string? BatchId = null,
    string? UndoGroupId = null,
    string? Command = null,
    long? ElapsedMs = null,
    LogError? Error = null,
    IReadOnlyDictionary<string, object?>? Scope = null,
    IReadOnlyDictionary<string, object?>? Props = null);

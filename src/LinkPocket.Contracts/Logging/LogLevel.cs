namespace LinkPocket.Contracts;

/// <summary>日志级别（数值即过滤阈值：低于管道最低级别的记录在入口被过滤并计入 Filtered）。
/// 与常见日志体系同名同序（trace &lt; debug &lt; info &lt; warn &lt; error &lt; fatal）。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

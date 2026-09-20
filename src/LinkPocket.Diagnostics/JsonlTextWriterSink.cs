using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// JSONL 文本流落点（**无头 / 容器宿主的日志通道**）：与文件落点**同一 schema**（复用
/// <see cref="LogJsonl.ToLine"/>，一行一条），写到给定的 <see cref="TextWriter"/>——
/// Headless 用 **stderr**：stdout 只承载协议帧，日志绝不能混进去（否则脚本逐行解析会当场崩）。
/// 不轮转、不保留、不建目录（收集策略归消费方）。
/// 观测面铁律：写失败**计数暴露**（Failed + LastError），绝不抛给业务调用方。
/// </summary>
public sealed class JsonlTextWriterSink : ILogSink
{
    private readonly TextWriter _writer;
    private readonly object _lock = new();
    private long _written;
    private long _failed;
    private string? _lastError;

    public JsonlTextWriterSink(TextWriter writer)
        => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public bool IsEnabled(LogLevel level) => true;

    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            var line = LogJsonl.ToLine(record);
            lock (_lock)
            {
                _writer.WriteLine(line);
                // error / fatal 立即刷出：崩溃现场不能留在缓冲里
                if (record.Level >= LogLevel.Error) _writer.Flush();
                _written++;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _failed++;
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public void Flush(TimeSpan timeout)
    {
        try
        {
            lock (_lock) _writer.Flush();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _failed++;
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public LogStats Stats => new(
        Written: Interlocked.Read(ref _written),
        Failed: Interlocked.Read(ref _failed),
        LastError: Volatile.Read(ref _lastError));
}

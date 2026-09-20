using System.Globalization;
using System.Text;
using LinkPocket.Contracts;

namespace LinkPocket.Diagnostics;

/// <summary>
/// JSONL 文件落点（**唯一实现**）：按日命名 + 按大小轮转 + **跨日自动轮转** + 保留策略 + 长开写句柄。
/// 命名：<c>linkpocket-yyyy-MM-dd.NNN.jsonl</c>（NNN 三位，同日递增；**名称升序 = 时间升序**）；
/// 旧 <c>linkpocket-yyyy-MM-dd.log</c> 属历史格式——清空日志时一并清理，不再写入。
/// 轮转触发 = 单文件超过 <see cref="LoggingOptions.MaxFileBytes"/>（写入后判）**或本地日期变化**（写入前判，
/// 长开进程跨零点必须换文件，否则"按日命名"失真、按天保留永不触发）；保留策略在**每次开/换文件时重算**，
/// 因此进程连续运行数月也不会漂移。error / fatal 立即 flush（崩溃现场取证）；UTF-8 无 BOM。
/// 观测面铁律：删除失败 / 写入失败**计数暴露**（Failed + LastError），不静默吞。
/// 另实现读侧能力位 <see cref="ILogFileReader"/>（管道据此服务 <c>logs.query source=file</c>）。
/// </summary>
public sealed class JsonlFileSink : ILogSink, ILogFileMaintenance, ILogFileReader, IDisposable
{
    private const string Prefix = "linkpocket-";
    private const int DateLength = 10;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(5);

    private readonly LoggingOptions _options;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();

    private FileStream? _stream;
    private StreamWriter? _writer;
    private string? _currentPath;
    private DateTime _currentDay;   // 当前打开文件所属的本地日期（跨日判据；无打开文件 = default）
    private long _currentBytes;   // 自行记账：StreamWriter 有缓冲，_stream.Length 在未 flush 时不反映刚写入的行
    private DateTime _nextRetryAt = DateTime.MinValue;
    private long _written;
    private long _failed;
    private string? _lastError;

    /// <param name="clock">本地时间来源（跨日轮转与保留期判据）。缺省系统时钟；可注入以便测试跨零点。</param>
    public JsonlFileSink(LoggingOptions options, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _directory = options.Directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>日志目录（绝对路径）。</summary>
    public string Directory => _directory;

    public bool IsEnabled(LogLevel level) => true;

    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (_writer is null)
            {
                if (_clock.GetUtcNow().UtcDateTime < _nextRetryAt) return;   // 目录持续不可用：退避，不逐条重试
                try
                {
                    EnsureOpen();
                }
                catch (Exception ex)
                {
                    _nextRetryAt = _clock.GetUtcNow().UtcDateTime + RetryBackoff;
                    RecordFailure(ex);
                    return;
                }
            }

            try
            {
                // 跨日轮转：日期变了就换新文件——否则长开进程会把今天的日志写进昨天的文件，"按日命名"失真，
                // 且按天保留（只在开新文件时重算）永远不触发。Roll 里会顺带重跑保留策略。
                if (_clock.GetLocalNow().Date != _currentDay) Roll();

                var line = LogJsonl.ToLine(record);
                _writer!.WriteLine(line);
                _currentBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                _written++;
                if (record.Level >= LogLevel.Error)
                {
                    _writer.Flush();
                    _stream!.Flush(flushToDisk: true);
                }
                if (_currentBytes >= _options.MaxFileBytes) Roll();
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }
    }

    public void Flush(TimeSpan timeout)
    {
        lock (_lock)
        {
            try
            {
                if (_writer is null) return;
                _writer.Flush();
                _stream!.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }
    }

    public LogStats Stats => new(
        Written: Interlocked.Read(ref _written),
        Failed: Interlocked.Read(ref _failed),
        Directory: _directory,
        LastError: Volatile.Read(ref _lastError));

    /// <summary>日志文件清单（含遗留 *.log；按名称升序 = 时间升序）。</summary>
    public IReadOnlyList<string> Files
        => ExistingFiles().OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Path).ToArray();

    /// <summary>清空日志：释放写句柄 → 删除全部 <c>linkpocket-*</c>（.jsonl 与遗留 .log）→ 下次写入时重开。</summary>
    public int ClearFiles()
    {
        lock (_lock)
        {
            CloseWriter();
            var count = 0;
            foreach (var file in ExistingFiles())
            {
                try
                {
                    File.Delete(file.Path);
                    count++;
                }
                catch (Exception ex)
                {
                    RecordFailure(ex);   // 删不掉（被外部占用等）要暴露，不能假装清空成功
                }
            }
            return count;
        }
    }

    /// <summary>
    /// 回读尾部（<see cref="ILogFileReader"/>）：从**最新的 .jsonl 文件向前**回读，最多 <paramref name="maxRecords"/>
    /// 条已解析记录，返回按**时间升序**（= 文件写入顺序）。无法解析的行被**跳过并计数**（Skipped，绝不静默）；
    /// <c>MoreAvailable</c> = 是否还有更早的行未回读（因达到上限而停）。
    /// 只回读 <c>.jsonl</c>——遗留 <c>.log</c> 是旧文本格式（"清空日志"会一并清理，但本方法不解析）。
    /// 读取以 <see cref="FileShare.ReadWrite"/> 打开：与写侧的长开句柄（Write + FileShare.ReadWrite）兼容，
    /// 否则读会直接 IOException（见 `文档/WARNINGS.md` 65）。
    /// </summary>
    public LogFileTail ReadTail(int maxRecords)
    {
        if (maxRecords <= 0) return new LogFileTail([], 0, 0, false);

        var files = ExistingFiles()
            .Where(f => f.Kind == FileKind.Jsonl)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)   // 名称升序 = 时间升序 → 倒序 = 最新在前
            .ToList();

        var newestFirst = new List<LogRecord>();
        var skipped = 0;
        var filesRead = 0;
        var more = false;

        foreach (var file in files)
        {
            var lines = ReadSharedLines(file.Path);
            filesRead++;

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (newestFirst.Count >= maxRecords)
                {
                    more = true;   // 还有更早的行没读
                    break;
                }

                if (LogJsonl.TryParse(lines[i], out var record)) newestFirst.Add(record!);
                else skipped++;
            }

            if (newestFirst.Count >= maxRecords) { more = true; break; }
        }

        newestFirst.Reverse();   // → 时间升序
        return new LogFileTail(newestFirst, filesRead, skipped, more);
    }

    /// <summary>整文件读行（<see cref="FileShare.ReadWrite"/>：写句柄仍开着也能读）；读取失败**计数暴露**并返回已读部分。</summary>
    private List<string> ReadSharedLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);   // 读侧失败同样要暴露（不静默吞、不自愈）
        }

        return lines;
    }

    public void Dispose()
    {
        lock (_lock) CloseWriter();
    }

    private void EnsureOpen()
    {
        if (_writer is not null) return;
        System.IO.Directory.CreateDirectory(_directory);

        var localNow = _clock.GetLocalNow();
        var today = localNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todays = ExistingFiles()
            .Where(f => f.Kind == FileKind.Jsonl && f.Date == today)
            .OrderBy(f => f.Sequence)
            .ToList();
        var last = todays.Count > 0 ? todays[^1] : (LogFileInfo?)null;

        string path;
        var append = false;
        if (last is { } tail && tail.Size < _options.MaxFileBytes)
        {
            path = tail.Path;
            append = true;
        }
        else
        {
            var sequence = last?.Sequence + 1 ?? 0;
            path = Path.Combine(_directory, $"{Prefix}{today}.{sequence:D3}.jsonl");
        }

        // FileShare.ReadWrite：日志文件在写入期间仍可被外部工具读取 / tail（删除仍须经 ClearFiles，避免孤儿句柄）
        _stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        _currentPath = path;
        _currentDay = localNow.Date;
        _currentBytes = append ? new FileInfo(path).Length : 0;

        ApplyRetention();
    }

    private void Roll()
    {
        CloseWriter();
        EnsureOpen();   // 开新段（PickCurrent 会给出下一个序号）+ 复核保留
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); } catch { /* 关闭路径：尽力刷，失败已在写入路径计数 */ }
        try { _writer?.Dispose(); } catch { /* 同上 */ }
        try { _stream?.Dispose(); } catch { /* 同上 */ }
        _writer = null;
        _stream = null;
        _currentPath = null;
        _currentDay = default;
        _currentBytes = 0;
        _nextRetryAt = DateTime.MinValue;
    }

    /// <summary>保留策略：先按天（超期删）、再按总字节（从最旧删）；当前打开的文件永不删。</summary>
    private void ApplyRetention()
    {
        var files = ExistingFiles()
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var cutoff = _clock.GetLocalNow().Date
            .AddDays(-_options.RetentionDays)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var total = 0L;
        var kept = new List<LogFileInfo>();
        foreach (var file in files)
        {
            if (file.Kind == FileKind.Jsonl && string.CompareOrdinal(file.Date, cutoff) < 0 && !IsCurrent(file))
            {
                if (TryDelete(file)) continue;
            }
            kept.Add(file);
            total += file.Size;
        }

        foreach (var file in kept)
        {
            if (total <= _options.RetentionMaxBytes) break;
            if (IsCurrent(file)) continue;
            if (TryDelete(file)) total -= file.Size;
        }
    }

    private bool IsCurrent(LogFileInfo file)
        => _currentPath is not null && string.Equals(file.Path, _currentPath, StringComparison.OrdinalIgnoreCase);

    private bool TryDelete(LogFileInfo file)
    {
        try
        {
            File.Delete(file.Path);
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);   // 保留策略失败同样要暴露（不自愈、不静默）
            return false;
        }
    }

    private List<LogFileInfo> ExistingFiles()
    {
        var list = new List<LogFileInfo>();
        if (!System.IO.Directory.Exists(_directory)) return list;

        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, Prefix + "*"))
        {
            var name = Path.GetFileName(path);
            var size = new FileInfo(path).Length;

            if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                var core = name[Prefix.Length..^".jsonl".Length];
                var dot = core.LastIndexOf('.');
                if (dot != DateLength || !int.TryParse(core[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                    continue;
                list.Add(new LogFileInfo(path, name, core[..DateLength], sequence, size, FileKind.Jsonl));
            }
            else if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new LogFileInfo(path, name, name[Prefix.Length..^".log".Length], -1, size, FileKind.LegacyLog));
            }
        }
        return list;
    }

    private void RecordFailure(Exception ex)
    {
        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastError, $"{ex.GetType().Name}: {ex.Message}");
    }

    private enum FileKind { Jsonl, LegacyLog }

    private readonly record struct LogFileInfo(
        string Path, string Name, string Date, int Sequence, long Size, FileKind Kind);
}

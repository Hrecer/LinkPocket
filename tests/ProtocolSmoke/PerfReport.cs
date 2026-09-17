using System.Diagnostics;
using System.Text.Json;

namespace ProtocolSmoke;

/// <summary>
/// 性能门槛记录与 CI 消费点（方案 7.3：10k 基准进 CI）。
/// 每次 <see cref="Asserts.Within"/> 都把「实测值 / 生效门槛 / 是否达标」记进本报告，
/// 运行结束后落盘 <c>perf_report.json</c>，供 CI 归档、趋势对比与失败定位
/// （门槛只在断言里，报告只呈现——两者不重复定义，避免"报告与门槛漂移"）。
/// </summary>
internal sealed class PerfReport
{
    internal static readonly PerfReport Instance = new();

    private readonly object _lock = new();
    private readonly List<PerfSample> _samples = [];

    private PerfReport() { }

    /// <summary>严格模式（CI）：不加放宽倍数，按方案 7.3 原始门槛判定。</summary>
    public bool Strict { get; set; }

    /// <summary>当前放宽倍数（Debug 缺省 5；严格模式恒为 1）。</summary>
    public double Relaxation { get; set; } =
#if DEBUG
        5.0;
#else
        1.0;
#endif

    /// <summary>生效门槛 = 标定门槛 × 放宽倍数（向上取整，至少 1ms）。</summary>
    public long EffectiveLimit(long limitMs) => Math.Max(1, (long)Math.Ceiling(limitMs * Relaxation));

    public void Record(string name, long elapsedMs, long limitMs)
    {
        lock (_lock) _samples.Add(new PerfSample(name, elapsedMs, limitMs));
    }

    public IReadOnlyList<PerfSample> Samples
    {
        get { lock (_lock) return _samples.ToArray(); }
    }

    /// <summary>落盘 JSON 报告；返回写入路径（写不进去时返回 null——报告是观测面，不阻断基准判定）。</summary>
    public string? Write(string? overridePath = null)
    {
        var path = overridePath
            ?? Environment.GetEnvironmentVariable("LP_PERF_REPORT")
            ?? Path.Combine(AppContext.BaseDirectory, "perf_report.json");
        try
        {
            var samples = Samples;
            var document = new
            {
                generated_at = DateTimeOffset.Now,
                configuration =
#if DEBUG
                    "Debug",
#else
                    "Release",
#endif
                strict = Strict,
                relaxation = Relaxation,
                machine = Environment.MachineName,
                framework = Environment.Version.ToString(),
                all_ok = samples.All(s => s.Ok),
                results = samples.Select(s => new
                {
                    name = s.Name,
                    elapsed_ms = s.ElapsedMs,
                    limit_ms = s.LimitMs,
                    ok = s.Ok,
                }),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(document, Options));
            return path;
        }
        catch
        {
            return null;   // 落盘失败不改变结论：门槛由断言判定，报告只是观测
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>单条基准：名称 / 实测毫秒 / 生效门槛毫秒 / 是否达标。</summary>
    internal sealed record PerfSample(string Name, long ElapsedMs, long LimitMs)
    {
        public bool Ok => ElapsedMs <= LimitMs;
    }
}

/// <summary>
/// 计时助手（性能节专用）：冷却 GC 噪声不是目的——用「同一台机器、同一次运行内的相对比较」
/// 与宽松门槛来保证稳定；绝对门槛按方案 7.3 标定，Debug 下统一放宽 <see cref="PerfReport.Relaxation"/> 倍。
/// </summary>
internal static class Perf
{
    /// <summary>计时执行一个异步动作并把结果记进报告 + 断言门槛。</summary>
    public static async Task<long> MeasureAsync(string what, long limitMs, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        Asserts.Within(sw.ElapsedMilliseconds, limitMs, what);
        return sw.ElapsedMilliseconds;
    }
}

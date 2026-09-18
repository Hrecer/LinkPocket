namespace ProtocolSmoke;

/// <summary>冒烟断言 helper（失败即抛，中止全部后续节）。</summary>
internal static class Asserts
{
    public static void That(bool cond, string msg)
    {
        if (!cond) throw new Exception("断言失败: " + msg);
    }

    /// <summary>
    /// 性能断言：门槛按 <b>Release/CI 口径</b>标定。
    /// Debug 构建统一放宽 <see cref="PerfReport.Relaxation"/> 倍（JIT 未优化、无内联），
    /// 严格模式（<c>--strict-perf</c> 或 <c>LP_PERF_STRICT=1</c>）下不放宽 —— 这就是"基准进 CI"的开关。
    /// 实测值同时记入 <see cref="PerfReport"/>，随运行落盘 perf_report.json。
    /// </summary>
    public static void Within(long elapsedMs, long limitMs, string what)
    {
        var effectiveLimit = PerfReport.Instance.EffectiveLimit(limitMs);
        PerfReport.Instance.Record(what, elapsedMs, effectiveLimit);
        That(elapsedMs <= effectiveLimit,
            $"{what} 应 ≤ {effectiveLimit}ms（标定 {limitMs}ms × {PerfReport.Instance.Relaxation}），实际 {elapsedMs}ms");
    }
}

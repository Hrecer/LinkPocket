namespace LinkPocket.Contracts;

/// <summary>
/// 引擎运行时统计（<c>diagnostics.collect</c> 的 cache 段；观测面按需装配——未接线时为 null 而非假值）。
/// 缓存计数是「加速器是否在工作」的唯一可观测证据：命中率长期为 0 = 策略失效（应排查而非静默）。
/// <see cref="ObservationFailures"/>：已提交成功的写遇到观测面（审计/事件发布）失败的次数——
/// 「失败要暴露、但不否定已提交事实」（ARCHITECTURE 不变量 #10）。
/// </summary>
public sealed record EngineRuntimeStats(
    long CacheEntries,
    long CacheHits,
    long CacheMisses,
    long CacheEvictions,
    long CacheInvalidations,
    long EventStoreHead,
    long ObservationFailures)
{
    /// <summary>命中率（无请求时为 0；仅作参考，不做任何自动决策）。</summary>
    public double CacheHitRate
    {
        get
        {
            var total = CacheHits + CacheMisses;
            return total == 0 ? 0d : (double)CacheHits / total;
        }
    }
}

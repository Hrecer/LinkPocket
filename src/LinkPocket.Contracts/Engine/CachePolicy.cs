namespace LinkPocket.Contracts;

/// <summary>
/// 查询缓存策略（读流「校验 → 缓存 → 读池」/ 关键路径优化表）。
/// 由命令描述符声明（Descriptor 是单一事实源）：<see cref="CommandDescriptor.Cache"/> 非空 = 该查询走结果缓存。
///
/// <para><b>失效语义 = 事件名精确失效</b>：读路径在执行前对 <see cref="DependsOn"/> 里的每个事件名取
/// 「世代戳」，随结果一并存进缓存条目；写路径提交后按实际发布的事件名推进对应世代戳。
/// 条目只有在「存档时的世代戳 == 当前世代戳」时才被信任——因此写期间的并发读最坏情况只是
/// 一次多余的回填（fail-safe 方向为"不命中"，绝不会把陈旧结果长期留在缓存里）。</para>
///
/// <para><b>TtlSeconds</b> = 兜底过期（世代戳因进程外改库/漏发事件等原因未推进时，缓存仍会自行失效）。
/// 不设无限期条目：缓存是加速器，不是数据源。</para>
///
/// <para><b>调用方契约</b>：命中时返回的是<b>同一实例</b>（不深拷贝）。声明了缓存策略的查询，
/// 其结果对调用方即为只读——原地排序/清空/改写会污染后续调用方看到的对象。</para>
/// </summary>
public sealed record CachePolicy(IReadOnlyList<string> DependsOn, int TtlSeconds = 10)
{
    /// <summary>声明「本查询的结果只受这些事件影响」。ttl 缺省 10 秒兜底。</summary>
    public static CachePolicy Of(int ttlSeconds, params string[] dependsOn)
    {
        if (dependsOn.Length == 0)
            throw new ArgumentException("cache dependencies must not be empty (empty = never invalidates, forbidden)", nameof(dependsOn));
        if (ttlSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "TTL must be at least 1 second");
        return new CachePolicy(dependsOn, ttlSeconds);
    }

    /// <summary>仅受文件夹/链接变更影响（目录页、树、面包屑这类内容类查询）。</summary>
    public static CachePolicy Content(int ttlSeconds = 10) => Of(ttlSeconds,
        DomainEventNames.FoldersChanged, DomainEventNames.LinksChanged);

    /// <summary>受文件夹/链接/回收站三方影响（计数类查询：回收站项数进统计口径）。</summary>
    public static CachePolicy Counting(int ttlSeconds = 10) => Of(ttlSeconds,
        DomainEventNames.FoldersChanged, DomainEventNames.LinksChanged, DomainEventNames.TrashChanged);
}

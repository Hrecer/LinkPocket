namespace LinkPocket.Kernel;

/// <summary>
/// 引擎资源上限（组合根可配置；缺省值 = 既有行为）。
/// 目的：把「命令实现里写死的魔法上限」收敛为一处可配置策略，并让触限在**响应里可见**
/// （响应带 truncated 标志）——绝不静默截断。
/// </summary>
public sealed record EngineLimits
{
    /// <summary>单次读取能返回的最大条目数（per_page 未启用分页时"全量"的实际上限）。</summary>
    public int MaxPageSize { get; init; } = 10_000;

    /// <summary>缺省上限（不改配置时的行为）。</summary>
    public static readonly EngineLimits Default = new();
}
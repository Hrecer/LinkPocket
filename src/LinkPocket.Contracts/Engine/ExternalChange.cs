namespace LinkPocket.Contracts;

/// <summary>
/// **外部进程**写入的一条变更（操作记录尾流的一行投影）：由附属库审计表 <c>audit_log</c> 的一行解析而来，
/// 携带"哪个命令、谁调的、改了哪些实体、发布了哪些事件、人话摘要"。
///
/// <para><b>为什么需要它</b>：<c>PRAGMA data_version</c> 只能回答"别的连接刚提交过"（且**本进程自己的写也会让它跳**），
/// 不能回答"改了什么"。审计表本来就是"一切副作用可审计"的落地物（写侧 <c>SqlAuditWriter</c>），
/// 读它的尾部即可得到**逐条精确变更**：① 精确失效（只推进实际发布的事件名）；
/// ② 精确提示（告诉界面外部到底动了什么）；③ 精确标脏（派生视图只认相关变更）。</para>
///
/// <para><b>归属过滤</b>：本类型只承载"外部进程"的行（见 <see cref="CallerRef.IsExternalProcess"/>）——
/// 界面自己的写（<c>ui:-</c>）与应用内助手的写（<c>agent:&lt;会话&gt;</c>）**已经**有进程内事件驱动刷新，
/// 若也算作"外部变更"，用户自己点一下就会弹一条"外部变更"提示（实测会真的发生）。</para>
/// </summary>
/// <param name="Id">审计行 id（单调递增 = 流的水位）。</param>
/// <param name="At">发生时刻（带本地偏移）。</param>
/// <param name="Command">命令名（如 <c>links.move_batch</c>）。</param>
/// <param name="Caller">调用方文本（外部进程恒为 <see cref="CallerRef.ExternalAgent"/> 的文本形态）。</param>
/// <param name="Success">调用是否成功（失败也留痕，但不改数据）。</param>
/// <param name="DryRun">是否干跑（零副作用）。</param>
/// <param name="IsNested">是否批/宏的嵌套子步骤（其变更已并入父条目的事件集会一并发布）。</param>
/// <param name="BatchId">批运行键（非批调用为 null）。</param>
/// <param name="Touched">本次调用影响的实体（去重后）。</param>
/// <param name="Events">本次调用发布的领域事件名（**查询缓存精确失效的判据**）。</param>
/// <param name="HumanSummary">人话摘要（引擎产出的英文机器面文案，非本地化 UI 文案）。</param>
public sealed record ExternalChange(
    long Id,
    DateTimeOffset At,
    string Command,
    string Caller,
    bool Success,
    bool DryRun,
    bool IsNested,
    string? BatchId,
    IReadOnlyList<EntityRef> Touched,
    IReadOnlyList<string> Events,
    string? HumanSummary)
{
    /// <summary>是否**真的改到了数据**（成功 + 非干跑 + 非嵌套子步骤）：提示与计数只认它。</summary>
    public bool IsEffective => Success && !DryRun && !IsNested;
}

/// <summary>
/// 跨进程变更流（**读侧唯一契约**）：按"审计行 id"这个单调整数取增量，一次一水位。
///
/// <para><b>水位语义</b>：<see cref="Watermark"/> = 已消费到的最新审计行 id。首次构造**不回放历史**
/// （从当前最大 id 起）——界面关心的永远是"从现在开始变了什么"，不是"历史上改过什么"。</para>
///
/// <para><b>降级口径</b>：审计被 <c>audit.prune</c> 裁剪、库读不到、或行内载荷不可解析时，
/// 本接口**返回空**而不是猜——调用方把"空"当作"无法精确"，退化为**整体失效 + 通用刷新**（保守但正确）。
/// 也就是说：精确是**优化**，正确性不依赖精确。</para>
/// </summary>
public interface IChangeFeed
{
    /// <summary>当前水位（已消费到的最新审计行 id）。</summary>
    long Watermark { get; }

    /// <summary>取水位之后的新行（按 id 升序）并推进水位；没有新行 = 空列表。</summary>
    /// <param name="limit">单次上限（防止一次拉取过多；超出部分留在下一次）。</param>
    IReadOnlyList<ExternalChange> Poll(int limit);
}

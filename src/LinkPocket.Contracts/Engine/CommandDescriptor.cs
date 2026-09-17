namespace LinkPocket.Contracts;

/// <summary>命令能力标志（方案 4.2 Descriptor.Caps）。</summary>
[Flags]
public enum CommandCaps
{
    None = 0,
    /// <summary>查询命令：走读池，免写闸/免审计/免撤销。</summary>
    Query = 1 << 0,
    /// <summary>变更命令：走完整写流（写闸 → UoW 事务 → 事件发布 → 审计）。</summary>
    Mutation = 1 << 1,
    /// <summary>可撤销（描述符声明逆向命令名，见 UndoInverse）。</summary>
    Reversible = 1 << 2,
    /// <summary>破坏性：需要两阶段确认令牌（CONFIRM_REQUIRED → token → 执行）。</summary>
    Destructive = 1 << 3,
    /// <summary>长任务：必须声明 SupportsCancellation 并按令牌中断。</summary>
    LongRunning = 1 << 4,
    /// <summary>涉及文件读写（FILE_IO_ERROR 可重试）。</summary>
    FileIo = 1 << 5,
    /// <summary>网络抓取（闸外执行，慢站点不阻塞数据操作）。</summary>
    NetworkOutsideGate = 1 << 6,
    /// <summary>支持调用方取消（CancellationToken 贯穿）。</summary>
    SupportsCancellation = 1 << 7,
}

/// <summary>影响面摘要（破坏性命令 CONFIRM_REQUIRED 时随 Details 下发）。</summary>
public sealed record ImpactSummary(string Text)
{
    public static readonly ImpactSummary Link = new("链接");
    public static readonly ImpactSummary Folder = new("文件夹（含子树）");
    public static readonly ImpactSummary Database = new("整库");
    public override string ToString() => Text;
}

/// <summary>命令参数描述（目录自描述用；TypeName 机器可读，Phase 4 起参与入参校验）。</summary>
public sealed record ParamSpec(string Name, string TypeName, string Description, bool Required)
{
    public static ParamSpec Req<T>(string name, string description) => new(name, typeof(T).Name, description, true);
    public static ParamSpec Opt<T>(string name, string description) => new(name, typeof(T).Name, description, false);
}

/// <summary>
/// 命令描述符：命令的唯一自描述事实源（方案 4.2）。
/// 目录（AI 工具清单/用户文档/测试骨架）由它机械生成，杜绝文档漂移。
/// </summary>
public sealed record CommandDescriptor(
    string Name,
    string Category,
    string Description,
    IReadOnlyList<ParamSpec> Parameters,
    CommandCaps Caps,
    string? UndoInverse = null,
    ImpactSummary? Impact = null,
    CachePolicy? Cache = null)
{
    public bool IsQuery => Caps.HasFlag(CommandCaps.Query);
    public bool IsMutation => Caps.HasFlag(CommandCaps.Mutation);
    public bool IsDestructive => Caps.HasFlag(CommandCaps.Destructive);

    /// <summary>本查询是否参与结果缓存（方案 7.2：只有声明了依赖与 TTL 的查询才缓存）。</summary>
    public bool IsCacheable => IsQuery && Cache is not null;
}

/// <summary>引擎目录清单（engine.describe 的返回；AI 工具清单/文档的唯一来源）。</summary>
public sealed record EngineManifest(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CommandDescriptor> Commands);

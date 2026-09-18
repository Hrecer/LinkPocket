namespace LinkPocket.Contracts;

/// <summary>命令能力标志（Descriptor.Caps）。</summary>
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

/// <summary>命令参数描述（目录自描述用；TypeName 机器可读，参与入参校验）。</summary>
public sealed record ParamSpec(string Name, string TypeName, string Description, bool Required)
{
    /// <summary>
    /// 类型名规范化：<c>typeof(IReadOnlyList&lt;string&gt;).Name</c> 是带反引号的
    /// <c>"IReadOnlyList`1"</c>——落到目录/AI 工具清单里既畸形又丢失泛型信息；这里展平成可读形态
    /// <c>"IReadOnlyList&lt;string&gt;"</c>（泛型参数递归规范化）。
    /// </summary>
    public static ParamSpec Req<T>(string name, string description) => new(name, TypeNameOf(typeof(T)), description, true);
    public static ParamSpec Opt<T>(string name, string description) => new(name, TypeNameOf(typeof(T)), description, false);

    internal static string TypeNameOf(Type type)
        => type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeNameOf))}>"
            : type.Name;
}

/// <summary>
/// 命令描述符：命令的唯一自描述事实源。
/// 目录（AI 工具清单/用户文档/测试骨架）由它机械生成，杜绝文档漂移。
/// </summary>
/// <param name="UndoInverse">
/// 逆向命令名——**仅当"逆向参数 = 原参数"时声明**（对称对 links.trash↔trash.restore 是唯一形态）。
/// 需要旧值的命令（移动/新建/复制/删文件夹）**留空**，由处理器经 <see cref="CommandResult.Undo"/> 回填
/// 逆向步骤（含计算出的参数）。「这个命令可撤销」由 <see cref="CommandCaps.Reversible"/> 表达。
/// 留空的另一个必要性：否则引擎的"退回原参数"路径会把**不可逆的调用形态**（如 folders.delete 的
/// delete_all）也记进撤销栈，让 Ctrl+Z 给出"撤销成功"的假象。
/// </param>
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

    /// <summary>本查询是否参与结果缓存（只有声明了依赖与 TTL 的查询才缓存）。</summary>
    public bool IsCacheable => IsQuery && Cache is not null;
}

/// <summary>引擎目录清单（engine.describe 的返回；AI 工具清单/文档的唯一来源）。</summary>
public sealed record EngineManifest(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CommandDescriptor> Commands);

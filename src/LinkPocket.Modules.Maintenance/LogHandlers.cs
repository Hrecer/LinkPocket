using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>logs.level 的结果（如实回报切换前后；公开 = 供消费者反序列化）。</summary>
public sealed record LogsLevelResult(string Level, string Previous);

/// <summary>logs.* 共用的前置与参数校验（唯一实现；两处各写一份 = 迟早漂移）。</summary>
internal static class LogCommands
{
    /// <summary>
    /// 取日志读侧入口；**未装配 = 报错 LP.STATE.005**——不返回空结果假装"没有日志"：
    /// "宿主没装日志管道"和"确实没有记录"是两件完全不同的事（观测面纪律）。
    /// </summary>
    public static ILogQuerySource WiredSource()
        => LpLog.QuerySource ?? throw new EngineException(EngineErrors.Of(
            EngineErrors.LogUnavailable,
            "日志管道未装配：宿主未调用 EngineComposer.ConfigureLogging",
            JsonSerializer.SerializeToElement(new { wiring = "EngineComposer.ConfigureLogging" })));

    /// <summary>可选级别参数：缺失 / null = null（不过滤）；非法名称 → LP.VAL.003（详情带合法清单，
    /// 绝不静默按缺省级别过滤——那会把"参数写错了"读成"日志本来就少"）。</summary>
    public static LogLevel? OptionalLevel(JsonElement args, string name)
    {
        var raw = CommandArgs.OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (LogLevels.TryParse(raw, out var level)) return level;

        throw InvalidLevel(name, raw);
    }

    /// <summary>非法级别的统一报错（logs.level 用它；详情同样带合法清单）。</summary>
    public static EngineException InvalidLevel(string name, string raw)
        => new(EngineErrors.Of(
            EngineErrors.EnumOutOfRange,
            $"参数「{name}」不是合法日志级别：{raw}",
            JsonSerializer.SerializeToElement(new { @param = name, allowed = LogLevels.Names })));
}

/// <summary>
/// logs.query（Query）：**日志读侧的唯一入口**——读内存环（缺省，零 IO）或 JSONL 文件（跨进程历史），
/// 按游标 / 最低级别 / 来源分类过滤，时间升序返回。
/// <para>与 <c>audit.query</c> 的分工：audit = 结构化**调用史**（落库、可 SQL 过滤）；logs = **过程记录**
/// （内存环 + 文件，含 UI 与观测面噪音）；两者靠 <c>correlation_id</c> 对齐到同一条用户动作。</para>
/// <para>免 UoW / 免写闸：日志不在库里（内存环 + 文件），本命令不触碰任何业务数据。</para>
/// </summary>
internal sealed class LogsQueryHandler : ICommandHandler
{
    /// <summary>缺省返回条数（够看"刚才发生了什么"，又不至于把上下文灌满）。</summary>
    private const int DefaultLimit = 200;

    /// <summary>硬上限（防一次拉爆内存；要更多请用 cursor 分段轮询）。</summary>
    private const int MaxLimit = 1000;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "logs.query",
        Category: "logs",
        Description: "读日志：source=memory 读内存环（缺省，零 IO，支持 cursor 增量轮询）或 " +
                     "source=file 从最新 JSONL 日志文件向前回读（跨进程历史）；level/category/correlation_id 过滤，时间升序；" +
                     "未装配日志管道 → LP.STATE.005",
        Parameters:
        [
            ParamSpec.Opt<long>("cursor", "只返回序号大于它的记录（进程内游标，仅 source=memory 有效；缺省 0 = 全部）"),
            ParamSpec.Opt<string>("level", "最低级别：trace/debug/info/warn/error/fatal（大小写不敏感，越界 → LP.VAL.003）"),
            ParamSpec.Opt<string>("category", "来源分类精确匹配（如 ui / engine.call / engine.pipeline / modules.trash）"),
            ParamSpec.Opt<string>("correlation_id",
                "关联 ID：一次用户动作的全部日志（UI 调用记录 + 引擎里程碑 + 动作汇总）——与 audit.query 同一把钥匙"),
            ParamSpec.Opt<string>("source", "读取来源：memory（缺省，内存环）| file（日志文件）"),
            ParamSpec.Opt<int>("limit", $"返回条数上限（缺省 {DefaultLimit}，上限 {MaxLimit}）"),
        ],
        Caps: CommandCaps.Query);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var source = LogCommands.WiredSource();

        var limit = CommandArgs.OptionalInt(args, "limit", DefaultLimit);
        if (limit < 1 || limit > MaxLimit)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange,
                $"参数「limit」越界：允许 1..{MaxLimit}，实际 {limit}",
                JsonSerializer.SerializeToElement(new { @param = "limit", min = 1, max = MaxLimit })));

        var cursor = CommandArgs.OptionalLong(args, "cursor");
        if (cursor < 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange,
                $"参数「cursor」越界：不得为负，实际 {cursor}",
                JsonSerializer.SerializeToElement(new { @param = "cursor", min = 0 })));

        var result = source.Query(new LogQuery(
            Cursor: cursor,
            MinimumLevel: LogCommands.OptionalLevel(args, "level"),
            Category: CommandArgs.OptionalString(args, "category"),
            Source: ParseSource(CommandArgs.OptionalString(args, "source")),
            Limit: limit,
            CorrelationId: CommandArgs.OptionalString(args, "correlation_id")));

        return Task.FromResult(CommandResult.Ok(result));
    }

    /// <summary>读取来源：缺省 memory；未声明值 → LP.VAL.003（不猜意图，也不静默退回缺省）。</summary>
    private static LogSource ParseSource(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "memory" => LogSource.Memory,
        "file" => LogSource.File,
        _ => throw new EngineException(EngineErrors.Of(
            EngineErrors.EnumOutOfRange,
            $"参数「source」不是合法来源：{raw}",
            JsonSerializer.SerializeToElement(new { @param = "source", allowed = new[] { "memory", "file" } }))),
    };
}

/// <summary>
/// logs.level（Mutation · 非破坏）：**运行期**切换日志最低级别——进程内生效，不落库、不重启。
/// 用途：现场排障（临时开到 debug 看细节，事后调回），与 <c>LINKPOCKET_LOG_LEVEL</c>（启动口径）互补。
/// <para>切换后写一条 Warn 级公告（含 from/to）：若新级别高于 warn，这条公告**本身**就按新口径被过滤——
/// 如实如此，不为它开特例（否则"过滤"这件事就有了不可解释的例外）。切换动作另有一条审计（调用史）。</para>
/// </summary>
internal sealed class LogsLevelHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "logs.level",
        Category: "logs",
        Description: "运行期切换日志最低级别（进程内生效，不落库、不重启；未装配日志管道 → LP.STATE.005）",
        Parameters:
        [
            ParamSpec.Req<string>("level", "新的最低级别：trace/debug/info/warn/error/fatal（大小写不敏感）"),
        ],
        Caps: CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var source = LogCommands.WiredSource();

        var raw = CommandArgs.RequireString(args, "level");
        if (!LogLevels.TryParse(raw, out var level)) throw LogCommands.InvalidLevel("level", raw);

        var previous = source.SetLevel(level);
        LpLog.Write(LogLevel.Warn, "logs.level",
            $"日志最低级别已切换：{LogLevels.Name(previous)} → {LogLevels.Name(level)}",
            props: new Dictionary<string, object?>
            {
                ["previous"] = LogLevels.Name(previous),
                ["level"] = LogLevels.Name(level),
            });

        return Task.FromResult(CommandResult.Ok(
            new LogsLevelResult(LogLevels.Name(level), LogLevels.Name(previous)),
            new ChangeSet(
                Touched: [],
                Events: [],
                HumanSummary: $"日志最低级别：{LogLevels.Name(previous)} → {LogLevels.Name(level)}")));
    }
}

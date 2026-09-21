using System.Globalization;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>audit.query 的分页结果（形状与标准参数口径一致：items/total/page/page_count；公开 = 供消费者反序列化）。</summary>
public sealed record AuditPagedResult(IReadOnlyList<AuditRecord> Items, int Total, int Page, int PageCount);

/// <summary>audit.prune 的结果（如实回报：删了多少行、按什么界限删的；公开 = 供消费者反序列化）。</summary>
public sealed record AuditPruneResult(int Deleted, int KeepDays, DateTimeOffset Before);

/// <summary>
/// audit.query（Query）：**调用史的唯一读侧入口**——按命令 / 调用方 / 会话 / correlation / 批 / 时间等过滤，
/// 倒序（新→旧）分页。设计给 AI 与排障：一次 correlation_id 查询即可取回"一条用户动作"的全部审计
/// （顶层 + 各嵌套子记录）；重负载列（args / changes / stack）默认不取，按 include_payloads 显式开启。
/// </summary>
internal sealed class AuditQueryHandler : ICommandHandler
{
    /// <summary>每页上限（防一次拉爆内存；需要更多用分页）。</summary>
    private const int MaxPerPage = 1000;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "audit.query",
        Category: "audit",
        Description: "Query the audit trail (call history): filter by command/caller/session/correlation/batch/time, newest first;" +
                     "include_payloads=true carries args_json/changes_json/stack_trace",
        Parameters:
        [
            ParamSpec.Opt<string>("command", "Command name (exact)"),
            ParamSpec.Opt<string>("caller", "Caller text (exact, e.g. ui:- / agent:s1)"),
            ParamSpec.Opt<string>("session_id", "Session ID (exact)"),
            ParamSpec.Opt<string>("correlation_id", "Correlation ID: every audit row of one call (including nested child records)"),
            ParamSpec.Opt<string>("batch_id", "Batch ID: the batch parent entry and each nested step record"),
            ParamSpec.Opt<bool>("success", "true = successes only; false = failures only"),
            ParamSpec.Opt<bool>("is_nested", "true = nested child records only; false = top level only"),
            ParamSpec.Opt<bool>("dry_run", "true = dry runs only; false = real executions only"),
            ParamSpec.Opt<string>("from", "Time lower bound (ISO-8601, inclusive)"),
            ParamSpec.Opt<string>("to", "Time upper bound (ISO-8601, exclusive half-open)"),
            ParamSpec.Opt<bool>("include_payloads", "Whether to carry args_json / changes_json / stack_trace (default false)"),
            ParamSpec.Opt<int>("page", "Page number (1-based, default 1)"),
            ParamSpec.Opt<int>("per_page", $"Rows per page (default 100, cap {MaxPerPage})"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filter = new AuditQueryFilter
        {
            Command = CommandArgs.OptionalString(args, "command"),
            Caller = CommandArgs.OptionalString(args, "caller"),
            SessionId = CommandArgs.OptionalString(args, "session_id"),
            CorrelationId = CommandArgs.OptionalString(args, "correlation_id"),
            BatchId = CommandArgs.OptionalString(args, "batch_id"),
            Success = CommandArgs.OptionalBoolOrNull(args, "success"),
            IsNested = CommandArgs.OptionalBoolOrNull(args, "is_nested"),
            DryRun = CommandArgs.OptionalBoolOrNull(args, "dry_run"),
            From = OptionalTime(args, "from"),
            To = OptionalTime(args, "to"),
        };
        var includePayloads = CommandArgs.OptionalBool(args, "include_payloads");
        var page = Math.Max(1, CommandArgs.OptionalInt(args, "page", 1));
        var perPage = CommandArgs.OptionalInt(args, "per_page", 100);
        if (perPage < 1 || perPage > MaxPerPage)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange,
                $"parameter 'per_page' out of range: allowed 1..{MaxPerPage}, got {perPage}",
                JsonSerializer.SerializeToElement(new { @param = "per_page", min = 1, max = MaxPerPage })));

        var result = await ctx.Uow.Audit.QueryAsync(filter, (page - 1) * perPage, perPage, includePayloads, ctx.Ct);
        var pageCount = (int)Math.Ceiling(result.Total / (double)perPage);
        return CommandResult.Ok(new AuditPagedResult(result.Items, result.Total, page, pageCount));
    }

    /// <summary>可选时间参数（ISO-8601）；非法格式 → LP.VAL.002（不得静默当"不过滤"）。</summary>
    private static DateTime? OptionalTime(JsonElement args, string name)
    {
        var raw = CommandArgs.OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return value;

        throw new EngineException(EngineErrors.Of(
            EngineErrors.TypeMismatch,
            $"parameter '{name}' is not a valid timestamp (ISO-8601): {raw}",
            JsonSerializer.SerializeToElement(new { @param = name, expected = "ISO-8601" })));
    }
}

/// <summary>
/// audit.prune（Mutation · Destructive）：**审计保留策略**——删除最近 N 天之外的审计行。
/// 破坏性命令走引擎两阶段确认（首次调用返回 LP.SEC.003 与令牌）；支持 DryRun 预演（返回将删行数，零副作用）。
/// 不做 VACUUM：SQLite 复用释放页，文件体积不自动收缩（避免写入放大）。
/// </summary>
internal sealed class AuditPruneHandler : ICommandHandler
{
    /// <summary>缺省保留天数（文档口径：审计按月归档，默认保留 90 天）。</summary>
    private const int DefaultKeepDays = 90;

    public CommandDescriptor Descriptor { get; } = new(
        Name: "audit.prune",
        Category: "audit",
        Description: "Purge audit rows outside the retention window (default keeps the last 90 days; destructive, two-phase confirmation; dry_run previews the row count)",
        Parameters:
        [
            ParamSpec.Opt<int>("keep_days", $"Retention in days (default {DefaultKeepDays}, minimum 1)"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var keepDays = CommandArgs.OptionalInt(args, "keep_days", DefaultKeepDays);
        if (keepDays < 1)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange,
                $"parameter 'keep_days' out of range: at least 1, got {keepDays}",
                JsonSerializer.SerializeToElement(new { @param = "keep_days", min = 1 })));

        var before = DateTimeOffset.Now.AddDays(-keepDays);
        var deleted = await ctx.Uow.Audit.DeleteBeforeAsync(before, ctx.Ct);

        return CommandResult.Ok(
            new AuditPruneResult(deleted, keepDays, before),
            new ChangeSet(
                Touched: [new EntityRef("audit", "*")],
                Events: [],
                HumanSummary: $"Purged {deleted} audit record(s) older than {keepDays} day(s)"));
    }
}

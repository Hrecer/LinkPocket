using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>
/// dedup.apply（Mutation）：执行查重处置——内部重建 plan 后逐条嵌套派发 links.trash
/// （跨模块通信唯一合法形式；子命令事件随父提交一并发布、审计合并为父条目子记录，单审计条目口径）。
/// 不声明 Reversible：逆向依赖扫描时状态、无法精确定义（撤销栈只收 UndoInverse 非空命令，标志无消费路径）。
/// </summary>
internal sealed class DedupApplyHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "dedup.apply",
        Category: "dedup",
        Description: "Apply the duplicate handling plan: move the surplus bookmark of each group into the trash per strategy (nested links.trash dispatch, dry_run can rehearse it)",
        Parameters:
        [
            ParamSpec.Opt<string>("strategy", "keep_most_visited (default) | keep_newest | keep_explicit (unlisted groups are skipped)",
                enumValues: [DedupStrategy.KeepMostVisited, DedupStrategy.KeepNewest, DedupStrategy.KeepExplicit]),
            ParamSpec.Opt<JsonElement>("group_urls", "Handle only groups with these URLs; default = all duplicate groups",
                schema: ParamSchemas.DedupGroupUrls),
            ParamSpec.Opt<JsonElement>("explicit_keep", "Keeper dictionary for keep_explicit; unlisted groups are not handled",
                schema: ParamSchemas.DedupExplicitKeep),
        ],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var groups = await DedupScanHandler.ScanAsync(ctx.Uow, ctx.Ct);
        var plan = DedupPlanHandler.BuildPlan(args, groups, ctx.CorrelationId);

        var trashed = 0;
        var diff = new List<FieldChange>();
        foreach (var group in plan.Groups)
        {
            foreach (var victim in group.Trash)
            {
                // 嵌套步骤的字段级 diff 由 links.trash 产出；并回本命令结果（事件负载侧另有父缓冲聚合 + 值去重）
                var result = await ctx.DispatchNestedAsync("links.trash", new { id = victim.LinkId }, ctx.Ct);
                if (result.Changes?.Diff is { Count: > 0 } stepDiff) diff.AddRange(stepDiff);
                trashed++;
            }
        }

        // 空计划 = 零实际变更：不发事件（避免空事件推进 trash.changed 世代戳、假失效缓存/假刷新）
        var changes = plan.Groups.Count == 0
            ? ChangeSet.Empty
            : new ChangeSet(
                Touched: plan.Groups.SelectMany(g => g.Trash).Select(l => new EntityRef("link", l.LinkId)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.TrashChanged],
                HumanSummary: $"Duplicate scan done: {plan.Groups.Count} groups, {trashed} duplicate bookmarks moved to trash (strategy {plan.Strategy})",
                Diff: diff.Count > 0 ? diff : null);

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { strategy = plan.Strategy, groups = plan.Groups.Count, trashed }),
            changes);
    }
}
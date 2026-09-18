using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>
/// dedup.apply（Mutation）：执行查重处置——内部重建 plan 后逐条嵌套派发 links.trash
/// （跨模块通信唯一合法形式；子命令事件随父提交一并发布、审计合并为父条目子记录，单审计条目口径）。
/// </summary>
internal sealed class DedupApplyHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "dedup.apply",
        Category: "dedup",
        Description: "执行查重处置：按策略把每组多余的重复书签移入回收站（嵌套派发 links.trash，可在干跑模式预演）",
        Parameters:
        [
            ParamSpec.Opt<string>("strategy", "keep_most_visited（默认）| keep_newest | keep_explicit"),
            ParamSpec.Opt<JsonElement>("group_urls", "只处置这些 URL 的组；缺省 = 全部重复组"),
            ParamSpec.Opt<JsonElement>("explicit_keep", "keep_explicit 时的保留者字典"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var groups = await DedupScanHandler.ScanAsync(ctx.Uow, ctx.Ct);
        var plan = DedupPlanHandler.BuildPlan(args, groups, ctx.CorrelationId);

        var trashed = 0;
        foreach (var group in plan.Groups)
        {
            foreach (var victim in group.Trash)
            {
                _ = await ctx.DispatchNestedAsync("links.trash", new { id = victim.LinkId }, ctx.Ct);
                trashed++;
            }
        }

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { strategy = plan.Strategy, groups = plan.Groups.Count, trashed }),
            new ChangeSet(
                Touched: plan.Groups.SelectMany(g => g.Trash).Select(l => new EntityRef("link", l.LinkId)).ToList(),
                Events: ["trash.changed"],
                HumanSummary: $"查重完成：{plan.Groups.Count} 组，移入回收站 {trashed} 个重复书签（策略 {plan.Strategy}）"));
    }
}

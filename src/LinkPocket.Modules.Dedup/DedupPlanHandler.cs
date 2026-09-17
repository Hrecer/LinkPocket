using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>
/// dedup.plan（★ 引擎能力）：把策略变成删除计划——纯干跑、零副作用（复杂命令样板第 2 步）。
/// 策略：keep_most_visited（查看次数最高，平局取最新更新）| keep_newest（最新创建）|
/// keep_explicit（explicit_keep 指定每组保留者，缺失组回落 keep_most_visited）。
/// </summary>
internal sealed class DedupPlanHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "dedup.plan",
        Category: "dedup",
        Description: "生成查重处置计划（纯干跑零副作用）：策略 + 可选 URL 过滤 + 显式保留者 → 每组 keep/trash 清单",
        Parameters:
        [
            ParamSpec.Opt<string>("strategy", "keep_most_visited（默认）| keep_newest | keep_explicit"),
            ParamSpec.Opt<JsonElement>("group_urls", "只计划这些 URL 的组；缺省 = 全部重复组"),
            ParamSpec.Opt<JsonElement>("explicit_keep", "keep_explicit 时的保留者字典 {url: link_id}"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var groups = await DedupScanHandler.ScanAsync(ctx.Uow, ctx.Ct);
        var plan = BuildPlan(args, groups, ctx.CorrelationId);
        return CommandResult.Ok(plan);
    }

    /// <summary>计划构建（scan/apply 共用；纯函数除读参外零副作用）。</summary>
    internal static DedupPlan BuildPlan(JsonElement args, IReadOnlyList<DedupGroup> groups, string correlationId)
    {
        var strategy = CommandArgs.OptionalString(args, "strategy") ?? DedupStrategy.KeepMostVisited;
        if (!DedupStrategy.All.Contains(strategy))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange, $"未知查重策略：{strategy}", correlationId: correlationId));

        var urlFilter = CommandArgs.Raw(args, "group_urls") is { ValueKind: JsonValueKind.Array } raw
            ? raw.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)
            : null;

        var explicitKeep = new Dictionary<string, string>(StringComparer.Ordinal);
        if (strategy == DedupStrategy.KeepExplicit)
        {
            if (CommandArgs.Raw(args, "explicit_keep") is not { ValueKind: JsonValueKind.Object } keepRaw)
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.RequiredParam, "keep_explicit 策略需要 explicit_keep（{url: link_id}）", correlationId: correlationId));
            foreach (var prop in keepRaw.EnumerateObject())
                explicitKeep[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        var planGroups = new List<DedupPlanGroup>();
        foreach (var group in groups)
        {
            if (urlFilter != null && !urlFilter.Contains(group.Url)) continue;

            LinkDto keep;
            if (strategy == DedupStrategy.KeepExplicit && explicitKeep.TryGetValue(group.Url, out var keepId))
            {
                keep = group.Links.FirstOrDefault(l => l.LinkId == keepId)
                    ?? throw new EngineException(EngineErrors.Of(
                        EngineErrors.EntityNotFound,
                        $"URL「{group.Url}」的保留者 {keepId} 不在该组内", correlationId: correlationId));
            }
            else if (strategy == DedupStrategy.KeepNewest)
            {
                keep = group.Links.OrderByDescending(l => l.CreatedAt).First();
            }
            else
            {
                keep = group.Links
                    .OrderByDescending(l => l.VisitCount)
                    .ThenByDescending(l => l.UpdatedAt)
                    .First();
            }

            var trash = group.Links.Where(l => l.LinkId != keep.LinkId).ToList();
            planGroups.Add(new DedupPlanGroup(group.Url, keep, trash));
        }

        return new DedupPlan(strategy, planGroups, planGroups.Sum(g => g.Trash.Count));
    }
}

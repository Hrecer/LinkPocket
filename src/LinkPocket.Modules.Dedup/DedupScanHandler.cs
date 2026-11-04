using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Dedup;

/// <summary>dedup.scan（Query）：按 URL 分组全库链接，返回重复组（计数 > 1）。</summary>
internal sealed class DedupScanHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "dedup.scan",
        Category: "dedup",
        Description: "Scan duplicate bookmarks: grouped by URL, returning groups with more than one entry (ascending creation time within a group)",
        Parameters: [],
        Caps: CommandCaps.Query,
        // 结果只随链接/回收站变化而变（回收站变化会改变"谁还在库里"）；同一次数据状态下的重复调用
        // （进入去重页 + 事件刷新 + 防抖补跑）不必各扫一遍
        Cache: CachePolicy.Of(30, DomainEventNames.LinksChanged, DomainEventNames.TrashChanged),
        // UI 去重页直接消费本查询（只回重复组，不再拉全库）：分组键与引擎的 URL 判定同口径（Ordinal）。
        Client: new("DedupScanAsync", typeof(IReadOnlyList<DedupGroup>)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var groups = await ScanAsync(ctx.Uow, ctx.Ct);
        return CommandResult.Ok(groups);
    }

    internal static async Task<IReadOnlyList<DedupGroup>> ScanAsync(Kernel.IUnitOfWork uow, CancellationToken ct)
    {
        // 「哪些 URL 重复」由 SQL 判定（仓库层先 GROUP BY 取重复 URL，再按密度取回）：
        // 仓储直出 DTO，处理器只做分组/排序，不再自己拉全库。
        var links = await uow.Links.ListDuplicatedDtosAsync(ct);
        var groups = links
            .GroupBy(l => l.Url, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DedupGroup(
                g.Key,
                g.Count(),
                g.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Url, StringComparer.Ordinal)
            .ToList();
        return groups;
    }
}

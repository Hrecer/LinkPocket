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
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var groups = await ScanAsync(ctx.Uow, ctx.Ct);
        return CommandResult.Ok(groups);
    }

    internal static async Task<IReadOnlyList<DedupGroup>> ScanAsync(Kernel.IUnitOfWork uow, CancellationToken ct)
    {
        var links = await uow.Links.ListAsync(new LinkQuerySpec(), ct);
        var groups = links
            .GroupBy(l => l.Url, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DedupGroup(
                g.Key,
                g.Count(),
                g.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId, StringComparer.Ordinal)
                    .Select(l => l.ToDto()).ToList()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Url, StringComparer.Ordinal)
            .ToList();
        return groups;
    }
}

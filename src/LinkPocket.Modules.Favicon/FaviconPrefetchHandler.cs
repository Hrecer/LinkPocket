using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Favicon;

/// <summary>
/// favicon.prefetch（Mutation）：把缺失图标的链接加入后台预取队列（fire-and-forget，
/// 立即返回排队数——写闸只持有入队瞬间；队列内部并发 4、按地址去重，「预取后台化」）。
/// link_ids 缺省 = 全库缺失图标的链接。
/// </summary>
internal sealed class FaviconPrefetchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "favicon.prefetch",
        Category: "favicon",
        Description: "Queue links with missing favicons for background prefetch (returns immediately; concurrency 4, deduplicated, backoff on failure)",
        Parameters: [ParamSpec.Opt<IReadOnlyList<string>>("link_ids", "Specific links; default = every link in the database without a favicon")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var specified = CommandArgs.StringArray(args, "link_ids");
        var ct = ctx.Ct;

        List<string> faviconUrls;
        if (specified.Count > 0)
        {
            var urls = new List<string>();
            foreach (var id in specified)
            {
                var link = await ctx.Uow.Links.FindAsync(new LinkId(id), ct)
                    ?? throw new EngineException(EngineErrors.Of(
                        EngineErrors.EntityNotFound, $"link {id} does not exist", correlationId: ctx.CorrelationId));
                if (!string.IsNullOrWhiteSpace(link.FaviconUrl)) urls.Add(link.FaviconUrl!);
            }

            faviconUrls = urls;
        }
        else
        {
            var links = await ctx.Uow.Links.ListAsync(new LinkQuerySpec(), ct);
            faviconUrls = links
                .Where(l => !string.IsNullOrWhiteSpace(l.FaviconUrl))
                .Select(l => l.FaviconUrl!)
                .ToList();
        }

        var queued = PrefetchQueue.EnqueueMissing(faviconUrls);
        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { queued }),
            ChangeSet.Of(
                new EntityRef("favicon_cache", "*"),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"Queued {queued} favicon(s) for prefetch"));
    }
}

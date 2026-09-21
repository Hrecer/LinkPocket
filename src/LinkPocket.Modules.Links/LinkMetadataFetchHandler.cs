using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.metadata_fetch（Query）：抓取页面元数据（标题/描述/favicon）。
/// 读池执行、免写闸——慢站点不阻塞任何数据操作；网络失败 = NETWORK_ERROR（可重试）。
/// </summary>
internal sealed class LinkMetadataFetchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.metadata_fetch",
        Category: "links",
        Description: "Fetch page metadata (title / og: / description / favicon; 10s timeout, runs outside the write gate)",
        Parameters: [ParamSpec.Req<string>("url", "Page URL (absolute http/https)")],
        Caps: CommandCaps.Query | CommandCaps.NetworkOutsideGate | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var url = CommandArgs.RequireString(args, "url");
        var metadata = await MetadataFetcher.FetchAsync(url, ctx.Ct);
        return CommandResult.Ok(new MetadataDto
        {
            Title = metadata.Title,
            Description = metadata.Description,
            FaviconUrl = metadata.FaviconUrl,
        });
    }
}

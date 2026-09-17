using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.metadata_fetch（Query）：抓取页面元数据（标题/描述/favicon）。
/// 读池执行、免写闸——慢站点不阻塞任何数据操作（方案 7.1）；网络失败 = NETWORK_ERROR（可重试）。
/// </summary>
internal sealed class LinkMetadataFetchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.metadata_fetch",
        Category: "links",
        Description: "抓取网页元数据（title / og: / description / favicon；10s 超时，闸外执行）",
        Parameters: [ParamSpec.Req<string>("url", "页面地址（http/https 绝对地址）")],
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

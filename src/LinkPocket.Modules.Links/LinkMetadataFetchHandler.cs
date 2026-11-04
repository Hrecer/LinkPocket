using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.metadata_fetch（Query）：抓取页面元数据（标题/描述/favicon）。
/// 读池执行、免写闸——慢站点不阻塞任何数据操作；网络失败 = NETWORK_ERROR（可重试）。
/// <para><b>interactive</b>：只有用户主动发起的抓取才传 <c>true</c>（需要真人点一下时开专用窗口）；
/// 默认 <c>false</c>，遇到人机挑战立即返回 <c>needs_human_verification</c>，由调用方决定是否重试。
/// 批处理永不使用本参数（见 <c>links.metadata_fetch_batch</c>）。</para>
/// </summary>
internal sealed class LinkMetadataFetchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.metadata_fetch",
        Category: "links",
        Description: "Fetch page metadata (title / og: / description / favicon; HTTP has a 10s timeout and runs outside the write gate; optional browser-render / interactive verification tiers take longer and report elapsed_ms). interactive=true may open a real browser window for human verification (user-initiated only); the result reports resolution / needs_human_verification / human_verification_available / elapsed_ms",
        Parameters: [
            ParamSpec.Req<string>("url", "Page URL (absolute http/https)"),
            ParamSpec.Opt<bool>("interactive", "Allow a real browser window when the site requires a human check (default false; only for user-initiated fetches)"),
        ],
        Caps: CommandCaps.Query | CommandCaps.NetworkOutsideGate |
              CommandCaps.LongRunning | CommandCaps.SupportsCancellation,
        Client: new("LinkMetadataFetchAsync", typeof(MetadataDto)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var url = CommandArgs.RequireString(args, "url");
        var interactive = CommandArgs.OptionalBool(args, "interactive");
        var outcome = await MetadataFetcher.FetchDetailedAsync(
            url, ctx.Ct, allowHumanVerification: interactive);
        var metadata = outcome.Metadata;
        return CommandResult.Ok(new MetadataDto
        {
            Title = metadata.Title,
            Description = metadata.Description,
            FaviconUrl = metadata.FaviconUrl,
            BlockedByVerification = metadata.BlockedByVerification,
            Resolution = outcome.Resolution,
            NeedsHumanVerification = outcome.NeedsHumanVerification,
            HumanVerificationAvailable = outcome.HumanVerificationAvailable,
            BrowserRenderAvailable = PageRenderHook.Renderer is not null,
            ElapsedMs = outcome.ElapsedMs,
        });
    }
}

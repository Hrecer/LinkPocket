using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.find_by_url（★ 引擎能力）：按地址找全部同址链接（查重/Dedup 模块的复用点）。</summary>
internal sealed class LinkFindByUrlHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.find_by_url",
        Category: "links",
        Description: "Find every link with exactly this URL (reuse point for duplicate handling)",
        Parameters: [ParamSpec.Req<string>("url", "Link URL (exact match)")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var url = CommandArgs.RequireString(args, "url");
        var links = await ctx.Uow.Links.FindByUrlAsync(url, ctx.Ct);
        return CommandResult.Ok(links.Select(l => l.ToDto()).ToList());
    }
}

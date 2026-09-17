using System.Text.Json;
using LinkPocket.Api;
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
        Description: "按 URL 精确查找全部同址链接（查重场景复用点）",
        Parameters: [ParamSpec.Req<string>("url", "链接地址（精确匹配）")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var url = CommandArgs.RequireString(args, "url");
        var links = await ctx.Uow.Links.FindByUrlAsync(url, ctx.Ct);
        return CommandResult.Ok(links.Select(l => l.ToDto()).ToList());
    }
}

using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.get（Query）：按 ID 取单条链接；不存在 → ENTITY_NOT_FOUND（引擎语义）。</summary>
internal sealed class LinkGetHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.get",
        Category: "links",
        Description: "Get one link by ID",
        Parameters: [ParamSpec.Req<string>("id", "Link ID")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var link = await ctx.Uow.Links.FindAsync(id, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"link {id} does not exist", correlationId: ctx.CorrelationId));
        return CommandResult.Ok(link.ToDto());
    }
}

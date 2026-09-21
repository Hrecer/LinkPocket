using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.update（Mutation）：编辑链接（url/title/description/list_id/is_important/favicon_url）。
/// list_id：缺省 = 不改归属；只接受真实目录 ID（移到根级请用 <c>links.move_batch</c> 的 target_list_id 缺省）。
/// 链接被编辑或跨目录移动 → 新旧两个文件夹的内容都变了。
/// </summary>
internal sealed class LinkUpdateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.update",
        Category: "links",
        Description: "Edit a link (only explicitly provided fields change)",
        Parameters:
        [
            ParamSpec.Req<string>("id", "Link ID"),
            ParamSpec.Opt<string>("url", "New URL"),
            ParamSpec.Opt<string>("title", "New title"),
            ParamSpec.Opt<string>("description", "New description"),
            ParamSpec.Opt<string>("list_id", "New folder ID (default = ownership unchanged; use links.move_batch to move to root level)"),
            ParamSpec.Opt<bool>("is_important", "Whether it is important"),
            ParamSpec.Opt<string>("favicon_url", "Favicon URL"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var url = CommandArgs.OptionalString(args, "url");
        var title = CommandArgs.OptionalString(args, "title");
        var description = CommandArgs.OptionalString(args, "description");
        var listIdArg = CommandArgs.OptionalString(args, "list_id");
        var faviconUrl = CommandArgs.OptionalString(args, "favicon_url");
        var isImportant = CommandArgs.OptionalBoolOrNull(args, "is_important");
        var ct = ctx.Ct;

        var link = await ctx.Uow.Links.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"link {id} does not exist", correlationId: ctx.CorrelationId));
        var previousListId = link.ListId;

        if (!string.IsNullOrEmpty(url)) link.Url = url.Trim();
        if (title != null) link.Title = title;
        if (description != null) link.Description = description;
        if (listIdArg != null) link.ListId = listIdArg;
        if (isImportant != null) link.IsImportant = isImportant.Value;
        if (faviconUrl != null) link.FaviconUrl = faviconUrl;
        link.UpdatedAt = DateTime.UtcNow;

        // 新旧两个文件夹的内容构成变化（跨目录移动时）
        await ctx.Uow.Trees.TouchModifiedAsync(
            link.ListId == null ? null : new FolderId(link.ListId), ct);
        if (previousListId != link.ListId)
            await ctx.Uow.Trees.TouchModifiedAsync(
                previousListId == null ? null : new FolderId(previousListId), ct);

        // 撤销载荷：**仅当归属真的变了**（跨目录移动）才可撤销——改名/改描述/改收藏**绝不入撤销栈**
        // （重命名与改属性不属于可撤销动作）。
        // 描述符**不声明** UndoInverse：否则引擎的"退回原参数"路径会把纯改名也变成可撤销。
        // 逆向用 links.move_batch（唯一能表达"移回根级"的命令：target_list_id 缺省 = 根）。
        var undo = previousListId == link.ListId
            ? null
            : new[]
            {
                new UndoInverseStep("links.move_batch",
                    JsonSerializer.SerializeToElement(new { link_ids = new[] { id.Value }, target_list_id = previousListId }))
            };

        return CommandResult.Ok(
            link.ToDto(),
            ChangeSet.Of(
                new EntityRef("link", link.LinkId),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"Link '{link.Title ?? link.Url}' updated"),
            undo);
    }
}

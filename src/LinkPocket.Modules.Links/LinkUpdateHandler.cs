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
        Description: "编辑链接（仅显式传入的字段会被修改）",
        Parameters:
        [
            ParamSpec.Req<string>("id", "链接 ID"),
            ParamSpec.Opt<string>("url", "新地址"),
            ParamSpec.Opt<string>("title", "新标题"),
            ParamSpec.Opt<string>("description", "新描述"),
            ParamSpec.Opt<string>("list_id", "新目录 ID（缺省 = 不改归属；移到根级用 links.move_batch）"),
            ParamSpec.Opt<bool>("is_important", "是否重要"),
            ParamSpec.Opt<string>("favicon_url", "图标地址"),
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
                EngineErrors.EntityNotFound, $"链接 {id} 不存在", correlationId: ctx.CorrelationId));
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

        return CommandResult.Ok(
            link.ToDto(),
            ChangeSet.Of(
                new EntityRef("link", link.LinkId),
                "links.changed",
                $"已更新链接「{link.Title ?? link.Url}」"));
    }
}

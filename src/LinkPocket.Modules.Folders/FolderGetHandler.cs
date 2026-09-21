using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.get（Query）：按 ID 取单个文件夹（计数两口径与树一致）。
/// 参数缺省 / null = 在向「根」寻址 → 根不是实体、没有 ID → ROOT_NOT_ENTITY；
/// 给了 ID 但不存在 → ENTITY_NOT_FOUND。
/// </summary>
internal sealed class FolderGetHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.get",
        Category: "folders",
        Description: "Get one folder by ID (link_count = recursive link count, direct_link_count = direct child link count)",
        Parameters: [ParamSpec.Opt<string>("folder_id", "Folder ID; default = root (the root is not an entity -> LP.STATE.002)")],
        Caps: CommandCaps.Query,
        // 单文件夹读取仍是「全量文件夹 + 全量计数两口径」两趟，按内容类缓存（定位/跳转复用率高）
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.OptionalString(args, "folder_id");
        if (FolderIds.IsRoot(id))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RootNotEntity, "the root is not a folder and has no ID", correlationId: ctx.CorrelationId));

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == id)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"folder {id} does not exist", correlationId: ctx.CorrelationId));
        var counts = await ctx.Uow.Trees.LinkCountsAsync(ctx.Ct);
        return CommandResult.Ok(folder.ToDto(counts));
    }
}

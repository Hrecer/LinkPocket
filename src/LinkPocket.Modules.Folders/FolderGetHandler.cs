using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.get（Query）：按 ID 取单个文件夹（递归计数与树口径一致）。
/// 根不是实体 → ROOT_NOT_ENTITY；不存在 → ENTITY_NOT_FOUND（引擎语义，Phase 6 前端适配）。
/// </summary>
internal sealed class FolderGetHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.get",
        Category: "folders",
        Description: "按 ID 取单个文件夹（link_count = 递归子链接数）",
        Parameters: [ParamSpec.Req<string>("folder_id", "文件夹 ID")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "folder_id");
        if (FolderIds.IsRoot(id))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RootNotEntity, "根目录不是文件夹、没有 ID", correlationId: ctx.CorrelationId));

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == id)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"文件夹 {id} 不存在", correlationId: ctx.CorrelationId));
        var counts = await ctx.Uow.Trees.RecursiveLinkCountsAsync(ctx.Ct);
        return CommandResult.Ok(folder.ToDto(counts));
    }
}

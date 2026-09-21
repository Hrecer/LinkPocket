using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.breadcrumb（Query）：面包屑路径名列表；根 = 「全部书签」；未知文件夹回落根（既有口径）。</summary>
internal sealed class FolderBreadcrumbHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.breadcrumb",
        Category: "folders",
        Description: "Get the breadcrumb path of a folder (name list, including the root display name)",
        Parameters: [ParamSpec.Opt<string>("folder_id", "Folder ID; default = root")],
        Caps: CommandCaps.Query,
        // 每次目录导航都会取面包屑（全量文件夹一遍）；只受文件夹改名/移动影响
        Cache: CachePolicy.Of(10, DomainEventNames.FoldersChanged));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folderId = CommandArgs.OptionalString(args, "folder_id");
        if (FolderIds.IsRoot(folderId))
            return CommandResult.Ok(new List<string> { FolderIds.RootToken });

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId);
        var breadcrumb = folder == null
            ? new List<string> { FolderIds.RootToken }
            : FolderSupport.BuildBreadcrumb(folder, allFolders);
        return CommandResult.Ok(breadcrumb);
    }
}

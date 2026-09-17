using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.breadcrumb（Query）：面包屑路径名列表；根 = 「全部书签」；未知文件夹回落根（既有口径）。</summary>
internal sealed class FolderBreadcrumbHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.breadcrumb",
        Category: "folders",
        Description: "取某目录的面包屑路径（名称列表，含根显示名「全部书签」）",
        Parameters: [ParamSpec.Opt<string>("folder_id", "目录 ID；缺省 = 根")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folderId = FolderIds.Normalize(CommandArgs.OptionalString(args, "folder_id"));
        if (FolderIds.IsRoot(folderId))
            return CommandResult.Ok(new List<string> { FolderIds.RootDisplayName });

        var allFolders = await ctx.Uow.Folders.ListAllAsync(ctx.Ct);
        var folder = allFolders.FirstOrDefault(f => f.FolderId == folderId);
        var breadcrumb = folder == null
            ? new List<string> { FolderIds.RootDisplayName }
            : FolderSupport.BuildBreadcrumb(folder, allFolders);
        return CommandResult.Ok(breadcrumb);
    }
}

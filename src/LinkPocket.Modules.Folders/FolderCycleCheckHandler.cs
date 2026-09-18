using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>folders.cycle_check（Query）：把文件夹移动到目标父目录下是否会产生环。</summary>
internal sealed class FolderCycleCheckHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.cycle_check",
        Category: "folders",
        Description: "检查把文件夹移动到目标父目录下是否会产生环（true = 会成环）",
        Parameters:
        [
            ParamSpec.Req<string>("folder_id", "要移动的文件夹 ID"),
            ParamSpec.Opt<string>("target_parent_id", "目标父目录 ID；缺省 = 根（永不成环）"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folderId = new FolderId(CommandArgs.RequireString(args, "folder_id"));
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        var wouldCycle = target != null
            && await ctx.Uow.Trees.WouldCreateCycleAsync(folderId, new FolderId(target), ctx.Ct);
        return CommandResult.Ok(wouldCycle);
    }
}

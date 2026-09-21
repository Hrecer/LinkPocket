using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Locate;

/// <summary>
/// locate.resolve（Query）：把一个 ID（**链接或文件夹——ID 在两者之间唯一**）解析成"它在目录里的位置"：
/// 目标类型 / 容器目录（进入它才能看到目标那一行）/ 容器显示路径 / 目标自身显示路径 / 名称。
///
/// <para>语义与界面无关（一切读取皆查询）：界面的"跳转" = 切到浏览页 + 进入 <c>container_folder_id</c>
/// + 选中 <c>id</c>；无头宿主 / 批处理 / AI 可复用同一结果。根 = <c>null</c>（零哨兵，与全库同口径）。</para>
///
/// <para>ID 不存在（链接与文件夹都没有）→ <c>ENTITY_NOT_FOUND</c>（零兼容：查询就报错，不返回 null）。</para>
/// </summary>
internal sealed class LocateResolveHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "locate.resolve",
        Category: "locate",
        Description: "按 ID 解析目标位置（类型 / 容器目录 / 路径），供定位与跳转使用",
        Parameters: [ParamSpec.Req<string>("id", "目标 ID（链接或文件夹）")],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var raw = CommandArgs.RequireString(args, "id");

        // 链接优先（与界面 ID 跳转同一判别顺序：先链接后文件夹）
        var link = await ctx.Uow.Links.FindAsync(new LinkId(raw), ctx.Ct);
        if (link != null)
        {
            var containerPath = await ctx.Uow.Trees.PathCanonicalAsync(
                link.ListId == null ? null : new FolderId(link.ListId), ctx.Ct);
            return CommandResult.Ok(new LocateResolveDto
            {
                Kind = "link",
                Id = link.LinkId,
                Name = link.Title ?? string.Empty,
                ContainerFolderId = link.ListId,
                ContainerPath = containerPath,
                Path = BookmarkPath.Append(containerPath, link.Title),
            });
        }

        var folder = await ctx.Uow.Folders.FindAsync(new FolderId(raw), ctx.Ct);
        if (folder != null)
        {
            var containerPath = await ctx.Uow.Trees.PathCanonicalAsync(
                folder.ParentId == null ? null : new FolderId(folder.ParentId), ctx.Ct);
            return CommandResult.Ok(new LocateResolveDto
            {
                Kind = "folder",
                Id = folder.FolderId,
                Name = folder.Name,
                ContainerFolderId = folder.ParentId,
                ContainerPath = containerPath,
                Path = BookmarkPath.Append(containerPath, folder.Name),
            });
        }

        throw new EngineException(EngineErrors.Of(
            EngineErrors.EntityNotFound, $"ID {raw} 不存在（链接与文件夹都没有）", correlationId: ctx.CorrelationId));
    }
}

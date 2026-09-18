using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.visit_record（Mutation）：记录一次「查看」——链接 LastVisitedAt/VisitCount +
/// 所在文件夹沿父链刷新（同一工作单元一次提交，方案 7.2 性能项）。查看不改 UpdatedAt。
/// </summary>
internal sealed class LinkVisitRecordHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.visit_record",
        Category: "links",
        Description: "记录一次查看（链接计数 +1、最后查看刷新；所在文件夹沿父链同步刷新）",
        Parameters: [ParamSpec.Req<string>("id", "链接 ID")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = new LinkId(CommandArgs.RequireString(args, "id"));
        var ct = ctx.Ct;

        var link = await ctx.Uow.Links.FindAsync(id, ct)
            ?? throw new EngineException(EngineErrors.Of(
                EngineErrors.EntityNotFound, $"链接 {id} 不存在", correlationId: ctx.CorrelationId));

        link.VisitCount++;
        link.LastVisitedAt = DateTime.UtcNow;

        // 文件夹「最后查看 / 查看次数」沿父链刷新（与链接写入同一 UoW → 单次提交）
        await ctx.Uow.Trees.RecordFolderViewAsync(
            link.ListId == null ? null : new FolderId(link.ListId), ct);

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { link_id = link.LinkId, visit_count = link.VisitCount }),
            ChangeSet.Of(
                new EntityRef("link", id.Value),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"已记录查看「{link.Title ?? link.Url}」"));
    }
}

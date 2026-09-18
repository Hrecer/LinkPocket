using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.create（Mutation）：新建链接。
/// auto_fetch_metadata = true 且标题/描述缺失时抓取元数据补齐；**抓取失败不阻断创建，但结果如实上报**
/// （ChangeSet.Warnings + 摘要），绝不静默吞掉——否则调用方以为补全了、实际没有。
/// 写路径内抓取仅发生在显式开启时（默认关），常态网络抓取请走 links.metadata_fetch（闸外）。
/// </summary>
internal sealed class LinkCreateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.create",
        Category: "links",
        Description: "新建链接（list_id 缺省 = 根级；auto_fetch_metadata 可选自动补全标题/描述/图标）",
        Parameters:
        [
            ParamSpec.Req<string>("url", "链接地址"),
            ParamSpec.Opt<string>("title", "标题"),
            ParamSpec.Opt<string>("description", "描述"),
            ParamSpec.Opt<string>("list_id", "所属目录 ID；缺省 = 根级"),
            ParamSpec.Opt<bool>("is_important", "是否重要"),
            ParamSpec.Opt<bool>("auto_fetch_metadata", "自动抓取页面元数据（缺省 false；失败会在结果 warnings 里上报）"),
            ParamSpec.Opt<string>("favicon_url", "显式指定图标地址"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var url = CommandArgs.RequireString(args, "url");
        var title = CommandArgs.OptionalString(args, "title");
        var description = CommandArgs.OptionalString(args, "description");
        var listIdArg = CommandArgs.OptionalString(args, "list_id");
        var listId = listIdArg;
        var isImportant = CommandArgs.OptionalBool(args, "is_important");
        var autoFetch = CommandArgs.OptionalBool(args, "auto_fetch_metadata");
        var faviconUrl = CommandArgs.OptionalString(args, "favicon_url");
        var ct = ctx.Ct;

        var link = new Link
        {
            Url = url.Trim(),
            Title = title,
            Description = description,
            ListId = listId,
            IsImportant = isImportant,
            VisitCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        List<string>? warnings = null;
        if (autoFetch && (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(description)))
        {
            try
            {
                var metadata = await MetadataFetcher.FetchAsync(link.Url, ct);
                link.Title ??= string.IsNullOrEmpty(metadata.Title) ? null : metadata.Title;
                link.Description ??= string.IsNullOrEmpty(metadata.Description) ? null : metadata.Description;
                link.FaviconUrl ??= string.IsNullOrEmpty(metadata.FaviconUrl) ? null : metadata.FaviconUrl;
            }
            catch (EngineException ex)
            {
                // 抓取失败不阻断创建；但必须上报（调用方据此决定是否重试 links.metadata_fetch）
                warnings = [$"元数据抓取失败（{ex.Error.Code}）：{ex.Error.Message}"];
            }
        }

        if (!string.IsNullOrEmpty(faviconUrl))
            link.FaviconUrl = faviconUrl;

        _ = await ctx.Uow.Links.AddAsync(link, ct);

        if (listId != null)
            await LinkSupport.RefreshLinkCountAsync(ctx.Uow, listId, ct);

        // 新增链接 → 所在文件夹内容有变
        await ctx.Uow.Trees.TouchModifiedAsync(listId == null ? null : new FolderId(listId), ct);

        var summary = $"已创建链接「{link.Title ?? link.Url}」"
                      + (warnings == null ? "" : "（元数据未抓取到）");
        // 撤销载荷：撤销"新建链接"（也覆盖"复制粘贴链接"）= 把刚建的链接移入回收站（软删除）。
        // **重做必须显式给出** = 从回收站还原（保留原 ID）：若按缺省重放 links.create，会生成**新 ID**，
        // 原 ID 丢失且回收站里留下旧快照（同一个用户动作变成两条数据）。
        return CommandResult.Ok(
            link.ToDto(),
            new ChangeSet(
                Touched: [new EntityRef("link", link.LinkId)],
                Events: [LinkPocket.Contracts.DomainEventNames.LinksChanged],
                HumanSummary: summary,
                Warnings: warnings),
            [new UndoInverseStep("links.trash",
                JsonSerializer.SerializeToElement(new { id = link.LinkId }),
                new UndoAction("trash.restore",
                    JsonSerializer.SerializeToElement(new { id = link.LinkId, to_origin = true })))]);
    }
}

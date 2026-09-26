using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.tree_links（Query）：某个目录的**直接链接**的轻量投影（id / 标题 / 地址 / 归属），
/// 供浏览页目录树的链接叶子**按需加载**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不复用既有命令</b>：
/// ① <c>folders.contents</c> / <c>folders.overview</c> 返回完整 <see cref="LinkDto"/> —— 真实库里每条链接的
/// <c>favicon_url</c> 可能是 500 字符的内嵌 <c>data:</c> URI，展开一个 15 000 条的文件夹就是十几 MB，
/// 而树只画名称；
/// ② <c>links.query</c> 支持 <c>fields</c> 投影，但它的响应在**进程内**是模块内部类型，
/// 契约层拿不到强类型（`EngineClient.QueryAsync&lt;T&gt;` 的进程内直调不做序列化，跨类型转换会抛）。
/// </para>
/// <para>
/// <b>缓存口径</b>与 <c>folders.tree</c> 一致（内容型：文件夹 / 链接变更即失效）。
/// 单次返回上限 = 引擎页上限（<see cref="EngineLimits.MaxPageSize"/>）。
/// </para>
/// </remarks>
internal sealed class FolderTreeLinksHandler(EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.tree_links",
        Category: "folders",
        Description: "Get the direct links of one folder as a light projection (id / title / url / folder), for tree leaves loaded on demand",
        Parameters:
        [
            ParamSpec.Opt<string>("folder_id", "Folder ID; default = root level (the root is not an entity and has no ID)"),
            ParamSpec.Opt<int>("per_page", "Max rows; 0 = everything (bounded by the engine cap)"),
        ],
        Caps: CommandCaps.Query,
        Cache: CachePolicy.Content());

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folderId = CommandArgs.OptionalString(args, "folder_id");
        var perPage = Math.Max(0, CommandArgs.OptionalInt(args, "per_page", 0));
        var effectivePerPage = perPage == 0 ? limits.MaxPageSize : Math.Min(perPage, limits.MaxPageSize);

        var filter = FolderIds.IsRoot(folderId)
            ? new LinkFilter { Unfiled = true }
            : new LinkFilter { FolderId = new FolderId(folderId!) };

        var links = await ctx.Uow.Links.ListAsync(new LinkQuerySpec
        {
            Filter = filter,
            Sort = new[] { new SortSpec("title", SortDir.Asc) },
            Page = new PageSpec(1, effectivePerPage),
        }, ctx.Ct);

        return CommandResult.Ok(links.Select(l => l.ToTreeDto()).ToList());
    }
}

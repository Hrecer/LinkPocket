using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Favicon;

/// <summary>
/// favicon.cache_gc（Mutation）：清掉磁盘缓存里**没有任何行引用**的图标文件——
/// 删掉的链接（已过回收站又彻底删除）/ 换过图标的老文件留下的孤儿。
///
/// <para><b>引用集 = links.favicon_url ∪ trash_links.favicon_url</b>（去重、非空、排内嵌 <c>data:</c>）：
/// 回收站里的快照还带着 favicon_url——**还原之后图标必须还在**，所以被删进回收站的链接不算孤儿。
/// 文件名 = <c>SHA256(地址)+扩展名</c>（<see cref="FaviconCache.GetCacheFilePath"/>，两侧同一实现），
/// 因此"哪条数据用哪个文件"可以直接对账，不需要把缓存文件反解成地址。</para>
///
/// <para><b>保守边界</b>：名字不像缓存文件（不是 64 位十六进制 + 扩展名）的文件**绝不碰**——
/// 用户的杂物、其它工具的文件都可能落在这个目录里；删除失败（解码中的图标会锁住文件）只计数 + 留痕，
/// 下次启动再清，绝不因为一个锁住的文件让整场清扫失败。</para>
/// </summary>
internal sealed class FaviconCacheGcHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "favicon.cache_gc",
        Category: "favicon",
        Description: "Delete favicon cache files not referenced by any link or trash entry (orphans from deleted links); referenced files and unrecognized files are kept; locked files are reported and retried later",
        Parameters: [],
        Caps: CommandCaps.Mutation,
        Client: new("FaviconCacheGcAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var directory = FaviconCache.CacheDirectory;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddReferenced(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || FaviconCache.IsInlineData(url)) return;   // 内嵌图标从不落盘
            keep.Add(Path.GetFileName(FaviconCache.GetCacheFilePath(url)));
        }

        foreach (var url in await ctx.Uow.Links.ListFaviconUrlsAsync(ctx.Ct)) AddReferenced(url);
        foreach (var url in await ctx.Uow.Trash.ListFaviconUrlsAsync(ctx.Ct)) AddReferenced(url);

        var kept = 0;
        var deleted = 0;
        var failed = 0;
        long freedBytes = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (keep.Contains(name) || !LooksLikeCacheFileName(name))
                {
                    kept++;
                    continue;
                }
                try
                {
                    freedBytes += new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;   // 正在被解码持有（展示中的图标会锁文件）/ 权限问题：下次启动再清
                    LpLog.Warn($"favicon cache gc could not delete '{name}' (will retry on a later run)", ex, category: "favicon.gc");
                }
            }
        }

        return CommandResult.Ok(JsonSerializer.SerializeToElement(new
        {
            cache_directory = directory,
            referenced = keep.Count,
            kept,
            deleted,
            failed,
            freed_bytes = freedBytes,
        }));
    }

    /// <summary>缓存文件名形态：64 位十六进制（大写，<c>SHA256</c> 十六进制编码）+ 扩展名。认不出的绝不删。</summary>
    private static bool LooksLikeCacheFileName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot != 64 || name.Length - dot < 2 || name.Length - dot > 6) return false;
        for (var i = 0; i < 64; i++)
        {
            var c = name[i];
            var hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
            if (!hex) return false;
        }
        return true;
    }
}

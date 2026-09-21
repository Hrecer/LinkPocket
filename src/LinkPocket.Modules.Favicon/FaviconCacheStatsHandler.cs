using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Favicon;

/// <summary>favicon.cache_stats（Query）：磁盘缓存统计（文件数 / 总字节 / 目录）。</summary>
internal sealed class FaviconCacheStatsHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "favicon.cache_stats",
        Category: "favicon",
        Description: "Favicon disk cache statistics (file count, total bytes, cache directory)",
        Parameters: [],
        Caps: CommandCaps.Query);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var fileCount = 0;
        long totalBytes = 0;
        if (Directory.Exists(FaviconCache.CacheDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(FaviconCache.CacheDirectory))
            {
                fileCount++;
                totalBytes += new FileInfo(file).Length;
            }
        }

        return Task.FromResult(CommandResult.Ok(JsonSerializer.SerializeToElement(new
        {
            cache_directory = FaviconCache.CacheDirectory,
            file_count = fileCount,
            total_bytes = totalBytes,
        })));
    }
}

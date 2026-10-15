using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// favicon.cache_gc：图标缓存只按"库里的引用"清理——链接被彻底删掉后留下的孤儿图标会被删；
/// 活链接与**回收站条目**引用着的、名字认不出是缓存文件的（用户的杂物）都不动。
/// </summary>
public class FaviconCacheGcTests
{
    [Fact]
    public async Task 清扫_删孤儿_保引用与异物()
    {
        var (engine, _, _) = TestHost.Create();
        var directory = FaviconCache.CacheDirectory;
        Directory.CreateDirectory(directory);

        // 引用侧 ①：一条活链接；引用侧 ②：一条已进回收站的链接（快照仍带 favicon_url——还原后要用）
        var keepUrl = "https://gc.example/keep.png";
        var trashedUrl = "https://gc.example/trashed.png";
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://gc.example/a", title = "A", favicon_url = keepUrl });
        var trashed = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://gc.example/b", title = "B", favicon_url = trashedUrl });
        await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = trashed.Data!.LinkId });

        // 孤儿：库里没有任何行引用（模拟"链接已被彻底删除"）
        var orphanUrl = "https://gc.example/orphan.png";

        WriteFile(FaviconCache.GetCacheFilePath(keepUrl));
        WriteFile(FaviconCache.GetCacheFilePath(trashedUrl));
        WriteFile(FaviconCache.GetCacheFilePath(orphanUrl));
        var foreign = Path.Combine(directory, "readme.txt");
        await File.WriteAllTextAsync(foreign, "not a cache file");

        var result = await engine.ExecuteAsync<JsonElement>("favicon.cache_gc");

        Assert.True(File.Exists(FaviconCache.GetCacheFilePath(keepUrl)), "活链接引用的图标必须保留");
        Assert.True(File.Exists(FaviconCache.GetCacheFilePath(trashedUrl)), "回收站引用着的图标必须保留（还原后还要用）");
        Assert.False(File.Exists(FaviconCache.GetCacheFilePath(orphanUrl)), "无引用的孤儿图标必须被删掉");
        Assert.True(File.Exists(foreign), "认不出是缓存文件的（用户的杂物）绝不碰");
        Assert.True(result.Data.GetProperty("deleted").GetInt32() >= 1);
        Assert.True(result.Data.GetProperty("referenced").GetInt32() >= 2);
    }

    /// <summary>内容不重要（清扫只看文件名与引用对账，不读文件字节）。</summary>
    private static void WriteFile(string path) => File.WriteAllBytes(path, [137, 80, 78, 71]);
}

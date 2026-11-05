using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>启动空闲写路径预热：执行真实写查询后整体回滚，用户数据与库结构都不留痕。</summary>
public class WarmWritePathTests
{
    private static string TempDbPath()
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpwarm_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task 写路径预热_回滚后不留下任何行()
    {
        var dbPath = TempDbPath();
        try
        {
            var factory = new LinkPocketDbContextFactory(dbPath);
            using (var seed = factory.CreateDbContext())
            {
                seed.Folders.Add(new Folder { Name = "existing" });
                await seed.SaveChangesAsync();
            }

            await factory.WarmWritePathAsync();

            using (var verify = factory.CreateDbContext())
            {
                Assert.Equal(1, await verify.Folders.CountAsync());
                Assert.Equal("existing", await verify.Folders.Select(f => f.Name).SingleAsync());
                Assert.Equal(0, await verify.Links.CountAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(dbPath + suffix);
        }
    }
}

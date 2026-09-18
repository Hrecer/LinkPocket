using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// LinkPocketDbContext 主键碰撞兜底回归（2026-09-18）：
/// 新增 Folder/Link 的随机 ID 撞库后应换号重试而非直接抛错；换号只作用于批内新增实体，
/// 引用修正覆盖批内新增 + 被修改实体；回收站表（trash_*）碰撞不参与换号（语义不同）。
/// 本轮同时钉住：异步保存路径的兜底必须生效（曾是非 async 死代码，catch 永不触发）。
/// </summary>
public class LinkPocketDbContextCollisionTests
{
    /// <summary>固定种子主键（12 位数字 = EntityIds.NewFolderId 合法形态）。</summary>
    private const string SeedFolderId = "123456789012";

    private static string TempDbPath()
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpcol_{Guid.NewGuid():N}.db");

    private static void DeleteDb(string dbPath)
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // 池清理失败不阻断文件删除
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = dbPath + suffix;
            try { if (File.Exists(file)) File.Delete(file); } catch { /* 句柄滞留 → 系统清理 */ }
        }
    }

    /// <summary>建库并预置一条固定 ID 的种子文件夹。</summary>
    private static string SeedDatabase()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);
        using (var ctx = new LinkPocketDbContext(dbPath))
        {
            ctx.Folders.Add(new Folder { FolderId = SeedFolderId, Name = "种子目录" });
            ctx.SaveChanges();
        }
        return dbPath;
    }

    [Fact]
    public async Task SaveChangesAsync_新增文件夹主键碰撞_换号成功且同批链接指向新号()
    {
        var dbPath = SeedDatabase();
        try
        {
            string? newFolderId;
            using (var ctx = new LinkPocketDbContext(dbPath))
            {
                // 与种子同 ID → folders.id 主键冲突；同批新链接 ListId 指向它（换号后须同步改指）
                var collidedFolder = new Folder { FolderId = SeedFolderId, Name = "撞号目录" };
                ctx.Folders.Add(collidedFolder);
                var link = new Link { Url = "https://collision.example", Title = "撞号链接", ListId = SeedFolderId };
                ctx.Links.Add(link);

                // 修复前：非 async 直传 Task → catch 不触发 → 直接抛 DbUpdateException；
                // 修复后：await 进 catch → 换号重试成功。
                await ctx.SaveChangesAsync();

                Assert.NotEqual(SeedFolderId, collidedFolder.FolderId);   // 已换新号
                Assert.Equal(collidedFolder.FolderId, link.ListId);        // 引用同步新号
                newFolderId = collidedFolder.FolderId;
            }

            // 落库事实：2 个文件夹（种子 + 新建），链接指向新号
            using var verify = new LinkPocketDbContext(dbPath);
            Assert.Equal(2, await verify.Folders.CountAsync());
            Assert.Equal(newFolderId, (await verify.Links.SingleAsync(l => l.Url == "https://collision.example")).ListId);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public void SaveChanges_同步路径_同场景同构换号成功()
    {
        var dbPath = SeedDatabase();
        try
        {
            string? newFolderId;
            using (var ctx = new LinkPocketDbContext(dbPath))
            {
                var collidedFolder = new Folder { FolderId = SeedFolderId, Name = "撞号目录" };
                ctx.Folders.Add(collidedFolder);
                var link = new Link { Url = "https://collision-sync.example", Title = "撞号链接", ListId = SeedFolderId };
                ctx.Links.Add(link);

                ctx.SaveChanges();

                Assert.NotEqual(SeedFolderId, collidedFolder.FolderId);
                Assert.Equal(collidedFolder.FolderId, link.ListId);
                newFolderId = collidedFolder.FolderId;
            }

            using var verify = new LinkPocketDbContext(dbPath);
            Assert.Equal(2, verify.Folders.Count());
            Assert.Equal(newFolderId, verify.Links.Single(l => l.Url == "https://collision-sync.example").ListId);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_回收站表主键碰撞_不换号原样抛出_钉住作用域边界()
    {
        var dbPath = TempDbPath();
        try
        {
            SchemaMigrator.EnsureSchema(dbPath);
            using (var ctx = new LinkPocketDbContext(dbPath))
            {
                ctx.TrashedLinks.Add(new TrashedLink { LinkId = SeedFolderId, Url = "https://trash.example" });
                ctx.SaveChanges();
            }

            using var ctx2 = new LinkPocketDbContext(dbPath);
            ctx2.TrashedLinks.Add(new TrashedLink { LinkId = SeedFolderId, Url = "https://trash-dup.example" });

            // 回收站表主键冲突 = 语义不同（保留原 ID 是还原依据），必须原样抛错而非换号
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }
}
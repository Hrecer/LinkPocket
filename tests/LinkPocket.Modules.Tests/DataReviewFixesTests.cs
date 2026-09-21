using LinkPocket.Data;
using LinkPocket.Kernel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// Data 层审核报告修复的回归测试：
/// LinkCountsAsync 链式树形（O(N²) → O(N) 记忆化）的递归计数等价性；
/// PathCanonicalAsync 对断链如实返回 @unknown 而非伪装成根（路径一律 canonical，与界面语言无关）。
/// </summary>
public class DataReviewFixesTests
{
    [Fact]
    public async Task Tree_LinkCounts_Chain_Topology_Counts_Recursively()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            const string a = "100000000001", b = "100000000002", c = "100000000003";
            await using (var db = factory.CreateDbContext())
            {
                db.Folders.AddRange(
                    new Folder { FolderId = a, Name = "A" },
                    new Folder { FolderId = b, Name = "B", ParentId = a },
                    new Folder { FolderId = c, Name = "C", ParentId = b });
                db.Links.AddRange(
                    new Link { LinkId = "A1000000000000001", Url = "https://a.example/1", ListId = c },
                    new Link { LinkId = "A1000000000000002", Url = "https://a.example/2", ListId = c });
                await db.SaveChangesAsync();
            }

            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            var counts = await uow.Trees.LinkCountsAsync(default);

            // 直接计数只落在 C
            Assert.Equal(2, counts.Direct[new FolderId(c)]);
            Assert.False(counts.Direct.ContainsKey(new FolderId(a)));
            // 递归计数沿父链向上传播：A/B/C 全为 2
            Assert.Equal(2, counts.Recursive[new FolderId(a)]);
            Assert.Equal(2, counts.Recursive[new FolderId(b)]);
            Assert.Equal(2, counts.Recursive[new FolderId(c)]);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task Tree_PathDisplay_Unknown_Folder_Is_Marked_Unknown()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            Assert.Equal("@root", await uow.Trees.PathCanonicalAsync(null, default));
            // 不存在的目录：如实标 @unknown，不得伪装成根
            Assert.Equal("@unknown", await uow.Trees.PathCanonicalAsync(new FolderId("999000000001"), default));
        }
        finally { Cleanup(dbPath); }
    }

    private static void Cleanup(string path)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 临时文件交给系统清理 */ }
    }
}
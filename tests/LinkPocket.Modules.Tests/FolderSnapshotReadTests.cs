using LinkPocket.Data;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 刷新固定成本直读（B 路线）的回归：
/// ① <c>Folders.ListAllAsync</c> 直读必须逐列还原（含 description / sort_order / last_visited_at /
/// visit_count 这些可选列——漏列就是静默丢数据）；
/// ② <c>Trees.LinkCountsAsync</c> 直读计数的两口径（直接 / 递归）在分叉拓扑上逐层正确。
/// </summary>
public class FolderSnapshotReadTests
{
    [Fact]
    public async Task 全量文件夹直读_逐列还原_含可选列()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            var visited = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var created = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var updated = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            await using (var db = factory.CreateDbContext())
            {
                db.Folders.AddRange(
                    new Folder
                    {
                        FolderId = "100000000001", Name = "快照", Description = "描述文本", SortOrder = 7,
                        LastVisitedAt = visited, VisitCount = 42, CreatedAt = created, UpdatedAt = updated,
                    },
                    new Folder { FolderId = "100000000002", Name = "子目录", ParentId = "100000000001" });
                await db.SaveChangesAsync();
            }

            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            var all = await uow.Folders.ListAllAsync(default);

            Assert.Equal(2, all.Count);
            var full = all.Single(x => x.FolderId == "100000000001");
            Assert.Equal("快照", full.Name);
            Assert.Equal("描述文本", full.Description);
            Assert.Equal(7, full.SortOrder);
            Assert.Null(full.ParentId);
            Assert.Equal(visited, full.LastVisitedAt);
            Assert.Equal(42, full.VisitCount);
            Assert.Equal(created, full.CreatedAt);
            Assert.Equal(updated, full.UpdatedAt);

            var bare = all.Single(x => x.FolderId == "100000000002");
            Assert.Equal("100000000001", bare.ParentId);
            Assert.Null(bare.Description);
            Assert.Null(bare.LastVisitedAt);
            Assert.Equal(0, bare.SortOrder);
            Assert.Equal(0, bare.VisitCount);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 计数直读_分叉拓扑_两口径逐层正确()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            // 拓扑：A →（B → C，D）；链接分别落在 A / C / D，B 无直接链接
            const string a = "100000000001", b = "100000000002", c = "100000000003", d = "100000000004";
            await using (var db = factory.CreateDbContext())
            {
                db.Folders.AddRange(
                    new Folder { FolderId = a, Name = "A" },
                    new Folder { FolderId = b, Name = "B", ParentId = a },
                    new Folder { FolderId = c, Name = "C", ParentId = b },
                    new Folder { FolderId = d, Name = "D", ParentId = a });
                db.Links.AddRange(
                    new Link { LinkId = "A1000000000000001", Url = "https://a.example/1", ListId = a },
                    new Link { LinkId = "A1000000000000002", Url = "https://c.example/1", ListId = c },
                    new Link { LinkId = "A1000000000000003", Url = "https://c.example/2", ListId = c },
                    new Link { LinkId = "A1000000000000004", Url = "https://d.example/1", ListId = d },
                    new Link { LinkId = "A1000000000000005", Url = "https://root.example/" });   // 根级：不进任何计数
                await db.SaveChangesAsync();
            }

            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            var counts = await uow.Trees.LinkCountsAsync(default);

            // 直接口径：只有真有链接的目录在表里，B 不在（不得以 0 混入）
            Assert.Equal(1, counts.Direct[new FolderId(a)]);
            Assert.Equal(2, counts.Direct[new FolderId(c)]);
            Assert.Equal(1, counts.Direct[new FolderId(d)]);
            Assert.False(counts.Direct.ContainsKey(new FolderId(b)));

            // 递归口径：每个目录都有值，等于自身直接数 + 全部子孙直接数
            Assert.Equal(4, counts.Recursive[new FolderId(a)]);
            Assert.Equal(2, counts.Recursive[new FolderId(b)]);
            Assert.Equal(2, counts.Recursive[new FolderId(c)]);
            Assert.Equal(1, counts.Recursive[new FolderId(d)]);
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

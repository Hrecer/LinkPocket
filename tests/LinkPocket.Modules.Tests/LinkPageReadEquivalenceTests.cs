using LinkPocket.Data;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 取页直读（A 路线）的等价性回归：<c>ListDtosAsync</c> 的直读路径（原生 SQL reader）与
/// EF 实体路径（<c>ListAsync + ToDto</c>，即改动前的唯一口径）在
/// <b>全部排序字段 × 方向 × 目录/根 × 分页组合</b>下逐字段一致。
///
/// <para>直读只服务"单一归属谓词 + 白名单排序"这一形态（目录页的全部现实形态）；
/// 复杂过滤（搜索/多条件）回退 EF——本测试同时锁住两条路径的结果不可漂移。</para>
/// </summary>
public class LinkPageReadEquivalenceTests
{
    private const string FolderA = "100000000001";

    /// <summary>种子：覆盖大小写混排、空标题、null 时间、并列值（逼出 ID 兜底）、重要标记、目录内 / 根级。</summary>
    private static async Task SeedAsync(LinkPocketDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var t0 = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        db.Folders.Add(new Folder { FolderId = FolderA, Name = "等价" });
        db.Links.AddRange(
            new Link
            {
                LinkId = "A1000000000000001", Url = "https://bravo.example/x", Title = "Bravo", ListId = FolderA,
                Description = "d1", FaviconUrl = "https://bravo.example/f.ico", IsImportant = true,
                VisitCount = 7, LastVisitedAt = t0.AddDays(5), CreatedAt = t0.AddDays(1), UpdatedAt = t0.AddDays(9),
            },
            new Link
            {
                LinkId = "A1000000000000002", Url = "https://alpha.example/a", Title = "alpha", ListId = FolderA,
                VisitCount = 2, CreatedAt = t0.AddDays(2), UpdatedAt = t0.AddDays(3),
            },
            new Link
            {
                LinkId = "A1000000000000003", Url = "https://charlie.example/c", Title = "Charlie", ListId = FolderA,
                VisitCount = 2, LastVisitedAt = t0.AddDays(5), CreatedAt = t0, UpdatedAt = t0,
            },
            new Link
            {
                LinkId = "A1000000000000004", Url = "https://null-title.example/n", Title = null, ListId = FolderA,
                VisitCount = 0, CreatedAt = t0.AddDays(3), UpdatedAt = t0.AddDays(3),
            },
            new Link
            {
                // 与 #2 同名（NOCASE 并列）——排序稳定性必须靠 ID 兜底，两个路径的方向都随排序列
                LinkId = "A1000000000000005", Url = "https://alpha2.example/a", Title = "ALPHA", ListId = FolderA,
                VisitCount = 3, LastVisitedAt = t0.AddDays(5), CreatedAt = t0.AddDays(4), UpdatedAt = t0.AddDays(4),
            },
            new Link
            {
                LinkId = "A1000000000000006", Url = "https://zulu.example/z", Title = "zulu", ListId = FolderA,
                VisitCount = 1, CreatedAt = t0.AddDays(4), UpdatedAt = t0.AddDays(4),
            },
            new Link
            {
                LinkId = "A1000000000000007", Url = "https://root1.example/", Title = "root one", ListId = null,
                VisitCount = 4, CreatedAt = t0.AddDays(-1), UpdatedAt = t0.AddDays(-1),
            },
            new Link
            {
                LinkId = "A1000000000000008", Url = "https://root2.example/", Title = "root two", ListId = null,
                IsImportant = true, VisitCount = 4, LastVisitedAt = t0.AddDays(2),
                CreatedAt = t0.AddDays(-2), UpdatedAt = t0.AddDays(-2),
            });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("title")]
    [InlineData("url")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    [InlineData("last_visited_at")]
    [InlineData("visit_count")]
    [InlineData("is_important")]
    public async Task 目录页直读_与EF实体路径_逐字段一致(string field)
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await SeedAsync(factory);
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());

            foreach (var dir in new[] { SortDir.Asc, SortDir.Desc })
            foreach (var root in new[] { false, true })
            foreach (var page in new[] { new PageSpec(1, 3), new PageSpec(2, 3), new PageSpec(1, 0) })
            {
                var spec = new LinkQuerySpec
                {
                    Filter = root
                        ? new LinkFilter { Unfiled = true }
                        : new LinkFilter { FolderId = new FolderId(FolderA) },
                    Sort = [new SortSpec(field, dir)],
                    Page = page,
                };
                var direct = await uow.Links.ListDtosAsync(spec, default);
                var oracle = (await uow.Links.ListAsync(spec, default)).Select(l => l.ToDto()).ToList();
                AssertRowsEqual(oracle, direct);
            }
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 目录页直读_缺省排序与复杂过滤回退_同样一致()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await SeedAsync(factory);
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());

            // 缺省排序（Sort 空 = title 升序 + ID 兜底）
            var plain = new LinkQuerySpec { Filter = new LinkFilter { FolderId = new FolderId(FolderA) } };
            AssertRowsEqual(
                (await uow.Links.ListAsync(plain, default)).Select(l => l.ToDto()).ToList(),
                await uow.Links.ListDtosAsync(plain, default));

            // 复杂过滤（包含搜索）→ 直读不接手，结果仍与实体路径一致
            var complex = new LinkQuerySpec
            {
                Filter = new LinkFilter { FolderId = new FolderId(FolderA), TitleContains = "alp" },
                Sort = [new SortSpec("visit_count", SortDir.Desc)],
                Page = new PageSpec(1, 10),
            };
            AssertRowsEqual(
                (await uow.Links.ListAsync(complex, default)).Select(l => l.ToDto()).ToList(),
                await uow.Links.ListDtosAsync(complex, default));
        }
        finally { Cleanup(dbPath); }
    }

    private static void AssertRowsEqual(IReadOnlyList<LinkPocket.Contracts.LinkDto> expected,
        IReadOnlyList<LinkPocket.Contracts.LinkDto> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            Assert.Equal(e.LinkId, a.LinkId);
            Assert.Equal(e.Url, a.Url);
            Assert.Equal(e.Title, a.Title);
            Assert.Equal(e.Description, a.Description);
            Assert.Equal(e.FaviconUrl, a.FaviconUrl);
            Assert.Equal(e.ListId, a.ListId);
            Assert.Equal(e.LastVisitedAt, a.LastVisitedAt);
            Assert.Equal(e.VisitCount, a.VisitCount);
            Assert.Equal(e.IsImportant, a.IsImportant);
            Assert.Equal(e.CreatedAt, a.CreatedAt);
            Assert.Equal(e.UpdatedAt, a.UpdatedAt);
        }
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

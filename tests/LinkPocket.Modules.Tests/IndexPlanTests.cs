using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 索引覆盖复核（阶段 12，方案 7.2/7.3）：对<b>真实引擎建的库</b>跑 <c>EXPLAIN QUERY PLAN</c>，
/// 把「哪些查询形态必须走索引」固化成可执行断言——索引不是"加完就算"，要能被回归卡住。
///
/// <para>SQL 语句镜像仓储层的实际谓词：<c>EfLinkRepository.ApplyFilter / ListAsync</c>、
/// <c>EfTreeService.RecursiveLinkCountsAsync</c>、<c>EfTrashRepository</c>、<c>EfSortEngine</c> 下推的排序。
/// 预期值与阶段 12 的实测计划逐条对应（见 SchemaMigrator.IndexesV3 的复核说明）。</para>
///
/// <para>负向断言同样重要：<c>title LIKE '%x%'</c>（search.links 的中缀包含）<b>注定</b>全表扫描，
/// 这正是 7.3「10k 库 search.links &lt; 100ms」门槛的依据；把"已知可接受的全扫描"写进测试，
/// 才能让将来新增的意外全扫描无处藏身。</para>
/// </summary>
public class IndexPlanTests
{
    private static string Plan(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = cmd.ExecuteReader();
        var parts = new List<string>();
        while (reader.Read()) parts.Add(reader.GetString(3));   // 第 4 列 = 计划明细
        return string.Join(" | ", parts);
    }

    private static void WithFreshDb(Action<string> assert)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lpplan_{Guid.NewGuid():N}.db");
        try
        {
            SchemaMigrator.EnsureSchema(dbPath);
            assert(dbPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // —— 链接/回收站表：过滤器与排序键都必须命中列/复合索引 ——

    [Theory]
    // 按目录取直接子链接（folders.contents / links.list { folder_id }）
    [InlineData("SELECT id, folder_id, url FROM links WHERE folder_id = 'F1'", "idx_links_folder")]
    // 根级书签（folder_id IS NULL；links.query { folder_id, isnull }）
    [InlineData("SELECT id FROM links WHERE folder_id IS NULL", "idx_links_folder")]
    // 按目录分组计数（links.stats.ByFolder / folders 递归计数）
    [InlineData("SELECT folder_id, COUNT(*) FROM links WHERE folder_id IS NOT NULL GROUP BY folder_id", "idx_links_folder")]
    // 精确 URL（links.find_by_url / dedup 分组）
    [InlineData("SELECT id FROM links WHERE url = 'https://a.b/c'", "idx_links_url")]
    // URL 前缀（links.query { url, starts }）：必须靠 NOCASE 索引做范围查找，不能退化成全表扫描
    [InlineData(@"SELECT id FROM links WHERE url LIKE 'https://git%' ESCAPE '\'", "idx_links_url_nocase")]
    // 缺省排序（links.list 缺省 created_at DESC）+ 智能列表 recently_added
    [InlineData("SELECT id FROM links ORDER BY created_at DESC LIMIT 20", "idx_links_created")]
    [InlineData("SELECT id FROM links WHERE created_at >= '2026-01-01' ORDER BY created_at DESC LIMIT 50", "idx_links_created")]
    // 智能列表 recently_edited
    [InlineData("SELECT id FROM links WHERE updated_at >= '2026-01-01' ORDER BY updated_at DESC LIMIT 50", "idx_links_updated")]
    // 智能列表 recently_visited
    [InlineData("SELECT id FROM links WHERE last_visited_at >= '2026-01-01' ORDER BY last_visited_at DESC LIMIT 50", "idx_links_last_visited")]
    // 回收站平铺（trash.list：单独删除的书签按删除时间倒序）
    [InlineData("SELECT id FROM trash_links WHERE trash_folder_id IS NULL ORDER BY deleted_at DESC", "idx_trash_links_folder")]
    // 回收站单元树计数
    [InlineData("SELECT trash_folder_id, COUNT(*) FROM trash_links WHERE trash_folder_id IS NOT NULL GROUP BY trash_folder_id", "idx_trash_links_folder")]
    // 回收站单元全量排序（trash.tree；v3 补齐 EF 模型已声明、基线 DDL 漏建的索引）
    [InlineData("SELECT id FROM trash_folders ORDER BY deleted_at DESC", "idx_trash_folders_deleted")]
    public void Hot_Paths_Use_Index(string sql, string expectedIndex)
        => WithFreshDb(dbPath => Assert.Contains(expectedIndex, Plan(dbPath, sql)));

    /// <summary>缺省排序不得再走临时 B 树排序（v3 前实测 = <c>SCAN links | USE TEMP B-TREE FOR ORDER BY</c>）。</summary>
    [Fact]
    public void Default_Order_Does_Not_Use_Temp_BTree()
        => WithFreshDb(dbPath =>
            Assert.DoesNotContain("TEMP B-TREE", Plan(dbPath, "SELECT id FROM links ORDER BY created_at DESC LIMIT 20")));

    /// <summary>
    /// 负向复核：中缀包含（<c>%x%</c>）与低选择性等值（is_important）注定全表扫描，
    /// 这里显式记录「已知且接受」——它们的量级由 7.3 的 10k 门槛兜底，
    /// 而不是靠加一个不会生效的索引假装解决。
    /// </summary>
    [Theory]
    [InlineData(@"SELECT id FROM links WHERE title LIKE '%git%' ESCAPE '\'")]
    [InlineData(@"SELECT id FROM links WHERE url LIKE '%git%' ESCAPE '\'")]
    [InlineData("SELECT id FROM links WHERE is_important = 1")]
    public void Known_Acceptable_Full_Scans(string sql)
        => WithFreshDb(dbPath =>
        {
            var plan = Plan(dbPath, sql);
            Assert.Contains("SCAN", plan);                 // 确实没走索引
            Assert.DoesNotContain("TEMP B-TREE", plan);    // 但也不该额外付出排序代价
        });
}

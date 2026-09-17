using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// SchemaMigrator（方案 6.1/6.2）：从零建库（完整版本链）、版本脚本幂等、v2→v3 升级路径、旧库零责任拒绝。
/// 与 v1 无任何关系——不存在迁移路径，只有「全新库」与「拒绝旧库」两种结局。
/// EF 实体映射与 DDL 的一致性由 ModulesTests 全量黑盒回归（跑在 SchemaMigrator 建的库上）卡住，
/// 索引覆盖（查询计划）由 <see cref="IndexPlanTests"/> 卡住。
/// </summary>
public class SchemaMigratorTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"lpschema_{Guid.NewGuid():N}.db");

    private static string[] UserTables(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static int SchemaVersion(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>库内全部显式索引名（排序数组）——索引集合是阶段 12 复核后的可执行期望。</summary>
    private static string[] IndexNames(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    [Fact]
    public void EnsureSchema_Creates_Baseline_And_Applies_Evolution()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);

        Assert.Equal(
            new[] { "audit_log", "folders", "idempotency", "links", "macros", "schema_migrations", "trash_folders", "trash_links" },
            UserTables(dbPath));
        // 全新库跑的是完整版本链（v2 基线 + v3 索引复核），版本表落最高版本
        Assert.Equal(3, SchemaVersion(dbPath));

        // v2 关键形状抽查：主键统一 id、folders 无 link_count、根语义仅 NULL（无哨兵约束项）
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('folders') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('links') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('trash_links') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('trash_folders') WHERE pk = 1"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('folders') WHERE name = 'link_count'"));
            Assert.Equal(11, Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('links')")));
        }

        // v3（阶段 12 索引复核）：新建库路径必须与升级路径产出同一套索引
        Assert.Equal(
            new[]
            {
                "idx_folders_parent",
                "idx_links_created",
                "idx_links_folder",
                "idx_links_last_visited",
                "idx_links_updated",
                "idx_links_url",
                "idx_links_url_nocase",
                "idx_trash_folders_deleted",
                "idx_trash_folders_parent",
                "idx_trash_links_deleted",
                "idx_trash_links_folder",
            },
            IndexNames(dbPath));
    }

    /// <summary>
    /// v2 → v3 升级路径：既有库只补索引脚本，索引集合必须与新建库逐项一致
    /// （否则"老用户"永远拿不到性能修复）。
    /// </summary>
    [Fact]
    public void EnsureSchema_Upgrades_V2_Database_To_V3_Indexes()
    {
        var source = TempDbPath();
        SchemaMigrator.EnsureSchema(source);
        var expected = IndexNames(source);

        // 造一个"升级前"的库副本（版本行 = 2、无 v3 索引）：SchemaMigrator 从未见过该路径 → 走完整检查
        using (var conn = new SqliteConnection($"Data Source={source}"))
        {
            conn.Open();
            using var checkpoint = conn.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";   // 数据落主文件，副本才完整
            checkpoint.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var legacy = TempDbPath();
        File.Copy(source, legacy);
        using (var conn = new SqliteConnection($"Data Source={legacy}"))
        {
            conn.Open();
            foreach (var index in new[] { "idx_links_created", "idx_links_url_nocase", "idx_trash_folders_deleted" })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"DROP INDEX {index}";
                drop.ExecuteNonQuery();
            }
            using var rollback = conn.CreateCommand();
            rollback.CommandText = "DELETE FROM schema_migrations WHERE version = 3";
            rollback.ExecuteNonQuery();
        }

        SchemaMigrator.EnsureSchema(legacy);

        Assert.Equal(3, SchemaVersion(legacy));
        Assert.Equal(expected, IndexNames(legacy));
    }

    [Fact]
    public void EnsureSchema_Is_Idempotent()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);
        var tables = UserTables(dbPath);
        var version = SchemaVersion(dbPath);

        SchemaMigrator.EnsureSchema(dbPath);
        SchemaMigrator.EnsureSchema(dbPath);

        Assert.Equal(tables, UserTables(dbPath));   // 不重复建表、不改名
        Assert.Equal(version, SchemaVersion(dbPath)); // 版本不回退、不重复写版本行
    }

    [Fact]
    public void EnsureSchema_Rebuilds_After_File_Deleted()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);
        // 连接池会保留物理连接句柄（与 LinkPocketApi.ReinitializeDatabaseAsync 的 ClearPool 同因）
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);

        // 整库重置（删文件重建）路径：文件存在性守卫失效后自动重新建库
        SchemaMigrator.EnsureSchema(dbPath);
        Assert.Equal(3, SchemaVersion(dbPath));
        Assert.Equal(8, UserTables(dbPath).Length);
    }

    [Fact]
    public void EnsureSchema_Rejects_Legacy_Database()
    {
        var dbPath = TempDbPath();
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 模拟旧格式库：有用户表、无 schema_migrations 版本表
            cmd.CommandText = "CREATE TABLE lists (folder_id TEXT PRIMARY KEY, Name TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => SchemaMigrator.EnsureSchema(dbPath));
        Assert.Contains("不迁移", ex.Message);

        // 旧库文件必须原样留存（零责任：不删除、不触碰）
        Assert.Equal(new[] { "lists" }, UserTables(dbPath));
    }

    [Fact]
    public async Task Ef_Mapping_Works_On_Migrator_Created_Database()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);
        var factory = new LinkPocketDbContextFactory(dbPath);

        await using (var ctx = factory.CreateDbContext())
        {
            var folder = new Folder { Name = "映射对齐" };
            ctx.Add(folder);
            ctx.Add(new Link { Url = "https://example.com/", Title = "L", ListId = folder.FolderId });
            ctx.Add(new TrashedLink { Url = "https://trash.example/", OriginListId = folder.FolderId, DeletedAt = DateTime.UtcNow });
            ctx.Add(new TrashedFolder { Name = "单元", DeletedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = factory.CreateDbContext())
        {
            Assert.Equal(1, await verify.Folders.CountAsync());
            Assert.Equal(1, await verify.Links.CountAsync());
            Assert.Equal(1, await verify.TrashedLinks.CountAsync());
            Assert.Equal(1, await verify.TrashedFolders.CountAsync());
            Assert.Equal(3, await new EfUnitOfWork(verify).SchemaVersionAsync(default));
        }
    }

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }
}

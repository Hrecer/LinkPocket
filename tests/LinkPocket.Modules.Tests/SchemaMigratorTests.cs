using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// SchemaMigrator（方案 6.1/6.2）：从零建库、版本脚本幂等、旧库零责任拒绝。
/// 与 v1 无任何关系——不存在迁移路径，只有「全新 v2」与「拒绝旧库」两种结局。
/// EF 实体映射与 DDL 的一致性由 ModulesTests 全量黑盒回归（跑在 SchemaMigrator 建的库上）卡住。
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

    [Fact]
    public void EnsureSchema_Creates_V2_Baseline()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);

        Assert.Equal(
            new[] { "audit_log", "folders", "idempotency", "links", "macros", "schema_migrations", "trash_folders", "trash_links" },
            UserTables(dbPath));
        Assert.Equal(2, SchemaVersion(dbPath));

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
        Assert.Equal(2, SchemaVersion(dbPath));
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
            Assert.Equal(2, await new EfUnitOfWork(verify).SchemaVersionAsync(default));
        }
    }

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }
}

using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// SchemaMigrator：从零建库（完整版本链）、版本脚本幂等、v2→最新版升级路径、同层唯一名硬约束、旧库零责任拒绝。
/// 与 v1 无任何关系——不存在迁移路径，只有「全新库」与「拒绝旧库」两种结局。
/// EF 实体映射与 DDL 的一致性由 ModulesTests 全量黑盒回归（跑在 SchemaMigrator 建的库上）卡住，
/// 索引覆盖（查询计划）由 <see cref="IndexPlanTests"/> 卡住。
/// </summary>
public class SchemaMigratorTests
{
    private static string TempDbPath()
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpschema_{Guid.NewGuid():N}.db");

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

    /// <summary>库内全部显式索引名（排序数组）——索引集合是复核后的可执行期望。</summary>
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

    /// <summary>某表的全部列名（排序数组）——表形状可比对的期望（升级库 vs 新建库）。</summary>
    private static string[] ColumnNames(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY name";
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
        // 全新库跑的是完整版本链（v2 基线 + v3/v4/v5/v6 演进），版本表落最高版本
        Assert.Equal(7, SchemaVersion(dbPath));

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

        // v3/v4/v5/v6（索引复核 + 同层唯一约束 + 回收站保真列 + 审计补列与索引）：新建库与升级库必须同形
        Assert.Equal(
            new[]
            {
                "idx_audit_at",
                "idx_audit_correlation",
                "idx_folders_parent",
                "idx_folders_parent_name",
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

        // v5（回收站原位还原 + 快照保真）：trash_folders 共 12 列，6 个新增列齐备（v5 不加索引）
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Equal(12L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('trash_folders')"));
            Assert.Equal(6L, Scalar(conn,
                "SELECT COUNT(*) FROM pragma_table_info('trash_folders') WHERE name IN " +
                "('origin_parent_folder_id','description','sort_order','created_at','last_visited_at','visit_count')"));

            // v6（审计可读化）：audit_log 共 16 列（v2 基线 12 列 + v6 补 4 列）
            Assert.Equal(16L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('audit_log')"));
            Assert.Equal(4L, Scalar(conn,
                "SELECT COUNT(*) FROM pragma_table_info('audit_log') WHERE name IN " +
                "('dry_run','is_nested','stack_trace','args_truncated')"));
        }
    }

    /// <summary>
    /// v2 → 最新版升级路径：既有库只补增量脚本，索引集合与表形状必须与新建库逐项一致
    /// （否则"老用户"永远拿不到性能修复、同层唯一约束与回收站保真列）。
    /// </summary>
    [Fact]
    public void EnsureSchema_Upgrades_V2_Database_To_Latest_Indexes()
    {
        var source = TempDbPath();
        SchemaMigrator.EnsureSchema(source);
        var expected = IndexNames(source);
        var expectedTrashColumns = ColumnNames(source, "trash_folders");
        var expectedAuditColumns = ColumnNames(source, "audit_log");

        // 造一个"升级前"的库副本（版本行 = 2、无 v3..v6 演进）：SchemaMigrator 从未见过该路径 → 走完整检查
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
            foreach (var index in new[]
                     {
                         "idx_links_created", "idx_links_url_nocase", "idx_trash_folders_deleted",
                         "idx_folders_parent_name", "idx_audit_at", "idx_audit_correlation",
                     })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"DROP INDEX {index}";
                drop.ExecuteNonQuery();
            }
            // 回退 v5 列（模拟 v5 前进站的库形状）
            foreach (var column in new[]
                     {
                         "origin_parent_folder_id", "description", "sort_order",
                         "created_at", "last_visited_at", "visit_count",
                     })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"ALTER TABLE trash_folders DROP COLUMN {column}";
                drop.ExecuteNonQuery();
            }
            // 回退 v6 列（模拟审计补列前的库形状）
            foreach (var column in new[] { "dry_run", "is_nested", "stack_trace", "args_truncated" })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"ALTER TABLE audit_log DROP COLUMN {column}";
                drop.ExecuteNonQuery();
            }
            using var rollback = conn.CreateCommand();
            rollback.CommandText = "DELETE FROM schema_migrations WHERE version IN (3, 4, 5, 6, 7)";
            rollback.ExecuteNonQuery();
        }

        SchemaMigrator.EnsureSchema(legacy);

        Assert.Equal(7, SchemaVersion(legacy));
        Assert.Equal(expected, IndexNames(legacy));
        // 升级库与新建库的 trash_folders / audit_log 列集合逐项一致（v5/v6 ALTER 逐列补齐）
        Assert.Equal(expectedTrashColumns, ColumnNames(legacy, "trash_folders"));
        Assert.Equal(expectedAuditColumns, ColumnNames(legacy, "audit_log"));
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
        Assert.Equal(7, SchemaVersion(dbPath));
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
        Assert.Contains("does not migrate", ex.Message);

        // 旧库文件必须原样留存（零责任：不删除、不触碰）
        Assert.Equal(new[] { "lists" }, UserTables(dbPath));
    }

    [Fact]
    public async Task Ef_Mapping_Works_On_Migrator_Created_Database()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);
        var factory = new LinkPocketDbContextFactory(dbPath);

        var folder = new Folder { Name = "映射对齐" };
        var created = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Add(folder);
            ctx.Add(new Link { Url = "https://example.com/", Title = "L", ListId = folder.FolderId });
            ctx.Add(new TrashedLink { Url = "https://trash.example/", OriginListId = folder.FolderId, DeletedAt = DateTime.UtcNow });
            ctx.Add(new TrashedFolder
            {
                TrashFolderId = "900000000001",
                Name = "单元",
                OriginFolderId = "900000000001",
                OriginParentFolderId = folder.FolderId,
                OriginPath = "全部书签 / 映射对齐 / 单元",
                Description = "快照描述",
                SortOrder = 3,
                CreatedAt = created,
                LastVisitedAt = created,
                VisitCount = 7,
                DeletedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = factory.CreateDbContext())
        {
            Assert.Equal(1, await verify.Folders.CountAsync());
            Assert.Equal(1, await verify.Links.CountAsync());
            Assert.Equal(1, await verify.TrashedLinks.CountAsync());
            Assert.Equal(1, await verify.TrashedFolders.CountAsync());
            Assert.Equal(7, await new EfUnitOfWork(verify).SchemaVersionAsync(default));

            // v5 保真列经 EF 回读逐字段一致（DateTime? 往返按 Ticks 对齐，Kind 不参与比较）
            var unit = await verify.TrashedFolders.SingleAsync();
            Assert.Equal(folder.FolderId, unit.OriginParentFolderId);
            Assert.Equal("快照描述", unit.Description);
            Assert.Equal(3, unit.SortOrder);
            Assert.Equal(created.Ticks, unit.CreatedAt!.Value.Ticks);
            Assert.Equal(created.Ticks, unit.LastVisitedAt!.Value.Ticks);
            Assert.Equal(7, unit.VisitCount);
        }
    }

    /// <summary>
    /// v4 同层唯一名硬约束（表达式唯一索引）：同一父目录内（含根级）不允许重名（大小写不敏感），
    /// 不同父目录可同名。这是"路径解析不会歧义"的最后防线——绕过引擎编号直接写库也挡得住。
    /// </summary>
    [Fact]
    public void EnsureSchema_Enforces_Unique_Sibling_Folder_Names()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 根级 'Python' + 根级 'P' + P 下的 'Python'：不同父目录可同名 → 全部成功
        Exec(conn,
            "INSERT INTO folders (id, parent_id, name, sort_order, created_at, updated_at, visit_count) VALUES " +
            "('100000000001', NULL, 'Python', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 0)," +
            "('100000000002', NULL, 'P', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 0)," +
            "('100000000003', '100000000002', 'Python', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 0);");

        // 同一父目录重名（含大小写不同）→ 唯一约束拒绝
        var sameParent = Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO folders (id, parent_id, name, sort_order, created_at, updated_at, visit_count) " +
            "VALUES ('100000000004', NULL, 'python', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 0);"));
        Assert.Contains("UNIQUE", sameParent.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }
}

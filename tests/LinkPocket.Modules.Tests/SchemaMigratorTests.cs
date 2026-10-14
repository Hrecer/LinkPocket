using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// SchemaMigrator（4.0.0 起 **单一基线 + 纪元戳**）：
/// 从零建库（一次执行当前完整 DDL）、幂等、整库重建路径、同层唯一名硬约束、
/// **旧纪元（v8 及更早）/ 外来库 / 未来纪元的明确拒绝**（不迁移、不读取、不转换、不删除）。
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

    /// <summary>库头标记：`application_id` = 产品标识、`user_version` = 格式纪元。</summary>
    private static (int AppId, int UserVersion) HeaderMarkers(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA application_id;";
        var appId = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA user_version;";
        return (appId, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void 库头版本标记_产品标识与纪元戳都写上_外来库按标记拒绝()
    {
        // 备份是用户数据的跨版本迁移通道，库头标记是同一件事的库侧答案：
        // 不查任何表就能认出"这是哪个纪元、哪个产品的库"（任何工具都能读）。
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lphdr_{Guid.NewGuid():N}.db");
        string? restampedPath = null;
        string? foreignPath = null;
        try
        {
            SchemaMigrator.EnsureSchema(dbPath);
            var fresh = HeaderMarkers(dbPath);
            Assert.Equal(SchemaMigrator.ApplicationId, fresh.AppId);
            Assert.Equal(SchemaMigrator.EpochVersion, fresh.UserVersion);
            Assert.True(fresh.UserVersion > 0, "新建库的库头 user_version 必须写上（不许停在 0）");

            // 纪元相符但产品标记缺失（边角）：仍接受，并把标记补齐（换路径避开"已核验"短路）
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA application_id = 0;";
                cmd.ExecuteNonQuery();
            }
            restampedPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lphdr2_{Guid.NewGuid():N}.db");
            File.Copy(dbPath, restampedPath);
            SchemaMigrator.EnsureSchema(restampedPath);
            Assert.Equal(SchemaMigrator.ApplicationId, HeaderMarkers(restampedPath).AppId);

            // 带**别的** application_id 的库 = 外来文件 → 明确拒绝（不触碰文件）
            foreignPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpforeign_{Guid.NewGuid():N}.db");
            using (var conn = new SqliteConnection($"Data Source={foreignPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA application_id = 0x12345678; CREATE TABLE someone_elses (id INTEGER);";
                cmd.ExecuteNonQuery();
            }
            var ex = Assert.Throws<DatabaseIncompatibleException>(() => SchemaMigrator.EnsureSchema(foreignPath));
            Assert.Contains("foreign SQLite database", ex.Message);
            Assert.Equal(foreignPath, ex.DatabasePath);
            Assert.Equal(new[] { "someone_elses" }, UserTables(foreignPath));   // 原样留存
        }
        finally
        {
            // 先清连接池再删文件：池里空闲的物理连接会占住句柄（实测 IOException）
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, restampedPath, foreignPath })
                if (path != null && File.Exists(path)) File.Delete(path);
        }
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

    [Fact]
    public void EnsureSchema_Creates_Epoch_Baseline()
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);

        Assert.Equal(
            new[] { "folders", "links", "schema_migrations", "trash_folders", "trash_links" },
            UserTables(dbPath));
        // 单一基线：版本表落纪元行（9），不存在版本链
        Assert.Equal(SchemaMigrator.EpochVersion, SchemaVersion(dbPath));
        Assert.Equal(9, SchemaVersion(dbPath));

        // 关键形状抽查：主键统一 id、folders 无 link_count、根语义仅 NULL（无哨兵约束项）
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('folders') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('links') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('trash_links') WHERE pk = 1"));
            Assert.Equal("id", Scalar(conn, "SELECT name FROM pragma_table_info('trash_folders') WHERE pk = 1"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('folders') WHERE name = 'link_count'"));
            Assert.Equal(11, Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('links')")));

            // 用户库只放书签数据：操作记录两张表**不得**出现在用户库里（它们住在附属库）
            Assert.Equal(0L, Scalar(conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('audit_log','idempotency')"));

            // 回收站（原位还原 + 快照保真）：trash_folders 共 12 列，六个保真列齐备
            Assert.Equal(12L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('trash_folders')"));
            Assert.Equal(6L, Scalar(conn,
                "SELECT COUNT(*) FROM pragma_table_info('trash_folders') WHERE name IN " +
                "('origin_parent_folder_id','description','sort_order','created_at','last_visited_at','visit_count')"));
        }

        // 索引集合 = 定稿的完整清单（性能索引 + 同层唯一约束 + 回收站排序索引）
        Assert.Equal(
            new[]
            {
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
    }

    /// <summary>
    /// 旧纪元（v8 及更早）的库文件一律**明确拒绝**：不迁移、不读取、不转换、不删除（处置权归用户）。
    /// 迁移唯一通道 = 导出/导入（应用不做任何开机兼容）。>9 = 未来纪元（降级打开）同样拒绝。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    public void EnsureSchema_Rejects_NonEpoch_Databases(int userVersion)
    {
        var dbPath = TempDbPath();
        SchemaMigrator.EnsureSchema(dbPath);

        // 造一个"别的纪元"的库副本：改库头 user_version 后收敛 WAL（否则改动还在 -wal 里，副本不完整）
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {userVersion}; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // ⚠️ 必须换一个新路径打开：EnsureSchema 对**已核验过**的路径直接短路，同路径再跑测不到拒绝
        var other = TempDbPath();
        File.Copy(dbPath, other);
        try
        {
            var ex = Assert.Throws<DatabaseIncompatibleException>(() => SchemaMigrator.EnsureSchema(other));
            Assert.Contains("not compatible", ex.Message);
            Assert.Equal(other, ex.DatabasePath);
            // 被拒文件原样留存（零责任：不删除、不触碰）
            Assert.Equal(5, UserTables(other).Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, other })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EnsureSchema_Rejects_Unstamped_Legacy_Database()
    {
        var dbPath = TempDbPath();
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 模拟旧格式库：有用户表、无纪元戳（user_version = 0）
            cmd.CommandText = "CREATE TABLE lists (folder_id TEXT PRIMARY KEY, Name TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<DatabaseIncompatibleException>(() => SchemaMigrator.EnsureSchema(dbPath));
        Assert.Contains("not compatible", ex.Message);

        // 旧库文件必须原样留存（零责任：不删除、不触碰）
        Assert.Equal(new[] { "lists" }, UserTables(dbPath));
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
        // 连接池会保留物理连接句柄（与整库重置路径的 ClearPool 同因）
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);

        // 整库重置（删文件重建）路径：文件存在性守卫失效后自动重新建库
        SchemaMigrator.EnsureSchema(dbPath);
        Assert.Equal(SchemaMigrator.EpochVersion, SchemaVersion(dbPath));
        // 用户库只剩书签域数据（审计/幂等在附属库；宏在 macros.json 文件）
        Assert.Equal(5, UserTables(dbPath).Length);
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
                OriginPath = "@root/映射对齐/单元",
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
            Assert.Equal(SchemaMigrator.EpochVersion, await new EfUnitOfWork(verify).SchemaVersionAsync(default));
            Assert.Equal(9, await new EfUnitOfWork(verify).SchemaVersionAsync(default));

            // 回收站保真列经 EF 回读逐字段一致（DateTime? 往返按 Ticks 对齐，Kind 不参与比较）
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
    /// 同层唯一名硬约束（表达式唯一索引）：同一父目录内（含根级）不允许重名（大小写不敏感），
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using LinkPocket.Data;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// v7 迁移：<c>origin_path</c> 快照从"烙着界面语言的显示串"改成 canonical（<c>@root/A/B</c>）。
/// </summary>
/// <remarks>
/// <see cref="SchemaMigrator.EnsureSchema"/> 对已核验过的路径直接短路，所以"迁移后"与"再跑一遍"
/// 必须落在**不同的库文件**上——用同一文件测会静默空跑（看起来验了可重入，其实什么都没执行）。
/// </remarks>
public class CanonicalPathMigrationTests
{
    private static string TempDbPath(string tag)
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpcanon_{tag}_{Guid.NewGuid():N}.db");

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> OriginPaths(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(origin_path, '<null>') FROM trash_folders ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>复制库文件前先收敛 WAL（否则 -wal 里的内容不在主文件里，副本会缺最新变更）。</summary>
    private static string Clone(string from, string tag)
    {
        Exec(from, "PRAGMA wal_checkpoint(TRUNCATE)");
        var to = TempDbPath(tag);
        File.Copy(from, to);
        if (File.Exists(from + "-wal")) File.Copy(from + "-wal", to + "-wal", true);
        return to;
    }

    private static void Cleanup(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 临时文件交给系统清理 */ }
    }

    [Fact]
    public void 旧显示串快照改写成canonical_且对已改写的库再跑一次不变()
    {
        var seed = TempDbPath("seed");
        var migrated = string.Empty;
        var rerun = string.Empty;
        try
        {
            SchemaMigrator.EnsureSchema(seed);
            // 退回 v6 并塞回旧形态：根级 / 多层 / 断链 / 无快照 四种形状都要有
            Exec(seed, "DELETE FROM schema_migrations WHERE version = 7");
            Exec(seed, @"INSERT INTO trash_folders (id, name, origin_path, deleted_at) VALUES
                ('f1', '根级单元', '全部书签', '2026-01-01T00:00:00Z'),
                ('f2', '多层单元', '全部书签 / 工作 / 前端', '2026-01-01T00:00:00Z'),
                ('f3', '断链单元', '未知目录', '2026-01-01T00:00:00Z'),
                ('f4', '无快照', NULL, '2026-01-01T00:00:00Z');");

            migrated = Clone(seed, "run1");
            // 迁移前先验旧形态：断言不成立就说明"复制/回退版本"这一步没真把库做成 v6（用例空跑）
            Assert.Equal(new List<string> { "全部书签", "全部书签 / 工作 / 前端", "未知目录", "<null>" },
                OriginPaths(migrated));

            SchemaMigrator.EnsureSchema(migrated);
            var expected = new List<string> { "@root", "@root/工作/前端", "@unknown", "<null>" };
            Assert.Equal(expected, OriginPaths(migrated));

            // 再跑一遍（换文件绕开已核验短路）：已 canonical 的行不该被二次改写、也不该掉版本
            rerun = Clone(migrated, "run2");
            SchemaMigrator.EnsureSchema(rerun);
            Assert.Equal(expected, OriginPaths(rerun));
        }
        finally
        {
            Cleanup(seed);
            Cleanup(migrated);
            Cleanup(rerun);
        }
    }
}

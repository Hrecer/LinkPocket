using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 引擎侧 DbContext 工厂（方案 7.1 读池）：每次 CreateDbContext 产出短生命周期上下文，
/// 连接串复用；建工厂时对库文件一次性启用 WAL（journal_mode 为库级持久属性），
/// 每个连接经连接串启用 foreign_keys。WAL = SQLite 官方读写并发方案，
/// 导入大事务期间读者见旧快照——与现状"导入完成后才看到结果"的感知一致（行为等价）。
/// </summary>
public sealed class LinkPocketDbContextFactory : IDbContextFactory<LinkPocketDbContext>
{
    private readonly string _connectionString;

    public LinkPocketDbContextFactory(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder($"Data Source={dbPath}")
        {
            ForeignKeys = true,
        };
        _connectionString = builder.ToString();
        EnableWal(dbPath);
        // 首次使用直接创建全新 v2 库（方案 6.2：零责任，无迁移组件）；已是 v2+ 时幂等快速返回
        SchemaMigrator.EnsureSchema(dbPath);
    }

    public LinkPocketDbContext CreateDbContext()
        => new(contextPath: null, connectionStringOverride: _connectionString);
    /// <summary>一次性启用 WAL；已处于 WAL 时为幂等 no-op。</summary>
    private static void EnableWal(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder($"Data Source={dbPath}").ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
    }
}

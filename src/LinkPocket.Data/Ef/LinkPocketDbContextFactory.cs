using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 引擎侧 DbContext 工厂（读池）：每次 CreateDbContext 产出短生命周期上下文，
/// 连接串复用；建工厂时对库文件一次性启用 WAL（journal_mode 为库级持久属性），
/// 每个连接经连接串启用 foreign_keys。WAL = SQLite 官方读写并发方案，
/// 导入大事务期间读者见旧快照——与现状"导入完成后才看到结果"的感知一致（行为等价）。
/// </summary>
public sealed class LinkPocketDbContextFactory : IDbContextFactory<LinkPocketDbContext>
{
    /// <summary>
    /// 跨进程写争用的**忙等上限（秒）**：SQLite 是单写者——外部进程（CLI / 外部 Agent 经 MCP）
    /// 提交期间，本进程的写会拿到 <c>SQLITE_BUSY</c>。显式忙等 5s 重试，别依赖隐式默认
    /// （否则表现为"对方提交的那一瞬间，界面/命令行偶发报锁"）。超过上限就如实失败，不静默兜底。
    /// </summary>
    public const int BusyTimeoutSeconds = 5;

    private readonly string _connectionString;

    public LinkPocketDbContextFactory(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder($"Data Source={dbPath}")
        {
            ForeignKeys = true,
            DefaultTimeout = BusyTimeoutSeconds,
        };
        _connectionString = builder.ToString();
        EnableWal(dbPath);
        // 首次使用直接创建全新 v2 库（零责任，无迁移组件）；已是 v2+ 时幂等快速返回
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

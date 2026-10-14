using LinkPocket.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 附属库（操作记录库）的建立与打开：<c>&lt;库目录&gt;/&lt;库名&gt;-ops.db</c>。
///
/// <para><b>职责</b>：建/开附属库并保证其 schema（<c>audit_log</c> + <c>idempotency</c> +
/// <c>ops_schema_migrations</c>）。操作记录是**运行痕迹**而非用户数据——因此版本政策从简：
/// 库头 <c>user_version</c> / <c>application_id</c> 与当前实现不符 → **重建空库**
/// （只丢历史与 24h 去重，如实记日志），不做任何迁移（用户库的"纪元戳 + 封死"见 <see cref="SchemaMigrator"/>）。</para>
///
/// <para>附属库带自己的库头标记（<c>application_id = 'LPOP'</c>、<c>user_version</c> = 自己的版本），
/// 任何工具不查表就能认出"这是 LinkPocket 的操作记录库"。</para>
/// </summary>
public sealed class OpsDbContextFactory : IDbContextFactory<OpsDbContext>
{
    /// <summary>`PRAGMA application_id`：附属库的产品标识（4 字符码 <c>'LPOP'</c>）。</summary>
    public const int ApplicationId = 0x4C504F50;

    /// <summary>附属库自身的 schema 版本（与用户库纪元无关；不符即重建）。</summary>
    public const int SchemaVersion = 1;

    private static readonly object Gate = new();

    /// <summary>附属库文件名后缀（与主库同目录，名字由主库名派生）。</summary>
    private const string Suffix = "-ops.db";

    private readonly string _connectionString;

    public string OpsPath { get; }

    public OpsDbContextFactory(string mainDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainDbPath);
        var mainFull = Path.GetFullPath(mainDbPath);
        OpsPath = PathFor(mainFull);

        var dir = Path.GetDirectoryName(OpsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder($"Data Source={OpsPath}")
        {
            ForeignKeys = true,
            DefaultTimeout = LinkPocketDbContextFactory.BusyTimeoutSeconds,   // 跨进程写争用忙等（与主库同一口径）
        }.ToString();
        lock (Gate)
        {
            RebuildIfIncompatible();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            EnableWal(conn);
            EnsureSchema(conn);
            StampVersionMarkers(conn);
        }
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    public OpsDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OpsDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>主库路径 → 附属库路径（唯一推导点：测试清理与宿主回显都走它，避免两处各写一份）。</summary>
    public static string PathFor(string mainDbPath)
    {
        var full = Path.GetFullPath(mainDbPath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(full) + Suffix);
    }

    /// <summary>
    /// 删除一个库文件连同它的伴生文件：主库 + 附属库 + 各自的 <c>-wal</c>/<c>-shm</c>。
    /// 名字规则由本类拥有 → 删除也归这里（测试清理、脚本与将来的"整库重置"共用一份名单，
    /// 不再各处手抄后缀）。尽力而为：句柄滞留等失败一律吞掉（临时物交给系统清理，绝不打断调用方）。
    /// </summary>
    public static void DeleteDatabaseFiles(string mainDbPath)
    {
        var main = Path.GetFullPath(mainDbPath);
        var ops = PathFor(main);
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { main, ops })
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate)) File.Delete(candidate);
                }
                catch (IOException) { /* 连接池句柄滞留：交给系统清理 */ }
                catch (UnauthorizedAccessException) { /* 临时文件被占用/无权限：尽力而为，交由系统清理 */ }
            }
        }
    }

    private static void EnableWal(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 库头与当前实现不符（版本 / 身份）→ 删文件重建空库；只丢运行痕迹，如实记日志。
    /// 空文件与尚无表的新文件不在此列（直接走 <see cref="EnsureSchema"/> 建库）。
    /// </summary>
    private void RebuildIfIncompatible()
    {
        if (!File.Exists(OpsPath)) return;

        int version, appId;
        bool hasTables;
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            version = ReadUserVersion(conn);
            appId = ReadApplicationId(conn);
            if (version == SchemaVersion && (appId == 0 || appId == ApplicationId)) return;
            hasTables = HasTables(conn);
        }
        if (version == 0 && appId == 0 && !hasTables) return;   // 空文件 = 全新

        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { OpsPath, OpsPath + "-wal", OpsPath + "-shm" })
        {
            try
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 句柄滞留等失败不阻断（下面仍会走建库；失败已由随后的日志暴露）
            }
        }
        LpLog.Warn(
            $"operation-records database was rebuilt (version/identity mismatch): {OpsPath} "
            + $"(user_version={version}, application_id=0x{appId:X8})",
            category: "data.ops");
    }

    /// <summary>附属库 schema（幂等）。</summary>
    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ops_schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit_log (
              id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, session_id TEXT NULL,
              caller TEXT NOT NULL, command TEXT NOT NULL, args_json TEXT NULL, elapsed_ms INTEGER NOT NULL,
              success INTEGER NOT NULL, error_code TEXT NULL, changes_json TEXT NULL, batch_id TEXT NULL,
              correlation_id TEXT NOT NULL, dry_run INTEGER NOT NULL DEFAULT 0, is_nested INTEGER NOT NULL DEFAULT 0,
              stack_trace TEXT NULL, args_truncated INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS idx_audit_at ON audit_log(at);
            CREATE INDEX IF NOT EXISTS idx_audit_correlation ON audit_log(correlation_id);
            CREATE TABLE IF NOT EXISTS idempotency (key TEXT PRIMARY KEY, result_json TEXT NOT NULL, at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();

        using var seed = conn.CreateCommand();
        seed.CommandText = "INSERT OR IGNORE INTO ops_schema_migrations (version, applied_at) VALUES (@v, @at);";
        seed.Parameters.AddWithValue("@v", SchemaVersion);
        seed.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        seed.ExecuteNonQuery();
    }

    private static void StampVersionMarkers(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {SchemaVersion}; PRAGMA application_id = {ApplicationId};";
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ReadApplicationId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA application_id;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool HasTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}

using LinkPocket.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 附属库（操作记录库）的建立与打开：<c>&lt;库目录&gt;/&lt;库名&gt;-ops.db</c>。
///
/// <para><b>职责</b>：① 建/开附属库并保证其 schema（<c>audit_log</c> + <c>idempotency</c>）；
/// ② **一次性**把用户库里遗留的操作记录搬过来（v8 之前审计与幂等存在用户库中）——
/// 搬迁发生在用户库迁移 **之前**（见 <c>EngineComposer.Compose</c> 的装配顺序），
/// 因此用户库随后执行 v8 删表时数据已经安全落在附属库。</para>
///
/// <para><b>搬迁是版本无关的</b>：按 <c>PRAGMA table_info</c> 取主库实际存在的列再拼 SELECT，
/// 缺失的列（早期版本的 <c>audit_log</c> 没有 v6 的四列）按默认值补齐——不假设主库一定是最新版本。</para>
///
/// <para>附属库带自己的库头标记（<c>application_id = 'LPOP'</c>、<c>user_version</c> = 自己的版本），
/// 任何工具不查表就能认出"这是 LinkPocket 的操作记录库"。</para>
/// </summary>
public sealed class OpsDbContextFactory : IDbContextFactory<OpsDbContext>
{
    /// <summary>`PRAGMA application_id`：附属库的产品标识（4 字符码 <c>'LPOP'</c>）。</summary>
    public const int ApplicationId = 0x4C504F50;

    /// <summary>附属库自身的 schema 版本（与用户库版本链无关）。</summary>
    public const int SchemaVersion = 1;

    private static readonly object Gate = new();

    /// <summary>附属库文件名后缀（与主库同目录，名字由主库名派生）。</summary>
    private const string Suffix = "-ops.db";

    private readonly string _connectionString;

    public string OpsPath { get; }

    /// <summary>本次建库/打开是否搬迁了用户库里的遗留操作记录（供宿主回显与排障）。</summary>
    public bool MigratedLegacyRows { get; private set; }

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
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            EnableWal(conn);
            EnsureSchema(conn);
            MigratedLegacyRows = MoveLegacyRows(conn, mainFull);
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
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void EnableWal(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>附属库 schema（幂等）：审计表 = 用户库 v6 后的完整形态（含 dry_run / is_nested / stack_trace / args_truncated）。</summary>
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

    /// <summary>
    /// 把用户库里遗留的 <c>audit_log</c> / <c>idempotency</c> 搬到附属库（只在附属库对应表为空时做一次）。
    /// 用户库不存在 / 没有这两张表（新库、或 v8 之后）→ 什么都不做。
    /// </summary>
    private bool MoveLegacyRows(SqliteConnection ops, string mainDbPath)
    {
        if (!File.Exists(mainDbPath)) return false;
        if (CountRows(ops, "audit_log") > 0 || CountRows(ops, "idempotency") > 0) return false;

        var mainConnectionString = new SqliteConnectionStringBuilder($"Data Source={mainDbPath}").ToString();
        using var main = new SqliteConnection(mainConnectionString);
        main.Open();
        if (!TableExists(main, "audit_log") && !TableExists(main, "idempotency")) return false;

        var attached = false;
        try
        {
            using (var attach = main.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE '{OpsPath.Replace("'", "''")}' AS ops;";
                attach.ExecuteNonQuery();
            }
            attached = true;

            var moved = false;
            using (var copy = main.CreateCommand())
            {
                copy.CommandText = BuildAuditCopySql(main);
                if (copy.CommandText.Length > 0 && copy.ExecuteNonQuery() > 0) moved = true;
            }
            using (var copy = main.CreateCommand())
            {
                copy.CommandText =
                    "INSERT OR IGNORE INTO ops.idempotency (key, result_json, at) SELECT key, result_json, at FROM main.idempotency;";
                if (TableExists(main, "idempotency") && copy.ExecuteNonQuery() > 0) moved = true;
            }

            if (moved)
            {
                LpLog.Info($"moved legacy operation records to {OpsPath}", category: "data.ops");
            }
            return moved;
        }
        catch (SqliteException ex)
        {
            // 观测面纪律：搬迁失败必须暴露（如实报日志），但不阻断启动——操作记录不是用户数据
            LpLog.Warn($"failed to move legacy operation records to {OpsPath}", ex, category: "data.ops");
            return false;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    using var detach = main.CreateCommand();
                    detach.CommandText = "DETACH DATABASE ops;";
                    detach.ExecuteNonQuery();
                }
                catch (SqliteException) { /* 连接即将释放，DETACH 失败无副作用 */ }
            }
        }
    }

    /// <summary>
    /// 拼审计搬迁 SQL：目标列固定（附属库定义），来源列按主库**实际存在**的列取——
    /// 缺失列用与建表默认值一致的字面量补齐（早期 schema 的 audit_log 没有 v6 四列）。
    /// </summary>
    private static string BuildAuditCopySql(SqliteConnection main)
    {
        if (!TableExists(main, "audit_log")) return string.Empty;

        var present = Columns(main, "audit_log");
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dry_run"] = "0", ["is_nested"] = "0", ["stack_trace"] = "NULL", ["args_truncated"] = "0",
        };
        string[] target =
        [
            "id", "at", "session_id", "caller", "command", "args_json", "elapsed_ms", "success",
            "error_code", "changes_json", "batch_id", "correlation_id", "dry_run", "is_nested",
            "stack_trace", "args_truncated",
        ];

        // id 与几个 NOT NULL 列是 v2 基线就有的；只要缺其中任何一列就整体放弃搬迁（不猜形状）
        string[] mandatory = ["id", "at", "caller", "command", "elapsed_ms", "success", "correlation_id"];
        if (mandatory.Any(c => !present.Contains(c))) return string.Empty;

        var select = target.Select(c => present.Contains(c) ? c : defaults.GetValueOrDefault(c, "NULL"));
        return $"INSERT OR IGNORE INTO ops.audit_log ({string.Join(", ", target)}) "
             + $"SELECT {string.Join(", ", select)} FROM main.audit_log;";
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;";
        cmd.Parameters.AddWithValue("@n", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static long CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static HashSet<string> Columns(SqliteConnection conn, string table)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }
}

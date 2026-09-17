using Microsoft.Data.Sqlite;

namespace LinkPocket.Data;

/// <summary>
/// schema v2 建库与版本演进（方案 6.1/6.2，零责任定稿）：
///
/// <para><b>全新建库</b>：首次使用（库文件不存在或为空）时在单个事务内执行 v2 基线 DDL
/// 并写入 <c>schema_migrations(version=2)</c>。全库表/列/索引 = 方案 6.1 逐字定稿
/// （lists→folders、list_id→folder_id、哨兵 "0" 不存在、主键统一 id、根 = NULL）。</para>
///
/// <para><b>版本表</b>：<c>schema_migrations</c> 仅服务 v2 之后的<b>内部常规演进</b>
/// （新增列/索引/表时追加版本脚本，按版本号顺序应用、幂等跳过已应用版本），
/// 与任何历史库无任何关系。</para>
///
/// <para><b>旧数据零责任</b>：不存在 v1→v2 迁移器。检测到「有用户表但无版本表」的库
/// （旧格式或未知格式）时直接抛出并拒绝使用——不迁移、不读取、不转换、不删除，
/// 处置权完全归用户。也不存在任何外挂兼容组件。</para>
///
/// <para><b>调用时机</b>：引擎侧 = <see cref="LinkPocketDbContextFactory"/> 构造；
/// 旧协议侧 = LinkPocketApi 构造与整库重置。<see cref="EnsureSchema"/> 幂等：
/// 已是 v2+ 的库快速返回（进程内缓存 + 文件存在性守卫，库文件被删除后自动重建）。
/// PRAGMA 不在 DDL 内：journal_mode=WAL 由引擎工厂一次性启用（库级属性）、
/// foreign_keys 走连接串（连接级）；主应用直连路径保持既有连接行为。</para>
/// </summary>
public static class SchemaMigrator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, bool> _verified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>确保 dbPath 指向的库处于 v2+ schema；空库建 v2，旧格式库抛出（零责任）。</summary>
    public static void EnsureSchema(string dbPath)
    {
        var key = Path.GetFullPath(dbPath);
        lock (_verified)
        {
            // 库文件被外部删除（如整库重置）后守卫失效，重新走完整检查
            if (_verified.TryGetValue(key, out var ok) && ok && File.Exists(key)) return;
        }

        Gate.Wait();
        try
        {
            var connString = new SqliteConnectionStringBuilder($"Data Source={key}").ToString();
            using (var conn = new SqliteConnection(connString))
            {
                conn.Open();

                var existingVersion = ReadSchemaVersion(conn);
                if (existingVersion is { } version)
                {
                    ApplyPending(conn, version);
                }
                else if (HasUserTables(conn))
                {
                    throw new InvalidOperationException(
                        $"检测到非 v2 格式数据库「{key}」。新架构不迁移、不读取、不转换任何旧数据（零责任），" +
                        "请自行删除或移走该文件后重试。");
                }
                else
                {
                    CreateBaseline(conn);
                }
            }

            // 释放池中空闲的物理连接（否则文件句柄滞留，整库重置的 File.Delete 会报占用）
            SqliteConnection.ClearPool(new SqliteConnection(connString));

            lock (_verified) _verified[key] = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>读取 schema_migrations 的当前版本；表不存在返回 null。</summary>
    private static int? ReadSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return null;

        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>库内是否存在业务表（除 schema_migrations 外的任何用户表 = 旧格式/未知格式）。</summary>
    private static bool HasUserTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite_%' AND name <> 'schema_migrations'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>应用当前版本之后的所有版本脚本（当前仅 v2 基线；后续演进追加到 Scripts）。</summary>
    private static void ApplyPending(SqliteConnection conn, int currentVersion)
    {
        foreach (var (version, sql) in Scripts)
        {
            if (version <= currentVersion) continue;
            ExecuteInTransaction(conn, sql);
        }
    }

    /// <summary>空库：建 v2 基线（DDL + 版本行，同一事务——中途失败不留半成品库）。</summary>
    private static void CreateBaseline(SqliteConnection conn)
        => ExecuteInTransaction(conn, Scripts[0].Sql + BaselineVersionRow);

    private static void ExecuteInTransaction(SqliteConnection conn, string sql)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>版本脚本表（方案 6.2：schema_migrations 仅服务 v2 内部常规演进）。</summary>
    private static readonly (int Version, string Sql)[] Scripts =
    [
        (2, BaselineV2),
    ];

    /// <summary>建库时写入的基线版本行（v2，applied_at = 建库时刻 UTC）。</summary>
    private static string BaselineVersionRow =>
        "INSERT INTO schema_migrations (version, applied_at) VALUES (2, '" +
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "');";

    /// <summary>
    /// v2 基线 DDL（方案 6.1 逐字）。与 EF 实体映射的一致性由 Modules.Tests 全量黑盒回归卡住。
    /// </summary>
    private const string BaselineV2 =
        """
        CREATE TABLE folders (
          id TEXT PRIMARY KEY, parent_id TEXT NULL REFERENCES folders(id) ON DELETE RESTRICT,
          name TEXT NOT NULL, description TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          last_visited_at TEXT NULL, visit_count INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX idx_folders_parent ON folders(parent_id);

        CREATE TABLE links (
          id TEXT PRIMARY KEY, folder_id TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
          url TEXT NOT NULL, title TEXT NULL, description TEXT NULL, favicon_url TEXT NULL,
          is_important INTEGER NOT NULL DEFAULT 0, visit_count INTEGER NOT NULL DEFAULT 0,
          last_visited_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX idx_links_folder ON links(folder_id);
        CREATE INDEX idx_links_url ON links(url);
        CREATE INDEX idx_links_last_visited ON links(last_visited_at);
        CREATE INDEX idx_links_updated ON links(updated_at);

        CREATE TABLE trash_folders (id TEXT PRIMARY KEY, parent_id TEXT NULL, name TEXT NOT NULL,
          origin_folder_id TEXT NULL, origin_path TEXT NULL, deleted_at TEXT NOT NULL);
        CREATE TABLE trash_links (id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NULL,
          description TEXT NULL, favicon_url TEXT NULL, trash_folder_id TEXT NULL,
          origin_folder_id TEXT NULL, origin_path TEXT NULL, last_visited_at TEXT NULL,
          visit_count INTEGER NOT NULL DEFAULT 0, is_important INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NOT NULL);
        CREATE INDEX idx_trash_links_deleted ON trash_links(deleted_at);
        CREATE INDEX idx_trash_links_folder ON trash_links(trash_folder_id);
        CREATE INDEX idx_trash_folders_parent ON trash_folders(parent_id);

        CREATE TABLE macros (name TEXT PRIMARY KEY, script_json TEXT NOT NULL,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL,
          session_id TEXT NULL, caller TEXT NOT NULL, command TEXT NOT NULL, args_json TEXT NULL,
          elapsed_ms INTEGER NOT NULL, success INTEGER NOT NULL, error_code TEXT NULL,
          changes_json TEXT NULL, batch_id TEXT NULL, correlation_id TEXT NOT NULL);
        CREATE TABLE idempotency (key TEXT PRIMARY KEY, result_json TEXT NOT NULL, at TEXT NOT NULL);
        CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
        """;
}

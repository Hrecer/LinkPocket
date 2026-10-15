using LinkPocket.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkPocket.Data;

/// <summary>
/// 建库与库格式政策（**单一基线 + 纪元戳**，4.0.0 起）：
///
/// <para><b>单一基线</b>：没有任何库文件 → 单个事务一次执行<b>当前完整 DDL</b>（<see cref="BaselineDdl"/>，
/// 即书签域终态逐字：folders / links / trash_folders / trash_links / schema_migrations；
/// 宏 4.0.0 起是文件 <c>data\macros.json</c>，**不在库里**），
/// 并给库头打标记。<b>不存在任何版本链 / 迁移脚本</b>——历史脚本（v2–v8）已全部删除，永不再有；
/// 库结构变更由"更新 = 完全重新安装 + 导出/导入"天然吸收（见 内部资产/文档/UPDATE.md）。</para>
///
/// <para><b>纪元戳（封死）</b>：库头 <c>user_version</c> = <see cref="SchemaEpoch.EpochVersion"/>（9）是**纪元标记**——
/// 只有本纪元的库被接受；<b>v8 及更早的库文件一律拒绝</b>（含手工直搬），迁移唯一通道 = 导出/导入。
/// 带别的 <c>application_id</c> 的外来库同样拒绝。拒绝 = 抛 <see cref="DatabaseIncompatibleException"/>，
/// 不触碰被拒文件（不迁移、不读取、不转换、不删除）。</para>
///
/// <para><b>调用时机</b>：引擎侧 = <see cref="LinkPocketDbContextFactory"/> 构造；
/// <see cref="EnsureSchema"/> 幂等：已核验路径快速返回（进程内缓存 + 文件存在性守卫，
/// 库文件被删除后自动重建——整库重置路径依赖这条）。PRAGMA 不在 DDL 内：
/// journal_mode=WAL 由引擎工厂一次性启用（库级属性）、foreign_keys 走连接串（连接级）。</para>
/// </summary>
public static class SchemaMigrator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, bool> _verified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>`PRAGMA application_id`：LinkPocket 的库头产品标识（4 字符码 <c>'LPPK'</c>）。</summary>
    public const int ApplicationId = 0x4C50504B;

    /// <summary>确保 dbPath 指向的库处于当前纪元：空库建基线；异纪元 / 外来库抛 <see cref="DatabaseIncompatibleException"/>。</summary>
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

                var foreignAppId = ReadApplicationId(conn);
                if (foreignAppId != 0 && foreignAppId != ApplicationId)
                {
                    throw new DatabaseIncompatibleException(
                        $"Detected a foreign SQLite database: '{key}' (application_id=0x{foreignAppId:X8}, "
                        + $"the LinkPocket marker is 0x{ApplicationId:X8}). LinkPocket never reads, converts or "
                        + "rewrites a database it did not create (zero responsibility); move that file yourself and retry.",
                        key);
                }

                var userVersion = ReadUserVersion(conn);
                if (userVersion == SchemaEpoch.EpochVersion)
                {
                    // 本纪元的库：接受（幂等；标记齐全时零写入）
                    StampVersionMarkers(conn);
                    EnsureHotIndexes(conn);   // 老库补齐后加的性能索引（幂等；见 HotIndexDdl 说明）
                }
                else if (userVersion != 0 || HasUserTables(conn))
                {
                    // 旧纪元（v8 及更早）/ 未来纪元（降级打开）/ 无标记但带表（未知格式）→ 一律拒绝。
                    // 被拒文件原样留存（零责任：不迁移、不读取、不转换、不删除）。
                    throw new DatabaseIncompatibleException(
                        $"This data file is not compatible with the current LinkPocket database format: '{key}' "
                        + $"(user_version={userVersion}, current epoch is {SchemaEpoch.EpochVersion}). "
                        + "LinkPocket does not migrate, read or convert it. Export a backup with the version that "
                        + "created it, then import that backup here. The file itself is left untouched.",
                        key);
                }
                else
                {
                    CreateBaseline(conn);
                    StampVersionMarkers(conn);
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

    /// <summary>
    /// **库头版本标记**（SQLite 原生字段，不进 DDL）：`user_version` = 格式纪元、`application_id` = 产品标识
    /// ——任何工具（`sqlite3` / 第三方库 / 更新器）不查任何表就能认出"这是哪个纪元、哪个产品的库"。
    /// 幂等：已经等于目标值就不写。
    /// </summary>
    public static void StampVersionMarkers(SqliteConnection conn)
    {
        if (ReadUserVersion(conn) != SchemaEpoch.EpochVersion)
            ExecutePragma(conn, $"PRAGMA user_version = {SchemaEpoch.EpochVersion};");
        if (ReadApplicationId(conn) != ApplicationId)
            ExecutePragma(conn, $"PRAGMA application_id = {ApplicationId};");
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

    private static void ExecutePragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 目录页**排序键复合索引**（2026-10-15 性能加固）：`WHERE folder_id=? ORDER BY &lt;列&gt;[, id] LIMIT n`
    /// 是目录页每一帧的取数形态，缺匹配索引时 SQLite 要取回该目录**全部**行再排序——
    /// 100K 库实测单页 43ms（9 万条目录），命中索引后 0.1ms 级。
    ///
    /// <para><b>为什么能"老库补建"**而不升纪元**</b>：索引是纯性能结构——不改变表形状、列语义与数据字节，
    /// 同纪元的库读写行为完全一致（缺索引只是慢）。故走 <c>CREATE INDEX IF NOT EXISTS</c> 幂等补齐
    /// （一次性成本：100K 行 ≈ 数百毫秒，之后开库零开销）；表结构本身的任何变化仍然只能靠
    /// 纪元闸 + 导出/导入（零兼容红线不变）。本表与 <see cref="BaselineDdl"/> 里的索引语句**逐字一致**
    /// （由测试 `SchemaMigratorTests` 卡住，防止两处漂移）。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> HotIndexDdl =
    [
        "CREATE INDEX IF NOT EXISTS idx_links_folder_title ON links(folder_id, title COLLATE NOCASE, id);",
        "CREATE INDEX IF NOT EXISTS idx_links_folder_created ON links(folder_id, created_at, id);",
        "CREATE INDEX IF NOT EXISTS idx_links_folder_updated ON links(folder_id, updated_at, id);",
        "CREATE INDEX IF NOT EXISTS idx_links_folder_visits ON links(folder_id, visit_count, id);",
        "CREATE INDEX IF NOT EXISTS idx_links_folder_lastvisit ON links(folder_id, last_visited_at IS NULL, last_visited_at, id);",
        // last_visited 倒序时「为空恒排最后」的前置判空子句仍是 ASC → 方向混合，正向索引服务不了，另建一支
        "CREATE INDEX IF NOT EXISTS idx_links_folder_lastvisit_desc ON links(folder_id, last_visited_at IS NULL, last_visited_at DESC, id DESC);",
    ];

    /// <summary>补齐热索引（幂等；同纪元老库开库时调用，见 <see cref="HotIndexDdl"/>）。</summary>
    internal static void EnsureHotIndexes(SqliteConnection conn)
    {
        foreach (var ddl in HotIndexDdl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>库内是否存在业务表（除 sqlite 内部表外的任何用户表 = 已有内容的库文件）。</summary>
    private static bool HasUserTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// 空库：单事务执行当前完整 DDL + 纪元版本行——中途失败不留半成品库。
    /// </summary>
    private static void CreateBaseline(SqliteConnection conn)
        => ExecuteInTransaction(conn, BaselineDdl + VersionRow(SchemaEpoch.EpochVersion));

    private static void ExecuteInTransaction(SqliteConnection conn, string sql)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>版本行（applied_at = 执行时刻 UTC）。单一基线下只写纪元这一行。</summary>
    private static string VersionRow(int version) =>
        $"INSERT INTO schema_migrations (version, applied_at) VALUES ({version}, '" +
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "');";

    /// <summary>
    /// 当前完整 DDL（**单一基线，逐字**：4.0.0 起 = 原 v8 终态的结构，无任何后续脚本）。
    /// 与 EF 实体映射的一致性由 Modules.Tests 全量黑盒回归卡住，索引覆盖（查询计划）由 IndexPlanTests 卡住。
    ///
    /// <list type="bullet">
    /// <item><c>idx_links_created</c>：<c>links.list</c> 缺省排序与 <c>recently_added</c> 的键
    /// （加之前 10k 全表 + 临时排序）。</item>
    /// <item><c>idx_links_url_nocase</c>：<c>url starts</c> 前缀 LIKE 的键；<b>不替换</b> <c>idx_links_url</c>
    /// ——<c>url = ?</c>（BINARY 语义）在只有 NOCASE 索引时退化全表扫，两条并存、各司其职。</item>
    /// <item><c>idx_folders_parent_name</c>：同层唯一名的硬约束（表达式唯一索引：<c>COALESCE(parent_id,'')</c>
    /// 把根归一到同一键；<c>NOCASE</c> 比命名策略更宽松，策略只会更严格，不存在"策略放行、索引拒绝"）。</item>
    /// <item><c>idx_trash_folders_deleted</c>：<c>trash.list / trash.tree</c> 按 <c>deleted_at</c> 倒序。</item>
    /// </list>
    /// </summary>
    private const string BaselineDdl =
        """
        CREATE TABLE folders (
          id TEXT PRIMARY KEY, parent_id TEXT NULL REFERENCES folders(id) ON DELETE RESTRICT,
          name TEXT NOT NULL, description TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          last_visited_at TEXT NULL, visit_count INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX idx_folders_parent ON folders(parent_id);
        CREATE UNIQUE INDEX idx_folders_parent_name ON folders(COALESCE(parent_id, ''), name COLLATE NOCASE);

        CREATE TABLE links (
          id TEXT PRIMARY KEY, folder_id TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
          url TEXT NOT NULL, title TEXT NULL, description TEXT NULL, favicon_url TEXT NULL,
          is_important INTEGER NOT NULL DEFAULT 0, visit_count INTEGER NOT NULL DEFAULT 0,
          last_visited_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX idx_links_folder ON links(folder_id);
        CREATE INDEX idx_links_url ON links(url);
        CREATE INDEX idx_links_last_visited ON links(last_visited_at);
        CREATE INDEX idx_links_updated ON links(updated_at);
        CREATE INDEX idx_links_created ON links(created_at);
        CREATE INDEX idx_links_url_nocase ON links(url COLLATE NOCASE);
        CREATE INDEX idx_links_folder_title ON links(folder_id, title COLLATE NOCASE, id);
        CREATE INDEX idx_links_folder_created ON links(folder_id, created_at, id);
        CREATE INDEX idx_links_folder_updated ON links(folder_id, updated_at, id);
        CREATE INDEX idx_links_folder_visits ON links(folder_id, visit_count, id);
        CREATE INDEX idx_links_folder_lastvisit ON links(folder_id, last_visited_at IS NULL, last_visited_at, id);
        CREATE INDEX idx_links_folder_lastvisit_desc ON links(folder_id, last_visited_at IS NULL, last_visited_at DESC, id DESC);

        CREATE TABLE trash_folders (id TEXT PRIMARY KEY, parent_id TEXT NULL, name TEXT NOT NULL,
          origin_folder_id TEXT NULL, origin_path TEXT NULL, deleted_at TEXT NOT NULL,
          origin_parent_folder_id TEXT NULL, description TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NULL, last_visited_at TEXT NULL, visit_count INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX idx_trash_folders_parent ON trash_folders(parent_id);
        CREATE INDEX idx_trash_folders_deleted ON trash_folders(deleted_at);

        CREATE TABLE trash_links (id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NULL,
          description TEXT NULL, favicon_url TEXT NULL, trash_folder_id TEXT NULL,
          origin_folder_id TEXT NULL, origin_path TEXT NULL, last_visited_at TEXT NULL,
          visit_count INTEGER NOT NULL DEFAULT 0, is_important INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NOT NULL);
        CREATE INDEX idx_trash_links_deleted ON trash_links(deleted_at);
        CREATE INDEX idx_trash_links_folder ON trash_links(trash_folder_id);

        CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
        """;
}

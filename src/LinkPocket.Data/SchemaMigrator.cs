using Microsoft.Data.Sqlite;

namespace LinkPocket.Data;

/// <summary>
/// schema v2 建库与版本演进（零责任定稿）：
///
/// <para><b>全新建库</b>：首次使用（库文件不存在或为空）时在单个事务内执行<b>完整版本链</b>
/// （v2 基线 DDL + 全部演进脚本，见 <c>Scripts</c>）并逐版本写入 <c>schema_migrations</c>。
/// 全库表/列/索引 = 逐字定稿（lists→folders、list_id→folder_id、哨兵 "0" 不存在、
/// 主键统一 id、根 = NULL）+ 索引复核追加的 v3 索引。</para>
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

                var foreignAppId = ReadApplicationId(conn);
                if (foreignAppId != 0 && foreignAppId != ApplicationId)
                {
                    throw new InvalidOperationException(
                        $"Detected a foreign SQLite database: '{key}' (application_id=0x{foreignAppId:X8}, "
                        + $"the LinkPocket marker is 0x{ApplicationId:X8}). LinkPocket never reads, converts or "
                        + "rewrites a database it did not create (zero responsibility); move that file yourself and retry.");
                }

                var existingVersion = ReadSchemaVersion(conn);
                if (existingVersion is { } version)
                {
                    ApplyPending(conn, version);
                    StampVersionMarkers(conn);
                }
                else if (HasUserTables(conn))
                {
                    throw new InvalidOperationException(
                        $"Detected a database in pre-v2 format: '{key}'. The new schema does not migrate, read or convert any legacy data (zero responsibility);" +
                        "delete or move that file yourself and retry.");
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
    /// **库头版本标记**（SQLite 原生字段，不进 DDL）：`user_version` = 当前 schema 版本、
    /// `application_id` = 产品标识 —— 任何工具（`sqlite3` / 第三方库 / 未来的升级器）不查任何表
    /// 就能认出"这是哪个版本、哪个产品的库"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的理由：库里的 <c>schema_migrations</c> 是**内部**版本链，只有本应用会去读；
    /// 而"用户手里这个 .db 是什么版本、该由哪个版本的应用接手"必须在**库文件本身**上答得出来
    /// （备份是用户数据的迁移通道，库头标记是同一件事的库侧答案）。
    /// </para>
    /// <para>
    /// 幂等：已经等于目标值就不写（新建库 / 每次升级后写一次；老库首次打开时补一次）。
    /// </para>
    /// </remarks>
    public static void StampVersionMarkers(SqliteConnection conn)
    {
        if (ReadUserVersion(conn) != CurrentSchemaVersion)
            ExecutePragma(conn, $"PRAGMA user_version = {CurrentSchemaVersion};");
        if (ReadApplicationId(conn) != ApplicationId)
            ExecutePragma(conn, $"PRAGMA application_id = {ApplicationId};");
    }

    /// <summary>`PRAGMA application_id`：LinkPocket 的库头产品标识（4 字符码 <c>'LPPK'</c>）。</summary>
    /// <remarks>带**别的** application_id 的库 = 外来文件 → 明确拒绝（与"有用户表但无版本表"同一条零责任口径）。</remarks>
    public const int ApplicationId = 0x4C50504B;

    /// <summary>当前 schema 版本（= 版本链顶端）。</summary>
    public static int CurrentSchemaVersion => Scripts[^1].Version;

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

    /// <summary>应用当前版本之后的所有版本脚本（后续演进一律追加到 Scripts 末尾）。</summary>
    private static void ApplyPending(SqliteConnection conn, int currentVersion)
    {
        foreach (var (version, sql) in Scripts)
        {
            if (version <= currentVersion) continue;
            ExecuteInTransaction(conn, sql);
        }
    }

    /// <summary>
    /// 空库：一次性跑完整版本链（基线 DDL + 全部演进脚本），同一事务——中途失败不留半成品库。
    /// 每条脚本自带版本行 → 新建库与升级库共用同一份脚本，不存在「只对新库生效」的隐性差异。
    /// </summary>
    private static void CreateBaseline(SqliteConnection conn)
        => ExecuteInTransaction(conn, string.Join("\n", Scripts.Select(s => s.Sql)));

    private static void ExecuteInTransaction(SqliteConnection conn, string sql)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>版本脚本表（schema_migrations 仅服务 v2 之后的内部常规演进）。</summary>
    private static (int Version, string Sql)[] Scripts =>
    [
        (2, BaselineV2 + VersionRow(2)),
        (3, IndexesV3 + VersionRow(3)),
        (4, IndexesV4 + VersionRow(4)),
        (5, AdditionsV5 + VersionRow(5)),
        (6, AdditionsV6 + VersionRow(6)),
        (7, CanonicalPathV7 + VersionRow(7)),
    ];

    /// <summary>版本行（applied_at = 执行时刻 UTC）。</summary>
    private static string VersionRow(int version) =>
        $"INSERT INTO schema_migrations (version, applied_at) VALUES ({version}, '" +
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "');";

    /// <summary>
    /// v2 基线 DDL（逐字）。与 EF 实体映射的一致性由 Modules.Tests 全量黑盒回归卡住。
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

    /// <summary>
    /// v3 版本脚本（索引复核）：<b>只加索引，不动表/列</b>——三个候选都是
    /// 用 <c>EXPLAIN QUERY PLAN</c> 在实测计划里定位出来的空缺，不是凭感觉加的：
    ///
    /// <list type="bullet">
    /// <item><c>idx_links_created</c>：<c>links.list</c> 缺省排序（created_at DESC）与
    /// <c>links.smart_list recently_added</c> 的键。加之前计划是 <c>SCAN links | USE TEMP B-TREE FOR ORDER BY</c>
    /// （10k 全表 + 临时排序），加之后为 <c>SCAN links USING INDEX idx_links_created</c>（有序扫描，零临时排序）。</item>
    /// <item><c>idx_links_url_nocase</c>：<c>links.query { url, starts }</c> 的前缀 LIKE 走不到
    /// <c>idx_links_url</c>（BINARY 索引对默认大小写不敏感的 LIKE 无效，实测 <c>SCAN links</c>）；
    /// NOCASE 索引把该条件变成范围 SEARCH。注意<b>不能</b>用它替换 <c>idx_links_url</c>：
    /// 实测 <c>url = ?</c>（BINARY 语义）在只有 NOCASE 索引时退化为全表 SCAN，故两条并存、各司其职。</item>
    /// <item><c>idx_trash_folders_deleted</c>：<c>trash.list / trash.tree</c> 按 deleted_at 倒序，
    /// 实测 SCAN + 临时排序；同时补齐 EF 模型已声明而基线 DDL 漏建的不一致。</item>
    /// </list>
    ///
    /// <para><b>复核后决定不加的</b>（避免投机索引的写放大与小表收益倒挂）：
    /// <c>links(title)</c>（界面侧名称排序在内存做culture 比较，SQL 下推的 title 排序非热点）、
    /// <c>links(visit_count)</c>（most_visited 结果已进查询缓存，10k 全表排序仍在毫秒级）、
    /// <c>links(is_important)</c>（低选择性，EF 模型里那条声明属历史遗留，已在模型中删除）、
    /// <c>(trash_folder_id, deleted_at)</c> 复合索引（回收站单表量级小，且与既有 idx_trash_links_folder
    /// 高度重叠 = 白付写代价）、<c>idempotency(at)</c>（当前无对应保留/归档命令）。
    /// ⚠️ <c>audit_log(at)</c> 曾在 v3 刻意不加（"随 audit.prune 一起落地"）——**v6 已兑现**：
    /// 索引随 <c>audit.query</c> / <c>audit.prune</c> 一并落地。</para>
    /// </summary>
    private const string IndexesV3 =
        """
        CREATE INDEX idx_links_created ON links(created_at);
        CREATE INDEX idx_links_url_nocase ON links(url COLLATE NOCASE);
        CREATE INDEX idx_trash_folders_deleted ON trash_folders(deleted_at);
        """;

    /// <summary>
    /// v4 版本脚本：文件夹**同层唯一名**的硬约束（唯一索引）。
    ///
    /// <list type="bullet">
    /// <item><b>为什么是表达式索引</b>：SQLite 里 NULL 互不相等，普通 <c>UNIQUE(parent_id, name)</c>
    /// 挡不住根级（<c>parent_id IS NULL</c>）的重名；<c>COALESCE(parent_id,'')</c> 把根归一到同一键，
    /// 根级也纳入唯一性。空串不是实体 ID 形状（零哨兵红线），不会与真实文件夹 ID 冲突。</item>
    /// <item><b>比较口径</b>：<c>COLLATE NOCASE</c>（ASCII 大小写折叠）比命名策略（OrdinalIgnoreCase）更宽松——
    /// 策略只会更严格，不存在"策略放行、索引拒绝"的方向。</item>
    /// <item><b>定位</b>：这是最后防线（任何旁路/未来新入口都绕不过）。正常路径由
    /// <see cref="LinkPocket.Kernel.IFolderNaming"/>（经 <see cref="LinkPocket.Kernel.IUnitOfWork.Naming"/> 取得）统一编号，不会撞到它。</item>
    /// <item><b>既有重名</b>：建索引失败即明确报错拒绝（零兼容：不迁移、不自动改名，处置权归用户）。</item>
    /// </list>
    /// </summary>
    private const string IndexesV4 =
        """
        CREATE UNIQUE INDEX idx_folders_parent_name ON folders(COALESCE(parent_id, ''), name COLLATE NOCASE);
        """;

    /// <summary>
    /// v5 版本脚本：回收站**原位还原**的数据基础 + 被删文件夹快照保真。
    ///
    /// <list type="bullet">
    /// <item><c>origin_parent_folder_id</c>：删除时该文件夹的父目录 ID——此前**完全没有记录**
    /// （根单元的原父只活在内存撤销载荷里，进程重启即失），"还原到原位置"无从谈起。
    /// 语义：NULL = 原在根；v5 后所有进站单元**必有记录**（正常路径不存在"原目录未知"）。
    /// 子单元同样按原父记录（其父在回收站镜像内，原位语义同样成立）。</item>
    /// <item><b>保真列</b>（description / sort_order / created_at / last_visited_at / visit_count）：
    /// 此前 restore_unit 重建文件夹时不带这些字段（还原即丢元数据），同批补齐。
    /// 时间列可空 = v5 前进站的数据（如实未知，不伪造）。</item>
    /// <item><b>不加索引</b>：origin_parent_folder_id 只在还原时单点查一次——
    /// 与前瞻项"不预留空索引"一致，收益不抵写代价。</item>
    /// </list>
    /// </summary>
    private const string AdditionsV5 =
        """
        ALTER TABLE trash_folders ADD COLUMN origin_parent_folder_id TEXT NULL;
        ALTER TABLE trash_folders ADD COLUMN description TEXT NULL;
        ALTER TABLE trash_folders ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE trash_folders ADD COLUMN created_at TEXT NULL;
        ALTER TABLE trash_folders ADD COLUMN last_visited_at TEXT NULL;
        ALTER TABLE trash_folders ADD COLUMN visit_count INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>
    /// v6 版本脚本：**审计可读化**——补列 + 两个实测定位的索引。
    ///
    /// <list type="bullet">
    /// <item><c>dry_run</c> / <c>is_nested</c>：审计条目在内存里一直带这两个字段
    /// （<c>AuditEntry</c>），但落表时被丢弃 → 持久审计分不清"预演"与"嵌套子记录"（如 dedup.apply 的逐条子删）。
    /// 补列后 <c>audit.query</c> 可按它们过滤，也能让 AI 消费者正确解读审计链。</item>
    /// <item><c>stack_trace</c>：失败路径一直采集堆栈（<c>AuditEntry.StackTrace</c>）却无处落盘——
    /// 排障要的原始现场只在文件名日志里（且旧 Logger 是多行文本块）。</item>
    /// <item><c>args_truncated</c>：入参快照超 4000 字符被截断，此前**静默**——消费方无法知道
    /// "这段 args 是不是完整的"。补列如实标记。</item>
    /// <item>索引 <c>idx_audit_at</c>：<c>audit.query</c> 的时间范围 + 倒序分页，以及 <c>audit.prune</c> 的
    /// <c>DELETE WHERE at &lt; ?</c>。此前 <c>audit_log</c> **无任何索引**（v3 注释里明确写了
    /// "随 audit.prune 一起落地，不预留空索引"——本版本兑现）。</item>
    /// <item>索引 <c>idx_audit_correlation</c>：一次调用（含全部嵌套子记录）按 correlation_id 取齐，
    /// 是"把一条用户动作的整条链路一次读出来"的键（AI 自省与排障的主查询形态）。</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// v7：<c>origin_path</c> 快照由"当前语言的显示串"改成 <b>canonical 路径</b>
    /// （<c>@root/A/B</c>，分隔符 <c>/</c>、段内 <c>/</c> 转义）。
    /// <para>
    /// 旧形态把界面语言烙进了数据库：换一次语言，快照里的「全部书签」就与新语言下的路径体系对不上。
    /// 名字里本就含 <c>" / "</c> 的文件夹，在旧形态下**已经是歧义的**（显示串没有转义），
    /// 这里一并归成段分隔符——迁移不会比原状更失真，而此后新写入一律无损。
    /// </para>
    /// </summary>
    private const string CanonicalPathV7 =
        """
        UPDATE trash_folders SET origin_path = CASE
            WHEN origin_path = '未知目录' THEN '@unknown'
            WHEN origin_path = '全部书签' THEN '@root'
            WHEN origin_path LIKE '全部书签 / %'
                THEN '@root/' || replace(substr(origin_path, 8), ' / ', '/')
            ELSE origin_path END
         WHERE origin_path IS NOT NULL;
        UPDATE trash_links SET origin_path = CASE
            WHEN origin_path = '未知目录' THEN '@unknown'
            WHEN origin_path = '全部书签' THEN '@root'
            WHEN origin_path LIKE '全部书签 / %'
                THEN '@root/' || replace(substr(origin_path, 8), ' / ', '/')
            ELSE origin_path END
         WHERE origin_path IS NOT NULL;
        """;

    private const string AdditionsV6 =
        """
        ALTER TABLE audit_log ADD COLUMN dry_run INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE audit_log ADD COLUMN is_nested INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE audit_log ADD COLUMN stack_trace TEXT NULL;
        ALTER TABLE audit_log ADD COLUMN args_truncated INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX idx_audit_at ON audit_log(at);
        CREATE INDEX idx_audit_correlation ON audit_log(correlation_id);
        """;
}

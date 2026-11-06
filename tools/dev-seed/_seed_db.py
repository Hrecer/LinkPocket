# -*- coding: utf-8 -*-
r"""LinkPocket 开发种子脚本（**单一基线直出**：4.0.0 纪元基线，戳 = 9）。

库结构 = src/LinkPocket.Data/SchemaMigrator.cs 的 BaselineDdl（**逐字一致**；两处必须同步，
本脚本运行前先做 DDL 一致性自检，漂移即拒绝执行）。关键点：
  - 表名 folders / links / trash_folders / trash_links / schema_migrations
    （宏是文件 data\macros.json，4.0.0 起不在库里；审计与幂等住在附属库 <库名>-ops.db，也不在主库）；
  - 主键统一 id（folders.id / links.id / trash_links.id / trash_folders.id）；
  - 根不是实体：parent_id / folder_id 用 NULL 表示，不存在哨兵 "0"；
  - folders 没有 link_count 列（目录计数 = 即时递归计算）；
  - 同层唯一名硬约束：idx_folders_parent_name（表达式唯一索引，根级也纳入）；
  - 回收站（原位还原 + 快照保真）：trash_folders 含 origin_parent_folder_id（NULL = 原在根）
    + description / sort_order / created_at / last_visited_at / visit_count；
  - 建库后写库头标记：PRAGMA user_version = 9（纪元戳，旧纪元一律被应用拒绝）、
    PRAGMA application_id = 0x4C50504B（'LPPK'），并写 schema_migrations(version=9) 纪元行。
"""
import sqlite3
import random
import string
import os
import re
from datetime import datetime, timedelta, timezone

# 库文件位置：优先读环境变量 LINKPOCKET_DB；缺省为 App Debug 数据区。
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "linkpocket.db"),
)

# ==== 单一基线 DDL（与 SchemaMigrator.BaselineDdl 逐字一致）====
DDL_BASELINE = """
CREATE TABLE folders (
  id TEXT PRIMARY KEY, parent_id TEXT NULL REFERENCES folders(id) ON DELETE RESTRICT,
  name TEXT NOT NULL, description TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
  last_visited_at TEXT NULL, visit_count INTEGER NOT NULL DEFAULT 0);
CREATE INDEX idx_folders_parent ON folders(parent_id);
CREATE UNIQUE INDEX idx_folders_parent_name ON folders(COALESCE(parent_id, ''), name COLLATE NOCASE);
CREATE INDEX idx_folders_name ON folders(name COLLATE NOCASE, id);

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
CREATE INDEX idx_links_search ON links(title COLLATE NOCASE, id, folder_id, url, description);
CREATE INDEX idx_links_folder_title ON links(folder_id, title COLLATE NOCASE, id);
CREATE INDEX idx_links_folder_created ON links(folder_id, created_at, id);
CREATE INDEX idx_links_folder_updated ON links(folder_id, updated_at, id);
CREATE INDEX idx_links_folder_visits ON links(folder_id, visit_count, id);
CREATE INDEX idx_links_folder_lastvisit ON links(folder_id, last_visited_at IS NULL, last_visited_at, id);
CREATE INDEX idx_links_folder_lastvisit_desc ON links(folder_id, last_visited_at IS NULL, last_visited_at DESC, id DESC);
CREATE INDEX idx_links_visits ON links(visit_count, last_visited_at, id) WHERE folder_id IS NOT NULL OR folder_id IS NULL;

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
"""

# 纪元常量（与 SchemaMigrator.EpochVersion / ApplicationId 同一规则；改动必须同步 C# 侧）
EPOCH_VERSION = 9
APPLICATION_ID = 0x4C50504B   # 'LPPK'


def verify_ddl_consistency():
    """DDL 一致性自检：与 SchemaMigrator.cs 的 BaselineDdl 逐行比对（漂移即拒绝执行）。

    比较规则 = 去空行 + 逐行 strip 后的行序列（屏蔽缩进/行尾空白差异，只卡实际漂移）。
    """
    migrator = os.path.join(REPO_ROOT, "src", "LinkPocket.Data", "SchemaMigrator.cs")
    if not os.path.isfile(migrator):
        print(f"[自检] 未找到 SchemaMigrator.cs：{migrator}")
        return False
    with open(migrator, "r", encoding="utf-8") as f:
        source = f.read()

    def extract(const_name):
        m = re.search(r'private const string ' + const_name + r' =\s*"""(.*?)""";', source, re.DOTALL)
        if not m:
            print(f"[自检] SchemaMigrator.cs 中未找到常量 {const_name}")
            return None
        return m.group(1)

    def norm(sql):
        return [ln.strip() for ln in sql.replace("\r\n", "\n").split("\n") if ln.strip()]

    theirs = extract("BaselineDdl")
    ok = theirs is not None and norm(theirs) == norm(DDL_BASELINE)
    if not ok:
        print("[自检] DDL 不一致：BaselineDdl（_seed_db.py 与 SchemaMigrator.cs 漂移）")
    else:
        print("[自检] DDL 与 SchemaMigrator.cs 一致（单一基线）。")
    return ok


def gen_link_id():
    """链接 ID：16 位大小写字母+数字混合（与后端 EntityIds.NewLinkId 同款；文件夹 ID 是 12 位纯数字）。"""
    import random as _r
    alphabet = string.ascii_letters + string.digits
    return ''.join(_r.choices(alphabet, k=16))


NOW = datetime.now().isoformat()

FOLDERS = [('100000000001', '示例目录 01', None, ''),
 ('100000000010', '示例目录 02', '100000000001', ''),
 ('100000000011', '示例目录 03', '100000000001', ''),
 ('100000000012', '示例目录 04', '100000000001', ''),
 ('100000000020', '示例目录 05', '100000000010', ''),
 ('100000000021', '示例目录 06', '100000000010', ''),
 ('100000000022', '示例目录 07', '100000000010', ''),
 ('100000000023', '示例目录 08', '100000000010', ''),
 ('100000000030', '示例目录 09', '100000000020', ''),
 ('100000000031', '示例目录 10', '100000000020', ''),
 ('100000000040', '示例目录 11', '100000000021', ''),
 ('100000000041', '示例目录 12', '100000000021', ''),
 ('100000000050', '示例目录 13', '100000000011', ''),
 ('100000000051', '示例目录 14', '100000000011', ''),
 ('100000000060', '示例目录 15', '100000000012', ''),
 ('100000000061', '示例目录 16', '100000000012', ''),
 ('200000000001', '示例目录 17', None, ''),
 ('200000000010', '示例目录 18', '200000000001', ''),
 ('200000000011', '示例目录 19', '200000000001', ''),
 ('200000000012', '示例目录 20', '200000000001', ''),
 ('200000000013', '示例目录 21', '200000000001', ''),
 ('200000000020', '示例目录 22', '200000000010', ''),
 ('200000000021', '示例目录 23', '200000000010', ''),
 ('200000000022', '示例目录 24', '200000000010', ''),
 ('200000000030', '示例目录 25', '200000000011', ''),
 ('200000000031', '示例目录 26', '200000000011', ''),
 ('200000000032', '示例目录 27', '200000000030', ''),
 ('200000000033', '示例目录 28', '200000000030', ''),
 ('200000000040', '示例目录 29', '200000000012', ''),
 ('200000000041', '示例目录 30', '200000000012', ''),
 ('200000000050', '示例目录 31', '200000000013', ''),
 ('200000000051', '示例目录 32', '200000000013', ''),
 ('200000000052', '示例目录 33', '200000000013', ''),
 ('200000000060', '示例目录 34', '200000000052', ''),
 ('200000000061', '示例目录 35', '200000000052', ''),
 ('200000000062', '示例目录 36', '200000000052', ''),
 ('300000000001', '示例目录 37', None, ''),
 ('300000000010', '示例目录 38', '300000000001', ''),
 ('300000000011', '示例目录 39', '300000000001', ''),
 ('300000000012', '示例目录 40', '300000000001', ''),
 ('300000000013', '示例目录 41', '300000000001', ''),
 ('300000000020', '示例目录 42', '300000000010', ''),
 ('300000000021', '示例目录 43', '300000000010', ''),
 ('300000000030', '示例目录 44', '300000000011', ''),
 ('300000000031', '示例目录 45', '300000000011', ''),
 ('300000000032', '示例目录 46', '300000000011', ''),
 ('400000000001', '示例目录 47', None, ''),
 ('400000000010', '示例目录 48', '400000000001', ''),
 ('400000000011', '示例目录 49', '400000000001', ''),
 ('400000000012', '示例目录 50', '400000000001', ''),
 ('400000000020', '示例目录 51', '400000000010', ''),
 ('400000000021', '示例目录 52', '400000000010', ''),
 ('400000000022', '示例目录 53', '400000000010', '')]

LINKS = [('https://site-01.example/', '示例书签 01', '示例书签说明 01', '100000000020'),
 ('https://site-02.example/', '示例书签 02', '示例书签说明 02', '100000000030'),
 ('https://site-03.example/', '示例书签 03', '示例书签说明 03', '100000000031'),
 ('https://site-04.example/', '示例书签 04', '示例书签说明 04', '100000000020'),
 ('https://site-05.example/', '示例书签 05', '示例书签说明 05', '100000000020'),
 ('https://site-06.example/', '示例书签 06', '示例书签说明 06', '100000000021'),
 ('https://site-07.example/', '示例书签 07', '示例书签说明 07', '100000000040'),
 ('https://site-08.example/', '示例书签 08', '示例书签说明 08', '100000000041'),
 ('https://site-09.example/', '示例书签 09', '示例书签说明 09', '100000000021'),
 ('https://site-10.example/', '示例书签 10', '示例书签说明 10', '100000000022'),
 ('https://site-11.example/', '示例书签 11', '示例书签说明 11', '100000000022'),
 ('https://site-12.example/', '示例书签 12', '示例书签说明 12', '100000000022'),
 ('https://site-13.example/', '示例书签 13', '示例书签说明 13', '100000000023'),
 ('https://site-14.example/', '示例书签 14', '示例书签说明 14', '100000000023'),
 ('https://site-15.example/', '示例书签 15', '示例书签说明 15', '100000000023'),
 ('https://site-16.example/', '示例书签 16', '示例书签说明 16', '100000000060'),
 ('https://site-17.example/', '示例书签 17', '示例书签说明 17', '100000000060'),
 ('https://site-18.example/', '示例书签 18', '示例书签说明 18', '100000000061'),
 ('https://site-19.example/', '示例书签 19', '示例书签说明 19', '200000000020'),
 ('https://site-20.example/', '示例书签 20', '示例书签说明 20', '200000000021'),
 ('https://site-21.example/', '示例书签 21', '示例书签说明 21', '200000000011'),
 ('https://site-22.example/', '示例书签 22', '示例书签说明 22', '200000000031'),
 ('https://site-23.example/', '示例书签 23', '示例书签说明 23', '200000000040'),
 ('https://site-24.example/', '示例书签 24', '示例书签说明 24', '200000000041'),
 ('https://site-25.example/', '示例书签 25', '示例书签说明 25', '200000000050'),
 ('https://site-26.example/', '示例书签 26', '示例书签说明 26', '200000000051'),
 ('https://site-27.example/', '示例书签 27', '示例书签说明 27', '200000000060'),
 ('https://site-28.example/', '示例书签 28', '示例书签说明 28', '200000000061'),
 ('https://site-29.example/', '示例书签 29', '示例书签说明 29', '200000000062'),
 ('https://site-30.example/', '示例书签 30', '示例书签说明 30', '300000000020'),
 ('https://site-31.example/', '示例书签 31', '示例书签说明 31', '300000000021'),
 ('https://site-32.example/', '示例书签 32', '示例书签说明 32', '300000000030'),
 ('https://site-33.example/', '示例书签 33', '示例书签说明 33', '300000000031'),
 ('https://site-34.example/', '示例书签 34', '示例书签说明 34', '300000000012'),
 ('https://site-35.example/', '示例书签 35', '示例书签说明 35', '300000000012'),
 ('https://site-36.example/', '示例书签 36', '示例书签说明 36', '300000000013'),
 ('https://site-37.example/', '示例书签 37', '示例书签说明 37', '400000000020'),
 ('https://site-38.example/', '示例书签 38', '示例书签说明 38', '400000000020'),
 ('https://site-39.example/', '示例书签 39', '示例书签说明 39', '400000000021'),
 ('https://site-40.example/', '示例书签 40', '示例书签说明 40', '400000000022'),
 ('https://site-41.example/', '示例书签 41', '示例书签说明 41', '400000000011'),
 ('https://site-42.example/', '示例书签 42', '示例书签说明 42', '400000000011'),
 ('https://site-43.example/', '示例书签 43', '示例书签说明 43', '400000000012'),
 ('https://site-44.example/', '示例书签 44', '示例书签说明 44', None),
 ('https://site-45.example/', '示例书签 45', '示例书签说明 45', None),
 ('https://site-46.example/', '示例书签 46', '示例书签说明 46', None),
 ('https://site-47.example/', '示例书签 47', '示例书签说明 47', None)]


def reset_schema(cur):
    """直出**单一基线**（4.0.0 纪元）：DROP 全部业务表后一次执行完整 DDL、
    写库头标记与纪元版本行（幂等重建）。"""
    cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
    tables = [r[0] for r in cur.fetchall()]
    for t in tables:
        cur.execute(f'DROP TABLE IF EXISTS "{t}"')
    cur.executescript(DDL_BASELINE)
    cur.execute(f"PRAGMA user_version = {EPOCH_VERSION}")
    cur.execute(f"PRAGMA application_id = {APPLICATION_ID}")
    stamp = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    cur.execute(
        "INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)",
        (EPOCH_VERSION, stamp),
    )
    print(f"单一基线重建完成（重建 {len(tables)} 张旧表），纪元戳 = {EPOCH_VERSION}，版本行 = {EPOCH_VERSION}")


def main():
    if not verify_ddl_consistency():
        raise SystemExit("DDL 漂移：请先同步 _seed_db.py 的 DDL_BASELINE 与 SchemaMigrator.cs 后重试。")
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()

    random.seed(12345)
    now = datetime.now().isoformat()

    reset_schema(cur)

    print(f"\n插入 {len(FOLDERS)} 个文件夹...")
    for fid, name, pid, desc in FOLDERS:
        cur.execute(
            "INSERT INTO folders (id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at) VALUES (?,?,?,?,0,NULL,0,?,?)",
            (fid, pid, name, desc or None, now, now)
        )

    print(f"插入 {len(LINKS)} 个书签...")
    for url, title, desc, folder_id in LINKS:
        lid = gen_link_id()
        visit_count = random.randint(0, 50)
        is_important = 1 if random.random() < 0.15 else 0
        created = (datetime.now() - timedelta(days=random.randint(0, 180))).isoformat()
        updated = (datetime.now() - timedelta(days=random.randint(0, 7))).isoformat()
        days_ago = random.randint(0, 30)
        visited = (datetime.now() - timedelta(days=days_ago)).isoformat() if visit_count > 0 else None

        favicon_url = ""

        cur.execute(
            """INSERT INTO links
               (id, folder_id, url, title, description, favicon_url,
                last_visited_at, visit_count, is_important, created_at, updated_at)
               VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
            (lid, folder_id, url, title, desc or None, favicon_url, visited, visit_count, is_important, created, updated)
        )

    # === 插入回收站数据（模拟已删除的书签）===
    TRASH_LINKS = [('https://trash-01.example/', '回收站示例 01', '回收站示例说明 01', '100000000020'),
 ('https://trash-02.example/', '回收站示例 02', '回收站示例说明 02', '200000000040'),
 ('https://trash-03.example/', '回收站示例 03', '回收站示例说明 03', '100000000051'),
 ('https://trash-04.example/', '回收站示例 04', '回收站示例说明 04', '100000000023'),
 ('https://trash-05.example/', '回收站示例 05', '回收站示例说明 05', '300000000012'),
 ('https://trash-06.example/', '回收站示例 06', '回收站示例说明 06', '400000000010')]

    print(f"\n插入 {len(TRASH_LINKS)} 个回收站书签...")

    def origin_path(folder_id):
        """沿 parent 链拼出删除时路径快照（与后端 OriginPath 同语义）：『全部书签 / 工作 / Python』。"""
        chain = []
        cur_id = folder_id
        while cur_id is not None:
            f = next(x for x in FOLDERS if x[0] == cur_id)
            chain.append(f[1])
            cur_id = f[2]
        return " / ".join(["全部书签"] + list(reversed(chain)))

    for url, title, desc, orig_folder_id in TRASH_LINKS:
        tid = gen_link_id()
        deleted_at = (datetime.now() - timedelta(hours=random.randint(1, 72))).isoformat()
        visit_count = random.randint(0, 20)
        visited = (datetime.now() - timedelta(days=random.randint(10, 60))).isoformat() if visit_count > 0 else None

        # v2 trash_links 结构：单独删除 = trash_folder_id NULL，位置标记走 origin_folder_id/origin_path
        cur.execute(
            """INSERT INTO trash_links
               (id, url, title, description, favicon_url, trash_folder_id,
                origin_folder_id, origin_path, last_visited_at, visit_count, is_important,
                created_at, updated_at, deleted_at)
               VALUES (?,?,?,?,?,NULL,?,?,?,?,?,?,?,?)""",
            (tid, url, title, desc or None, "", orig_folder_id,
             origin_path(orig_folder_id), visited, visit_count, 0, now, now, deleted_at)
        )

    # === 插入回收站单元（模拟整目录删除；v5：origin_parent_folder_id = 原父目录，NULL = 原在根）===
    # 单元 ID = 被删目录的原文件夹 ID（与 FolderDeleteHandler 保留原 ID 同一规则）；
    # origin_folder_id = 自身 ID 的身份副本；单元内链接的 trash_folder_id = 单元 ID。
    TRASH_UNITS = [('900000000001',
  '回收站单元 01',
  '100000000001',
  '',
  3,
  [('https://trash-unit-01-01.example/', '单元示例 01-01', '单元示例说明 01-01'),
   ('https://trash-unit-01-02.example/', '单元示例 01-02', '单元示例说明 01-02')]),
 ('900000000002', '回收站单元 02', None, '', 0, [('https://trash-unit-02-01.example/', '单元示例 02-01', '单元示例说明 02-01')])]

    def unit_path(unit_name, origin_parent_id):
        """被删目录自身的路径快照（如「全部书签 / 工作 / 旧项目备份」）。"""
        if origin_parent_id is None:
            return f"全部书签 / {unit_name}"
        return origin_path(origin_parent_id) + " / " + unit_name

    unit_count = 0
    unit_link_count = 0
    for unit_id, unit_name, origin_parent, desc, vc, unit_links in TRASH_UNITS:
        deleted_at = (datetime.now() - timedelta(hours=random.randint(4, 96))).isoformat()
        created = (datetime.now() - timedelta(days=random.randint(30, 200))).isoformat()
        visited = (datetime.now() - timedelta(days=random.randint(5, 30))).isoformat() if vc > 0 else None
        own_path = unit_path(unit_name, origin_parent)
        cur.execute(
            """INSERT INTO trash_folders
               (id, parent_id, name, origin_folder_id, origin_parent_folder_id, origin_path,
                description, sort_order, created_at, last_visited_at, visit_count, deleted_at)
               VALUES (?,?,?,?,?,?,?,0,?,?,?,?)""",
            (unit_id, None, unit_name, unit_id, origin_parent, own_path,
             desc or None, created, visited, vc, deleted_at),
        )
        unit_count += 1
        for lurl, ltitle, ldesc in unit_links:
            cur.execute(
                """INSERT INTO trash_links
                   (id, url, title, description, favicon_url, trash_folder_id,
                    origin_folder_id, origin_path, last_visited_at, visit_count, is_important,
                    created_at, updated_at, deleted_at)
                   VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                (gen_link_id(), lurl, ltitle, ldesc or None, "", unit_id,
                 unit_id, own_path, None, 0, 0, created, created, deleted_at),
            )
            unit_link_count += 1

    print(f"\n插入 {unit_count} 个回收站单元（含 {unit_link_count} 个单元内书签）...")

    conn.commit()

    total_links = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
    total_folders = cur.execute("SELECT COUNT(*) FROM folders").fetchone()[0]
    total_trash = cur.execute("SELECT COUNT(*) FROM trash_links").fetchone()[0]
    total_trash_units = cur.execute("SELECT COUNT(*) FROM trash_folders").fetchone()[0]
    root_links = cur.execute("SELECT COUNT(*) FROM links WHERE folder_id IS NULL").fetchone()[0]
    version = cur.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0]

    print(f"\n{'='*40}")
    print(f"schema 版本:   {version}")
    print(f"文件夹总数:   {total_folders}")
    print(f"书签总数:     {total_links}")
    print(f"根目录书签:   {root_links}")
    print(f"回收站书签:   {total_trash}  ← 用于测试恢复/永久删除")
    print(f"回收站单元:   {total_trash_units}  ← 含单元内书签（用于测试单元还原）")
    print(f"{'='*40}")

    def display_origin(folder_id):
        """列表显示用的原位置名（主表文件夹 → 单元 → 根）。"""
        if folder_id is None:
            return "[根]"
        name = next((f[1] for f in FOLDERS if f[0] == folder_id), None)
        if name is None:
            name = next((u[1] for u in TRASH_UNITS if u[0] == folder_id), folder_id)
        return name

    if total_trash_units > 0:
        print("\n回收站单元:")
        for r in cur.execute(
            "SELECT id, name, origin_parent_folder_id FROM trash_folders ORDER BY deleted_at DESC"
        ).fetchall():
            print(f"  [{display_origin(r[2])}] {r[1]} (id={r[0]})")

    if total_trash > 0:
        print("\n回收站书签:")
        rows = cur.execute("SELECT url, title, origin_folder_id FROM trash_links").fetchall()
        for r in rows:
            print(f"  [{display_origin(r[2])}] {r[1]} → {r[0][:40]}")

    conn.close()


if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""10 万级高压种子：在 _seed_db.py 建好的基线上追加 100000 条书签 + 100 层嵌套目录。

用途：UiBench 的搜索/浏览/深树压力库（不是日常开发库）。先跑 _seed_db.py，再跑本脚本。
库文件位置与其它种子脚本一致：LINKPOCKET_DB 覆盖，缺省 = App Debug 数据区。

生成形状（总链接 100047 = 基线 47 + 100000）：
  · 基准·十万条（960000000001）：59985 条直链 + 3 个子目录各 5 条 = 60000 条；
  · 深链基准（960000000002）→ 深链层 001…100：100 层，每层 20 条 = 2000 条；
  · 其余 38000 条随机分到基线目录。
"""
import os
import random
import sqlite3
import string
from datetime import datetime

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "linkpocket.db"),
)

BIG_FOLDER = "960000000001"
DEEP_ROOT = "960000000002"
BIG_SUBFOLDERS = ["960000000301", "960000000302", "960000000303"]
DEEP_LEVELS = [f"9601{i:08d}" for i in range(1, 101)]   # 960100000001 … 960100000100（12 位）
DEEP_NAMES = [f"深链层 {i:03d}" for i in range(1, 101)]

# 11 000 条高命中 + 100 层深树 + 其余分散；固定种子保证可重复
BIG_DIRECT = 59_985
BIG_SUB_EACH = 5
DEEP_PER_LEVEL = 20
RANDOM_FILL = 38_000

SITES = [
    ("site-01.example", "示例站点 01"),
    ("site-02.example", "示例站点 02"),
    ("site-03.example", "示例站点 03"),
    ("site-04.example", "示例站点 04"),
    ("site-05.example", "示例站点 05"),
    ("site-06.example", "示例站点 06"),
    ("site-07.example", "示例站点 07"),
    ("site-08.example", "示例站点 08"),
    ("site-09.example", "示例站点 09"),
    ("site-10.example", "示例站点 10"),
    ("site-11.example", "示例站点 11"),
    ("site-12.example", "示例站点 12"),
    ("site-13.example", "示例站点 13"),
    ("site-14.example", "示例站点 14"),
    ("site-15.example", "示例站点 15"),
    ("site-16.example", "示例站点 16"),
    ("site-17.example", "示例站点 17"),
    ("site-18.example", "示例站点 18"),
    ("site-19.example", "示例站点 19"),
    ("site-20.example", "示例站点 20")
]


def gen_id():
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))


def main():
    if not os.path.exists(DB_PATH):
        raise SystemExit(f"库不存在：{DB_PATH}\n先跑 _seed_db.py 建基线。")

    random.seed(54321)
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()

    version = cur.execute("PRAGMA user_version").fetchone()[0]
    if version != 9:
        raise SystemExit(f"库纪元不符：user_version={version}（需要 9）")

    if cur.execute("SELECT 1 FROM folders WHERE id IN (?, ?)", (BIG_FOLDER, DEEP_ROOT)).fetchone():
        raise SystemExit("检测到 10 万级压力目录已存在；本脚本不重复灌库（请先重新建基线）。")

    base_folders = [r[0] for r in cur.execute(
        "SELECT id FROM folders WHERE id NOT IN (?, ?)", (BIG_FOLDER, DEEP_ROOT)).fetchall()]
    if not base_folders:
        raise SystemExit("基线库里没有文件夹；先跑 _seed_db.py。")

    now = datetime.now().isoformat()

    def insert_folder(fid, name, parent, desc):
        cur.execute(
            "INSERT INTO folders (id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at) VALUES (?,?,?,?,0,NULL,0,?,?)",
            (fid, parent, name, desc or None, now, now))

    insert_folder(BIG_FOLDER, "基准·十万条", None, "10 万级压力基准目录")
    for i, fid in enumerate(BIG_SUBFOLDERS, start=1):
        insert_folder(fid, f"基准子目录 {chr(64 + i)}", BIG_FOLDER, "基准目录的混合行测试子目录")
    insert_folder(DEEP_ROOT, "深链基准", None, "100 层嵌套压力树")
    parent = DEEP_ROOT
    for fid, name in zip(DEEP_LEVELS, DEEP_NAMES):
        insert_folder(fid, name, parent, "深链压力层")
        parent = fid

    batch = []
    inserted = 0
    total_planned = BIG_DIRECT + BIG_SUB_EACH * len(BIG_SUBFOLDERS) + DEEP_PER_LEVEL * len(DEEP_LEVELS) + RANDOM_FILL

    def add(fid, url, title, desc):
        nonlocal inserted
        batch.append((gen_id(), fid, url, title, desc, 0, 0, now, now))
        inserted += 1
        if len(batch) >= 2000:
            flush()

    def flush():
        if not batch:
            return
        cur.executemany(
            "INSERT INTO links (id, folder_id, url, title, description, visit_count, is_important, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?)",
            batch)
        conn.commit()
        batch.clear()
        print(f"已插入 {inserted}/{total_planned} ...")

    for i in range(BIG_DIRECT):
        add(BIG_FOLDER, f"https://bench.example/page/{i}",
            f"基准页面 #{i:05d}", "十万级搜索基准 - 示例页面")
    for sf in BIG_SUBFOLDERS:
        for j in range(BIG_SUB_EACH):
            add(sf, f"https://bench.example/sub/{sf[-1]}/{j}",
                f"基准子页 {sf[-1]}-{j}", "十万级搜索基准 - 子目录页面")
    for level, fid in enumerate(DEEP_LEVELS, start=1):
        for j in range(DEEP_PER_LEVEL):
            add(fid, f"https://deep.example/level/{level}/{j}",
                f"深链页面 L{level:03d}-{j:02d}", f"深层压力测试 - 第 {level} 层")
    for i in range(RANDOM_FILL):
        site = random.choice(SITES)
        add(random.choice(base_folders), f"https://{site[0]}/page/{random.randint(1, 99999)}",
            f"{site[1]} 页面 #{i + 1}", f"性能测试书签 - {site[1]} 的示例页面")
    flush()

    folders = cur.execute("SELECT COUNT(*) FROM folders").fetchone()[0]
    links = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
    depth = cur.execute(
        "WITH RECURSIVE d(id, n) AS (SELECT id, 1 FROM folders WHERE id = ? "
        "UNION ALL SELECT f.id, d.n + 1 FROM folders f JOIN d ON f.parent_id = d.id) "
        "SELECT MAX(n) FROM d", (DEEP_ROOT,)).fetchone()[0]
    print(f"\n完成：links={links} folders={folders} 深链深度={depth}")
    print(f"基准大目录={BIG_FOLDER}（{BIG_DIRECT + BIG_SUB_EACH * len(BIG_SUBFOLDERS)} 条，含子目录）"
          f"  深链根={DEEP_ROOT}（{len(DEEP_LEVELS)} 层）")
    conn.close()


if __name__ == "__main__":
    main()

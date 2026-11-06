# -*- coding: utf-8 -*-
"""爆裂式复杂库：20 条主干 × 100 层 × 每层 50 个分叉 = 100 000 个文件夹，100 000 条书签。

用途：回答"文件夹规模上万、深树上百层"时，搜索 / 目录树 / 导航 / 路径解析各自的成本。
先跑 _seed_db.py 建基线（DDL + 纪元），再跑本脚本。库文件位置同其它种子脚本，
也可用 LINKPOCKET_DB 指定（UiBench 的复杂库就这么建）。

形状（写死、可重复，不用随机数）：
  · 主干 EXP-T01…EXP-T20（20 个，各 500 条直链）；
  · 每个主干沿一条主轴下钻 100 层：每层在上一层主轴节点下建 50 个分叉，
    其中「分叉 01」就是下一层的主轴节点；
  · 文件夹 20 × 100 × 50 = 100 000，最深层 = 100；
  · 书签 100 000 = 主干 20×500 + 主轴 20×100×1 + 普通节点 88 000×1；
  · 因此有 9 980 个深层空目录（深链末段的非主轴节点），用来验证空态与"位置"列。

命名（供搜索压测选词）：
  · 每个节点名 = "爆裂 L{层:03d} 分叉 {序:02d}"；
  · 搜 "分叉 07" 命中 20×100 = 2 000 个文件夹（每层每主干各一个）；
  · 搜 "L050" 命中 20×50 = 1 000 个文件夹；
  · 位置范围（祖先名）搜 "L050" 命中其后代 50 层 × 50 × 20 = 50 000 个文件夹。
"""
import os
import sqlite3
from datetime import datetime

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "explode.db"),
)

TRUNKS = 20
DEPTH = 100
FANOUT = 50                       # 每层的分叉数（含下一层主轴）
TRUNK_LINKS = 500                 # 主干直链
TOTAL_LINKS = 100_000

TRUNK_PREFIX = "EXP-T"
ID_TEMPLATE = "EXP-T{t:02d}-L{d:03d}-F{s:02d}"


def folder_id(trunk, depth, slot):
    return ID_TEMPLATE.format(t=trunk, d=depth, s=slot)


def main():
    if not os.path.exists(DB_PATH):
        raise SystemExit(f"库不存在：{DB_PATH}\n先跑 _seed_db.py 建基线。")

    conn = sqlite3.connect(DB_PATH)
    conn.execute("PRAGMA foreign_keys = ON")
    cur = conn.cursor()

    version = cur.execute("PRAGMA user_version").fetchone()[0]
    if version != 9:
        raise SystemExit(f"库纪元不符：user_version={version}（需要 9）")

    if cur.execute("SELECT 1 FROM folders WHERE id = ? LIMIT 1", (TRUNK_PREFIX + "01",)).fetchone():
        raise SystemExit("检测到爆裂库已存在；本脚本不重复灌库（请先重建基线）。")

    now = datetime.now().isoformat()
    total_folders = TRUNKS * DEPTH * FANOUT
    if total_folders != 100_000:
        raise SystemExit(f"形状算错：{total_folders}")

    cur.execute("PRAGMA synchronous = OFF")
    cur.execute("BEGIN")

    folder_batch = []

    def insert_folder(fid, parent, name):
        folder_batch.append((fid, parent, name, now, now))

    # ① 主干
    for t in range(1, TRUNKS + 1):
        insert_folder(TRUNK_PREFIX + f"{t:02d}", None, f"爆裂主干 {t:02d}")

    # ② 每层 50 个分叉；分叉 01 = 下一层主轴
    spine = {t: TRUNK_PREFIX + f"{t:02d}" for t in range(1, TRUNKS + 1)}
    all_folders = []          # (id, parent, name, is_spine)
    for d in range(1, DEPTH + 1):
        for t in range(1, TRUNKS + 1):
            parent = spine[t]
            for s in range(1, FANOUT + 1):
                fid = folder_id(t, d, s)
                name = f"爆裂 L{d:03d} 分叉 {s:02d}"
                insert_folder(fid, parent, name)
                all_folders.append((fid, parent, name, s == 1))
                if s == 1:
                    spine[t] = fid
        if len(folder_batch) >= 5000:
            cur.executemany(
                "INSERT INTO folders (id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at)"
                " VALUES (?,?,?,NULL,0,NULL,0,?,?)", folder_batch)
            folder_batch.clear()
            print(f"已插入目录 {cur.execute('SELECT COUNT(*) FROM folders').fetchone()[0]}/{total_folders} ...")
    if folder_batch:
        cur.executemany(
            "INSERT INTO folders (id, parent_id, name, description, sort_order, last_visited_at, visit_count, created_at, updated_at)"
            " VALUES (?,?,?,NULL,0,NULL,0,?,?)", folder_batch)

    # ③ 书签：主干各 500；主轴各 1；普通节点填到总数 100 000
    link_batch = []
    inserted = 0

    def add_link(fid, title, url):
        nonlocal inserted
        link_batch.append((f"EXP-L{inserted:07d}", fid, url, title,
                           "爆裂库压力样本", 0, 0, now, now))
        inserted += 1

    for t in range(1, TRUNKS + 1):
        tid = TRUNK_PREFIX + f"{t:02d}"
        for i in range(TRUNK_LINKS):
            add_link(tid, f"爆裂主干 {t:02d} 页面 #{i:03d}", f"https://explode.example/t{t:02d}/{i}")
    for fid, _parent, _name, is_spine in all_folders:
        if is_spine:
            add_link(fid, f"主轴页面 {fid[-11:]}", f"https://explode.example/spine/{fid}")

    remaining = TOTAL_LINKS - inserted
    for fid, _parent, name, is_spine in all_folders:
        if remaining <= 0:
            break
        if is_spine:
            continue
        add_link(fid, f"{name} 页面", f"https://explode.example/leaf/{fid}")
        remaining -= 1

    if remaining != 0:
        raise SystemExit(f"书签分配不上：还差 {remaining} 条（普通节点不够）")

    cur.executemany(
        "INSERT INTO links (id, folder_id, url, title, description, visit_count, is_important, created_at, updated_at)"
        " VALUES (?,?,?,?,?,?,?,?,?)", link_batch)
    conn.commit()

    folders = cur.execute("SELECT COUNT(*) FROM folders").fetchone()[0]
    links = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
    depth = cur.execute(
        "WITH RECURSIVE d(id, n) AS (SELECT id, 1 FROM folders WHERE parent_id IS NULL "
        "UNION ALL SELECT f.id, d.n + 1 FROM folders f JOIN d ON f.parent_id = d.id) "
        "SELECT MAX(n) FROM d").fetchone()[0]
    empty = cur.execute(
        "SELECT COUNT(*) FROM folders f WHERE NOT EXISTS (SELECT 1 FROM links l WHERE l.folder_id = f.id)").fetchone()[0]
    print(f"\n完成：folders={folders} links={links} 最大深度={depth} 空目录={empty}")
    print(f"  主干={TRUNK_PREFIX}01…{TRUNK_PREFIX}{TRUNKS:02d}（各 {TRUNK_LINKS} 条）")
    print(f"  最深层={folder_id(1, DEPTH, 1)}")
    conn.close()


if __name__ == "__main__":
    main()

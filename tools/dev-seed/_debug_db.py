# -*- coding: utf-8 -*-
"""孤儿引用诊断脚本（schema v5；只读）：检查 links.folder_id 是否都能在 folders.id 中找到。"""
import sqlite3
import os

# 库文件位置：优先读环境变量 LINKPOCKET_DB；缺省为 App Debug 数据区。
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "linkpocket.db"),
)
conn = sqlite3.connect(DB_PATH)
c = conn.cursor()

existing = set(r[0] for r in c.execute("SELECT id FROM folders").fetchall())
print(f"Folders in DB: {len(existing)}")

# 所有非空的 folder_id 引用
rows = c.execute("SELECT DISTINCT folder_id, COUNT(*) as cnt FROM links WHERE folder_id IS NOT NULL GROUP BY folder_id").fetchall()
print(f"\nDistinct non-null folder_ids in links: {len(rows)}")
bad_count = 0
for fid, cnt in rows:
    status = "OK" if fid in existing else "MISSING"
    if status == "MISSING":
        bad_count += cnt
    print(f"  {fid!r} x{cnt} [{status}]")

print(f"\nTotal orphaned links: {bad_count}")
conn.close()

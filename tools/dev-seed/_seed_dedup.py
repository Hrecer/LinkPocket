"""查重场景种子（schema v5；只写 links 表）：在既有库上追加多组同 URL 书签（去重工具验证用）。

先跑 _seed_db.py 建库；库路径同 _seed_db.py（可用 LINKPOCKET_DB 覆盖）。
"""
import sqlite3
import os
import random, string

# 库文件位置：优先读环境变量 LINKPOCKET_DB；缺省为 App Debug 数据区。
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "linkpocket.db"),
)

def gen_link_id():
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))

DUP_GROUPS = [
    ("https://dedup-01.example/", "示例站点 01", "示例站点说明 01"),
    ("https://dedup-02.example/", "示例站点 02", "示例站点说明 02"),
    ("https://dedup-03.example/", "示例站点 03", "示例站点说明 03"),
    ("https://dedup-04.example/", "示例站点 04", "示例站点说明 04"),
    ("https://dedup-05.example/", "示例站点 05", "示例站点说明 05"),
    ("https://dedup-06.example/", "示例站点 06", "示例站点说明 06"),
    ("https://dedup-07.example/", "示例站点 07", "示例站点说明 07"),
    ("https://dedup-08.example/", "示例站点 08", "示例站点说明 08"),
    ("https://dedup-09.example/", "示例站点 09", "示例站点说明 09"),
    ("https://dedup-10.example/", "示例站点 10", "示例站点说明 10")
]

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()

for url, base_title, desc in DUP_GROUPS:
    for i in range(5):
        link_id = gen_link_id()
        title = f"{base_title} (副本{i + 1})" if i > 0 else base_title
        folder_id = random.choice([None, None, "100000000020", "100000000030",
                                   "200000000010", "200000000020"])
        cur.execute(
            "INSERT INTO links (id, folder_id, url, title, description, favicon_url, "
            "last_visited_at, visit_count, is_important, created_at, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, NULL, 0, 0, datetime('now'), datetime('now'))",
            (link_id, folder_id, url, title, desc, None)
        )

conn.commit()
print(f"Inserted {len(DUP_GROUPS) * 5} duplicate test records ({len(DUP_GROUPS)} groups × 5)")

cur.execute("SELECT url, COUNT(*) as cnt FROM links GROUP BY url HAVING cnt > 1 ORDER BY cnt DESC")
rows = cur.fetchall()
print(f"\nDuplicate groups in DB: {len(rows)}")
for r in rows:
    print(f"  [{r[1]}×] {r[0]}")

conn.close()
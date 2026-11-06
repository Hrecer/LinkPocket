# -*- coding: utf-8 -*-
"""10k 书签性能种子（schema v5；只写 links 表——该表自 v2 起列未变）：先跑 _seed_db.py 建库，再追加 10000 条书签。"""
import sqlite3
import os
import random
import string
from datetime import datetime

# 库文件位置：优先读环境变量 LINKPOCKET_DB；缺省为 App Debug 数据区。
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB_PATH = os.environ.get(
    "LINKPOCKET_DB",
    os.path.join(REPO_ROOT, "src", "LinkPocket.App", "bin", "Debug", "net10.0-windows", "data", "linkpocket.db"),
)

def gen_id():
    """链接 ID：16 位大小写字母+数字混合（与 EntityIds.NewLinkId 同一规则）。"""
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()

# 获取现有文件夹 ID（v2：folders 表主键 id；根不是实体，无哨兵 "0"）
folder_ids = [r[0] for r in cur.execute("SELECT id FROM folders").fetchall()]
folder_ids.append(None)  # 部分书签不分配文件夹

now = datetime.now().isoformat()
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

batch = []
BATCH_SIZE = 500
total = 10000

for i in range(total):
    site = random.choice(SITES)
    url = f"https://{site[0]}/page/{random.randint(1, 9999)}"
    title = f"{site[1]} 页面 #{i + 1}"
    desc = f"性能测试书签 - {site[1]} 的示例页面"
    fid = random.choice(folder_ids)
    # 绑定顺序必须与 INSERT 的列序逐位对齐：(id, folder_id, url, title, description, ...)
    batch.append((gen_id(), fid, url, title, desc, 0, 0, now, now))

    if len(batch) >= BATCH_SIZE:
        cur.executemany(
            "INSERT INTO links (id, folder_id, url, title, description, visit_count, is_important, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?)",
            batch)
        conn.commit()
        print(f"已插入 {i + 1} 条...")
        batch.clear()

if batch:
    cur.executemany(
        "INSERT INTO links (id, folder_id, url, title, description, visit_count, is_important, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?)",
        batch)
    conn.commit()

total_in_db = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
print(f"\n完成! links 表总计: {total_in_db} 条")
conn.close()

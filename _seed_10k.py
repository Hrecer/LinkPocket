import sqlite3
import random
import string
from datetime import datetime, timedelta

DB_PATH = r"g:\LinkPocket 网站管理器开发\开发目录\linkpocket\bin\Debug\net8.0-windows\linkpocket.db"

def gen_id():
    return ''.join(random.choices(string.digits, k=16))

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()

# 获取现有文件夹 ID
folder_ids = [r[0] for r in cur.execute("SELECT folder_id FROM lists WHERE folder_id != '0'").fetchall()]
folder_ids.append(None)  # 部分书签不分配文件夹

now = datetime.now().isoformat()
SITES = [
    ("example.com", "Example Site"), ("github.com", "GitHub"), ("google.com", "Google"),
    ("stackoverflow.com", "Stack Overflow"), ("medium.com", "Medium"), ("reddit.com", "Reddit"),
    ("youtube.com", "YouTube"), ("twitter.com", "Twitter"), ("linkedin.com", "LinkedIn"),
    ("dev.to", "Dev.to"), ("npmjs.com", "npm"), ("pypi.org", "PyPI"),
    ("docker.com", "Docker"), ("kubernetes.io", "K8s"), ("aws.amazon.com", "AWS"),
    ("azure.microsoft.com", "Azure"), ("cloud.google.com", "GCP"), ("mysql.com", "MySQL"),
    ("postgresql.org", "PostgreSQL"), ("mongodb.com", "MongoDB"),
]

batch = []
BATCH_SIZE = 500
total = 10000

for i in range(total):
    site = random.choice(SITES)
    url = f"https://www.{site[0]}/page/{random.randint(1, 9999)}"
    title = f"{site[1]} 页面 #{i + 1}"
    desc = f"性能测试书签 - {site[1]} 的示例页面"
    fid = random.choice(folder_ids)
    batch.append((gen_id(), url, title, desc, fid, 0, 0, now, now))

    if len(batch) >= BATCH_SIZE:
        cur.executemany(
            "INSERT INTO links (link_id, Url, Title, Description, list_id, visit_count, is_important, CreatedAt, UpdatedAt) VALUES (?,?,?,?,?,?,?,?,?)",
            batch)
        conn.commit()
        print(f"已插入 {i + 1} 条...")
        batch.clear()

if batch:
    cur.executemany(
        "INSERT INTO links (link_id, Url, Title, Description, list_id, visit_count, is_important, CreatedAt, UpdatedAt) VALUES (?,?,?,?,?,?,?,?,?)",
        batch)
    conn.commit()

total_in_db = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
print(f"\n完成! links 表总计: {total_in_db} 条")
conn.close()

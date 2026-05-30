import sqlite3
import random, string

DB_PATH = r"g:\LinkPocket 网站管理器开发\开发目录\linkpocket\bin\Debug\net8.0-windows\linkpocket.db"

def gen_link_id():
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))

DUP_GROUPS = [
    ("https://github.com/microsoft/vscode", "Visual Studio Code", "VS Code 官方仓库"),
    ("https://docs.python.org/3/", "Python 3 文档", "Python 官方文档"),
    ("https://react.dev", "React 官网", "React 前端框架"),
    ("https://nodejs.org/docs/latest/api/", "Node.js API 文档", "Node.js API"),
    ("https://developer.mozilla.org/zh-CN/docs/Web/JavaScript", "MDN JavaScript", "Mozilla 开发者网络 JS 教程"),
    ("https://stackoverflow.com/questions/tagged/python", "Stack Overflow Python", "SO Python 标签页"),
    ("https://www.typescriptlang.org/docs/", "TypeScript 文档", "TS 官方文档"),
    ("https://vuejs.org/guide/introduction.html", "Vue.js 指南", "Vue3 官方指南"),
    ("https://git-scm.com/doc", "Git 文档", "Git 官方文档"),
    ("https://www.docker.com/get-started", "Docker 入门", "Docker 官方教程"),
]

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()

for url, base_title, desc in DUP_GROUPS:
    for i in range(5):
        link_id = gen_link_id()
        title = f"{base_title} (副本{i + 1})" if i > 0 else base_title
        list_id = random.choice([None, None, "1000000000000000020", "1000000000000000030",
                                  "2000000000000000010", "2000000000000000020"])
        cur.execute(
            "INSERT INTO links (link_id, Url, Title, Description, favicon_url, list_id, "
            "last_visited_at, visit_count, is_important, CreatedAt, UpdatedAt) "
            "VALUES (?, ?, ?, ?, ?, ?, NULL, 0, 0, datetime('now'), datetime('now'))",
            (link_id, url, title, desc, None, list_id)
        )

conn.commit()
print(f"Inserted {len(DUP_GROUPS) * 5} duplicate test records ({len(DUP_GROUPS)} groups × 5)")

cur.execute("SELECT url, COUNT(*) as cnt FROM links GROUP BY url HAVING cnt > 1 ORDER BY cnt DESC")
rows = cur.fetchall()
print(f"\nDuplicate groups in DB: {len(rows)}")
for r in rows:
    print(f"  [{r[1]}×] {r[0]}")

conn.close()
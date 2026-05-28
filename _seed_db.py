import sqlite3
import random
import string
from datetime import datetime, timedelta

DB_PATH = r"g:\LinkPocket 网站管理器开发\开发目录\linkpocket\bin\Debug\net8.0-windows\linkpocket.db"

def gen_link_id():
    return ''.join(random.choices(string.digits, k=16))

NOW = datetime.now().isoformat()

FOLDERS = [
    ("1000000000000000001", "工作", "0", "工作相关书签"),
    ("1000000000000000010", "开发文档", "1000000000000000001", ""),
    ("1000000000000000011", "项目资料", "1000000000000000001", ""),
    ("1000000000000000012", "团队工具", "1000000000000000001", ""),
    ("1000000000000000020", "Python", "1000000000000000010", ""),
    ("1000000000000000021", "C# / .NET", "1000000000000000010", ""),
    ("1000000000000000022", "Web前端", "1000000000000000010", ""),
    ("1000000000000000023", "数据库", "1000000000000000010", ""),
    ("1000000000000000030", "Django", "1000000000000000020", ""),
    ("1000000000000000031", "FastAPI", "1000000000000000020", ""),
    ("1000000000000000040", "WPF", "1000000000000000021", ""),
    ("1000000000000000041", "EF Core", "1000000000000000021", ""),
    ("1000000000000000050", "2025年度", "1000000000000000011", ""),
    ("1000000000000000051", "2026年度", "1000000000000000011", ""),
    ("1000000000000000060", "Git托管", "1000000000000000012", ""),
    ("1000000000000000061", "项目管理", "1000000000000000012", ""),

    ("2000000000000000001", "学习", "0", ""),
    ("2000000000000000010", "在线课程", "2000000000000000001", ""),
    ("2000000000000000011", "电子书籍", "2000000000000000001", ""),
    ("2000000000000000012", "编程练习", "2000000000000000001", ""),
    ("2000000000000000013", "AI / ML", "2000000000000000001", ""),
    ("2000000000000000020", "Coursera", "2000000000000000010", ""),
    ("2000000000000000021", "慕课网", "2000000000000000010", ""),
    ("2000000000000000022", "YouTube教程", "2000000000000000010", ""),
    ("2000000000000000030", "计算机科学", "2000000000000000011", ""),
    ("2000000000000000031", "设计模式", "2000000000000000011", ""),
    ("2000000000000000032", "算法导论", "2000000000000000030", ""),
    ("2000000000000000033", "操作系统", "2000000000000000030", ""),
    ("2000000000000000040", "LeetCode", "2000000000000000012", ""),
    ("2000000000000000041", "CodeWars", "2000000000000000012", ""),
    ("2000000000000000050", "PyTorch", "2000000000000000013", ""),
    ("2000000000000000051", "TensorFlow", "2000000000000000013", ""),
    ("2000000000000000052", "LLM大模型", "2000000000000000013", ""),
    ("2000000000000000060", "GPT相关", "2000000000000000052", ""),
    ("2000000000000000061", "Claude", "2000000000000000052", ""),
    ("2000000000000000062", "本地部署", "2000000000000000052", ""),

    ("3000000000000000001", "日常", "0", ""),
    ("3000000000000000010", "社交媒体", "3000000000000000001", ""),
    ("3000000000000000011", "购物", "3000000000000000001", ""),
    ("3000000000000000012", "新闻资讯", "3000000000000000001", ""),
    ("3000000000000000013", "娱乐", "3000000000000000001", ""),
    ("3000000000000000020", "微博", "3000000000000000010", ""),
    ("3000000000000000021", "Twitter/X", "3000000000000000010", ""),
    ("3000000000000000030", "淘宝", "3000000000000000011", ""),
    ("3000000000000000031", "京东", "3000000000000000011", ""),
    ("3000000000000000032", "拼多多", "3000000000000000011", ""),

    ("4000000000000000001", "工具箱", "0", ""),
    ("4000000000000000010", "开发者工具", "4000000000000000001", ""),
    ("4000000000000000011", "设计资源", "4000000000000000001", ""),
    ("4000000000000000012", "效率工具", "4000000000000000001", ""),
    ("4000000000000000020", "API测试", "4000000000000000010", ""),
    ("4000000000000000021", "正则表达式", "4000000000000000010", ""),
    ("4000000000000000022", "JSON格式化", "4000000000000000010", ""),
]

LINKS = [
    # 工作区 - 开发文档
    ("https://docs.python.org/zh-cn/3/", "Python 官方文档", "", "1000000000000000020"),
    ("https://docs.djangoproject.com/", "Django 文档", "最流行的Python Web框架", "1000000000000000030"),
    ("https://fastapi.tiangolo.com/", "FastAPI 文档", "现代高性能Python Web框架", "1000000000000000031"),
    ("https://realpython.com/", "Real Python", "Python高质量教程网站", "1000000000000000020"),
    ("https://pypi.org/", "PyPI - Python包索引", "", "1000000000000000020"),
    ("https://learn.microsoft.com/dotnet/", ".NET 官方文档", "微软官方.NET学习中心", "1000000000000000021"),
    ("https://learn.microsoft.com/en-us/dotnet/desktop/wpf/", "WPF 文档", "Windows Presentation Foundation", "1000000000000000040"),
    ("https://learn.microsoft.com/en-us/ef/core/", "Entity Framework Core", "ORM框架", "1000000000000000041"),
    ("https://github.com/dotnet", ".NET GitHub", ".NET开源仓库", "1000000000000000021"),
    ("https://developer.mozilla.org/", "MDN Web Docs", "Web开发权威参考", "1000000000000000022"),
    ("https://vuejs.org/", "Vue.js 官网", "渐进式JavaScript框架", "1000000000000000022"),
    ("https://react.dev/", "React 官网", "Facebook出品的UI库", "1000000000000000022"),
    ("https://dev.mysql.com/doc/", "MySQL 文档", "最流行的关系型数据库之一", "1000000000000000023"),
    ("https://www.postgresql.org/docs/", "PostgreSQL 文档", "功能强大的开源数据库", "1000000000000000023"),
    ("https://redis.io/documentation", "Redis 文档", "内存数据结构存储", "1000000000000000023"),
    # 团队工具
    ("https://github.com/", "GitHub", "全球最大代码托管平台", "1000000000000000060"),
    ("https://gitlab.com/", "GitLab", "DevOps平台", "1000000000000000060"),
    ("https://trello.com/", "Trello", "看板式项目管理", "1000000000000000061"),
    # 学习区
    ("https://www.coursera.org/", "Coursera", "世界顶级大学在线课程", "2000000000000000020"),
    ("https://www.mooc.cn/", "中国大学MOOC", "国内优质MOOC平台", "2000000000000000021"),
    ("https://book.douban.com/", "豆瓣读书", "发现好书", "2000000000000000011"),
    ("https://refactoring.guru/design-patterns", "设计模式重构", "设计模式可视化指南", "2000000000000000031"),
    ("https://leetcode.cn/", "LeetCode 中文版", "算法刷题必备", "2000000000000000040"),
    ("https://www.codewars.com/", "CodeWars", "代码挑战社区", "2000000000000000041"),
    ("https://pytorch.org/", "PyTorch 官网", "深度学习框架", "2000000000000000050"),
    ("https://www.tensorflow.org/", "TensorFlow 官网", "Google的机器学习框架", "2000000000000000051"),
    ("https://chat.openai.com/", "ChatGPT", "OpenAI对话模型", "2000000000000000060"),
    ("https://claude.ai/", "Claude", "Anthropic AI助手", "2000000000000000061"),
    ("https://ollama.ai/", "Ollama", "本地运行LLM", "2000000000000000062"),
    # 日常区
    ("https://weibo.com/", "微博", "中文社交媒体", "3000000000000000020"),
    ("https://x.com/", "X (Twitter)", "全球社交媒体", "3000000000000000021"),
    ("https://www.taobao.com/", "淘宝", "综合购物平台", "3000000000000000030"),
    ("https://www.jd.com/", "京东", "品质购物", "3000000000000000031"),
    ("https://news.ycombinator.com/", "Hacker News", "科技新闻", "3000000000000000012"),
    ("https://www.zhihu.com/", "知乎", "中文问答社区", "3000000000000000012"),
    ("https://www.bilibili.com/", "B站", "视频分享平台", "3000000000000000013"),
    # 工具箱
    ("https://httpie.io/app", "HTTPie", "优雅的HTTP客户端", "4000000000000000020"),
    ("https://insomnia.rest/", "Insomnia REST Client", "API调试工具", "4000000000000000020"),
    ("https://regex101.com/", "Regex101", "正则表达式在线测试", "4000000000000000021"),
    ("https://jsonformatter.org/", "JSON Formatter", "JSON格式化工具", "4000000000000000022"),
    ("https://dribbble.com/", "Dribbble", "设计师作品展示", "4000000000000000011"),
    ("https://www.figma.com/", "Figma", "协作设计工具", "4000000000000000011"),
    ("https://obsidian.md/", "Obsidian", "知识管理笔记", "4000000000000000012"),
    # 根目录
    ("https://www.google.com/", "Google", "搜索引擎", "0"),
    ("https://www.baidu.com/", "百度", "中文搜索", "0"),
    ("https://www.bing.com/", "Bing", "微软搜索", "0"),
    ("https://stackoverflow.com/", "Stack Overflow", "程序员问答社区", "0"),
]


def main():
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()

    now = datetime.now().isoformat()

    cur.execute("DELETE FROM trashed_links")
    cur.execute("DELETE FROM links")
    cur.execute("DELETE FROM lists")
    conn.commit()
    print("已清空所有表")

    print(f"\n插入 {len(FOLDERS)} 个文件夹...")
    for fid, name, pid, desc in FOLDERS:
        cur.execute(
            "INSERT INTO lists (folder_id, Name, parent_id, Description, link_count, sort_order, CreatedAt, UpdatedAt) VALUES (?,?,?,?,?,?,?,?)",
            (fid, name, pid, desc, 0, 0, now, now)
        )

    print(f"插入 {len(LINKS)} 个书签...")
    for url, title, desc, list_id in LINKS:
        lid = gen_link_id()
        visit_count = random.randint(0, 50)
        is_important = 1 if random.random() < 0.15 else 0
        created = (datetime.now() - timedelta(days=random.randint(0, 180))).isoformat()
        updated = (datetime.now() - timedelta(days=random.randint(0, 7))).isoformat()
        days_ago = random.randint(0, 30)
        visited = (datetime.now() - timedelta(days=days_ago)).isoformat() if visit_count > 0 else None

        cur.execute(
            """INSERT INTO links 
               (link_id, Url, Title, Description, favicon_url, list_id, 
                last_visited_at, visit_count, is_important, CreatedAt, UpdatedAt)
               VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
            (lid, url, title, desc, "", list_id, visited, visit_count, is_important, created, updated)
        )

    # === 插入回收站数据（模拟已删除的书签）===
    TRASH_LINKS = [
        ("https://old-website.com/deprecated-page", "旧网站页面", "一个过时的链接", "1000000000000000020"),  # 原在Python文件夹
        ("https://example.com/test-link", "测试链接", "用于测试恢复功能", "2000000000000000040"),           # 原在LeetCode文件夹
        ("https://temp-project.local/dashboard", "临时项目仪表盘", "", "1000000000000000051"),             # 原在2026年度文件夹
        ("https://legacy-docs.readthedocs.io", "遗留文档站点", "旧版本文档", "1000000000000000023"),       # 原在数据库文件夹
        ("https://abandoned-blog.wordpress.com", "废弃博客", "不再更新的博客", "3000000000000000012"),      # 原在新闻资讯文件夹
        ("https://staging.app.internal/api", "内部测试环境", "内网测试地址", "4000000000000000010"),         # 原在开发者工具文件夹
    ]

    print(f"\n插入 {len(TRASH_LINKS)} 个回收站书签...")
    for url, title, desc, orig_list_id in TRASH_LINKS:
        tid = gen_link_id()
        deleted_at = (datetime.now() - timedelta(hours=random.randint(1, 72))).isoformat()
        visit_count = random.randint(0, 20)
        visited = (datetime.now() - timedelta(days=random.randint(10, 60))).isoformat() if visit_count > 0 else None

        cur.execute(
            """INSERT INTO trashed_links 
               (link_id, Url, Title, Description, list_id,
                last_visited_at, visit_count, is_important, deleted_at, CreatedAt, UpdatedAt)
               VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
            (tid, url, title, desc, orig_list_id, visited, visit_count, 0, deleted_at, now, now)
        )

    conn.commit()

    total_links = cur.execute("SELECT COUNT(*) FROM links").fetchone()[0]
    total_folders = cur.execute("SELECT COUNT(*) FROM lists").fetchone()[0]
    total_trash = cur.execute("SELECT COUNT(*) FROM trashed_links").fetchone()[0]
    root_links = cur.execute("SELECT COUNT(*) FROM links WHERE list_id='0'").fetchone()[0]

    print(f"\n{'='*40}")
    print(f"文件夹总数:   {total_folders}")
    print(f"书签总数:     {total_links}")
    print(f"根目录书签:   {root_links}")
    print(f"回收站书签:   {total_trash}  ← 用于测试恢复/永久删除")
    print(f"{'='*40}")

    if total_trash > 0:
        print("\n回收站内容:")
        rows = cur.execute("SELECT Url, Title, list_id FROM trashed_links").fetchall()
        for r in rows:
            folder_name = next((f[1] for f in FOLDERS if f[0] == r[2]), "[根]")
            print(f"  [{folder_name}] {r[1]} → {r[0][:40]}")

    conn.close()


if __name__ == "__main__":
    main()
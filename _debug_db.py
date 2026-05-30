import sqlite3
conn = sqlite3.connect(r"g:\LinkPocket 网站管理器开发\开发目录\linkpocket\bin\Debug\net8.0-windows\linkpocket.db")
c = conn.cursor()

existing = set(r[0] for r in c.execute("SELECT folder_id FROM lists").fetchall())
print(f"Folders in DB: {len(existing)}")

# 所有非空的list_id
rows = c.execute("SELECT DISTINCT list_id, COUNT(*) as cnt FROM links WHERE list_id IS NOT NULL AND list_id != '0' GROUP BY list_id").fetchall()
print(f"\nDistinct non-null list_ids in links: {len(rows)}")
bad_count = 0
for lid, cnt in rows:
    status = "OK" if lid in existing else "MISSING"
    if status == "MISSING":
        bad_count += cnt
    print(f"  {lid!r} x{cnt} [{status}]")

print(f"\nTotal orphaned links: {bad_count}")

# 直接查 Xfj2V67tj9rUNi7C
print("\n--- Xfj2V67tj9rUNi7C ---")
row = c.execute("SELECT link_id, list_id FROM links WHERE link_id=?", ("Xfj2V67tj9rUNi7C",)).fetchone()
if row:
    print(f"  ListId={row[1]!r}, exists_in_folders={row[1] in existing}")
conn.close()
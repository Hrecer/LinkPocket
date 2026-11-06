"""查重性能场景库生成器（UiBench `dedup` 套件配套）。

从 UiBench 生成的 100K 种子库（linkpocket.db）复制出若干场景库，
每个库只改 links.url / folder_id（不碰 schema）：

    base        原样（332 个重复组 / 667 条重复链接）
    unique      全部唯一（无重复）
    pairs20k    20000 组两两重复（含种子库原有重复 → 20114 组）
    pairs50k    50000 组两两重复（10 万条全是重复）
    many20k5    20000 组每组 5 条（10 万条全是重复）
    giant10k    一个 10000 条的巨组
    deep40k     20000 组两两重复，全部放进 960100000100（最深目录）

用法：
    python seed/_seed_dedup_bench.py
    <UiBench.exe> --db <out>/pairs50k.db --only dedup

输出目录默认 = 系统临时目录/linkpocket-dedup-bench（不写仓库；场景库合计约 500MB）。
"""
import argparse
import os
import shutil
import sqlite3
import tempfile

_HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.environ.get("LINKPOCKET_UI_BENCH_DB")
if not SRC:
    for _candidate in (os.path.join(os.path.dirname(_HERE), "linkpocket.db"),
                       os.path.join(os.path.dirname(_HERE), "UiBench", "linkpocket.db")):
        if os.path.exists(_candidate):
            SRC = _candidate
            break
DEFAULT_OUT = os.path.join(tempfile.gettempdir(), "linkpocket-dedup-bench")
DEEP_FOLDER = "960100000100"


def copy_base(outdir):
    base = os.path.join(outdir, "base.db")
    if os.path.exists(base):
        os.remove(base)
    con = sqlite3.connect(SRC)
    con.execute("VACUUM INTO ?", (base,))
    con.close()
    print("base", os.path.getsize(base) // 1024 // 1024, "MB")
    return base


def copy(base, outdir, name):
    path = os.path.join(outdir, name)
    if os.path.exists(path):
        os.remove(path)
    shutil.copy2(base, path)
    return sqlite3.connect(path), path


def ids(con):
    return [r[0] for r in con.execute("select id from links order by id")]


def set_urls(con, rows):
    con.executemany("update links set url=? where id=?", rows)
    con.commit()


def stats(con):
    links = con.execute("select count(*) from links").fetchone()[0]
    groups = con.execute(
        "select count(*) from (select url from links group by url having count(*)>1)"
    ).fetchone()[0]
    dup_rows = con.execute(
        "select count(*) from links where url in "
        "(select url from links group by url having count(*)>1)"
    ).fetchone()[0]
    return links, groups, dup_rows


def finish(con, path, name):
    print(name, stats(con), path)
    con.close()


def make_unique(base, outdir):
    con, path = copy(base, outdir, "unique.db")
    set_urls(con, [(f"https://uniq.example/{i:06d}", lid) for i, lid in enumerate(ids(con))])
    finish(con, path, "unique.db")


def make_pairs(base, outdir, name, group_count):
    con, path = copy(base, outdir, name)
    all_ids = ids(con)
    rows = []
    for i in range(group_count):
        url = f"https://dup.example/p{i:05d}"
        rows.append((url, all_ids[2 * i]))
        rows.append((url, all_ids[2 * i + 1]))
    set_urls(con, rows)
    finish(con, path, name)


def make_many(base, outdir, group_size):
    con, path = copy(base, outdir, "many20k5.db")
    all_ids = ids(con)
    rows = []
    for i in range(0, len(all_ids), group_size):
        url = f"https://dup.example/m{i // group_size:05d}"
        rows.extend((url, lid) for lid in all_ids[i:i + group_size])
    set_urls(con, rows)
    finish(con, path, "many20k5.db")


def make_giant(base, outdir, count):
    con, path = copy(base, outdir, "giant10k.db")
    all_ids = ids(con)
    set_urls(con, [(f"https://uniq.example/{i:06d}", lid) for i, lid in enumerate(all_ids)])
    set_urls(con, [("https://dup.example/giant", lid) for lid in all_ids[:count]])
    finish(con, path, "giant10k.db")


def make_deep(base, outdir, group_count):
    con, path = copy(base, outdir, "deep40k.db")
    all_ids = ids(con)
    set_urls(con, [(f"https://uniq.example/{i:06d}", lid) for i, lid in enumerate(all_ids)])
    rows = []
    for i in range(group_count):
        url = f"https://dup.example/d{i:05d}"
        for lid in all_ids[2 * i:2 * i + 2]:
            rows.append((url, lid))
            con.execute("update links set folder_id=? where id=?", (DEEP_FOLDER, lid))
    con.commit()
    set_urls(con, rows)
    finish(con, path, "deep40k.db")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=DEFAULT_OUT)
    args = parser.parse_args()
    outdir = os.path.abspath(args.out)
    os.makedirs(outdir, exist_ok=True)
    if not os.path.exists(SRC):
        raise SystemExit(f"种子库不存在：{SRC}（先按同目录 TOOL.md 生成 100K 库）")

    base = copy_base(outdir)
    make_unique(base, outdir)
    make_pairs(base, outdir, "pairs20k.db", 20_000)
    make_pairs(base, outdir, "pairs50k.db", 50_000)
    make_many(base, outdir, 5)
    make_giant(base, outdir, 10_000)
    make_deep(base, outdir, 20_000)
    print("OUT", outdir)


if __name__ == "__main__":
    main()

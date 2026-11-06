# 开发/测试数据种子脚本

直连 SQLite 库文件写数据的开发辅助脚本，不属于产品代码。所有数据均为合成夹具，
不包含真实站点、个人书签或网络下载内容。

## 库文件位置

优先读取环境变量 `LINKPOCKET_DB`；未设置时使用 App Debug 输出目录下的
`data/linkpocket.db`。以下命令在本目录内运行；脚本不会修改产品源码，也不会清理 `bin`。

```powershell
python _seed_db.py

$env:LINKPOCKET_DB = "<隔离库路径>"
python _seed_db.py
```

## 脚本

| 脚本 | 作用 |
|---|---|
| `_seed_db.py` | 清库重建为规范开发数据：单一基线 DDL（与 `SchemaMigrator.cs` 的 `BaselineDdl` 逐行一致）+ 示例目录/书签/回收站条目；运行前自带 DDL 一致性自检 |
| `_seed_10k.py` | 在既有库上追加 10 000 条书签（性能实验用；先跑 `_seed_db.py`） |
| `_seed_100k.py` | 在既有库上追加 100 000 条书签 + 100 层嵌套目录（UiBench 的 100K 压力库；先跑 `_seed_db.py`）。生成 `960000000001`「基准·十万条」60 000 条（含 3 个子目录）、`960000000002` → `960100000001…960100000100` 深链 100 层（每层 20 条），其余 38 000 条分散到基线目录；固定随机种子，可重复 |
| `_seed_explode.py` | 在既有库上追加复杂目录库：20 条主干 × 100 层 × 每层 50 个分叉 = 100 000 个文件夹（最深 101 层）+ 100 000 条书签；命名「爆裂 L{层:03d} 分叉 {序:02d}」，用于文件夹搜索与深树压力 |
| `_seed_dedup.py` | 灌入重复 URL 组（查重/去重场景） |
| `_seed_dedup_bench.py` | 查重性能场景库生成器（UiBench `dedup` 套件配套）：从 UiBench 的 100K 种子库复制出 base / unique / pairs20k / pairs50k / many20k5 / giant10k / deep40k 七个场景库；源库用 `LINKPOCKET_UI_BENCH_DB` 指定，默认输出到系统临时目录，不写源码目录 |
| `_debug_db.py` | 只读诊断：列出 `links.folder_id` 指向不存在目录的孤儿引用 |

## 规则

1. **DDL 必须与 `src/LinkPocket.Data/SchemaMigrator.cs` 的 `BaselineDdl` 逐行一致**
   （`_seed_db.py` 自带 `DDL_BASELINE`）。改动任何一侧都要同步另一侧；一致性自检不通过时脚本拒绝执行。
2. **测试数据只使用保留示例域与合成标题**，不下载图标、不访问外部站点。
3. **测试库是运行产物**：不要放进源码目录，也不要提交到版本控制。
4. **不要清理 `bin`**：默认库位于 App 输出目录的 `data` 下。
5. 增量脚本都要求先有基线库；重复灌库保护由各脚本自己的检查负责。

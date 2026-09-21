# LinkPocket.Modules.Trash

回收站唯一权威。黑盒：除 `TrashModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：回收站两表（`trash_links` / `trash_folders`）的读与清空、还原（含混合批量）、单元层级与计数、**页面快照（overview）**、永久删除。
- **不做**：决定"什么进回收站"（由 Folders/Links/Dedup 经各自命令发起）、保留策略调度（调度器接位，尚未落地）、**站内搬移**（已整体移除——条目只能被打开查看与两路处置）。

## 对外命令（9）

| 命令 | 类型 | 要点 |
|---|---|---|
| `trash.list` | 查询 | 平铺 = **单独删除的书签 + 被删文件夹单元根**（不含单元内部条目），按删除时间倒序；声明缓存 |
| `trash.tree` | 查询 | 全部单元节点（含子单元），`link_count` = 单元子树内书签总数；声明缓存 |
| `trash.unit_contents` | 查询 | 单元内容 = 直接子单元 + 子树内全部书签快照（回收站「打开目录」用） |
| `trash.overview` | 查询 | **回收站页主视图快照**（与 `folders.overview` 同哲学）：全量单元（子树计数 + `origin_path`）+ 全量书签快照（每项携归属单元 `trash_folder_id`，null = 根级）——树叶子注入 + 主栏按层过滤一次取齐；声明缓存 |
| `trash.restore` | 变更 | 还原单条链接：`to = "origin"`（**缺省**）落回删除前所在目录（原目录已不存在时落根 + `fell_back_to_root` 如实回报）；`to = "root"` 显式落根；`to` 越界 → LP.VAL.003 |
| `trash.restore_unit` | 变更 | **还原整单元**（整棵被删文件夹子树 + 单元内书签，全部保留原 ID）；`to` 同前（缺省 origin，依赖 **v5 `origin_parent_folder_id`**）；`target_parent_id` = 显式落点（与 `to` 互斥且必须存在）。**是 `folders.delete`(trash_links) 的逆向**，使"删文件夹"可被 Ctrl+Z 撤销。同层唯一命名经**唯一命名服务** `uow.Naming`：落点层预置既有名 + 单元内子层随还原累积（都不靠调用方查库） |
| `trash.restore_batch` | 变更 | **混合批量还原**（`link_ids` + `folder_ids` + `to`；形状对齐 `purge_batch`）：**先单元后链接**（链接 origin 可指向同批还原的单元 → 按 ID 落回）、共享命名占用表、原子单事务（任一项硬失败整批回滚）。结果如实回报回落 / 编号 / 同 URL 重复计数 |
| `trash.purge` / `trash.purge_batch` | 变更 | 永久删除（单条快照 / 整单元子树）；**破坏性：两阶段确认令牌** |

## 内部组件

- `TrashSupport.CollectSubtreeIds`：单元子树收集（单元树算法的唯一出处）。
- `TrashRestoreSupport`：**还原的唯一流水线**（三命令共用）——预检（不存在 / 主表同 ID 冲突 → 硬失败整批回滚）、
  先单元后链接、落点解析（唯一算法）、命名编号（落点共享占用表）、计数回填、撤销载荷；**批内自记账**
  （EF 查询看不到未提交新增，"同批已还原/同批已有同 URL"绝不依赖再查库）；撤销步**覆盖去重**
  （落点在同批已还原单元树内的项不再单独发步——重叠步会让撤销/重做对同一实体双次处理）。

## 关键口径（行为等价项）

- 「平铺只含单独删除的书签」是刻意的 Windows 口径：被删文件夹里的书签只在单元内可见，不重复出现在平铺列表。
- 还原**缺省**回删除前位置（`to = "origin"`）；原位置快照 = 链接的 `origin_folder_id`（既有）
  / 单元的 `origin_parent_folder_id`（**v5**）；NULL = 原在根（最极端兜底同此语义，正常路径必有记录）。
  `origin_*` 列同时用于「原位置」列展示。
- 还原**入撤销栈**：一条用户动作 = 一条记录（每链接 `links.trash`、每单元 `folders.delete`；重做保原 ID）；
  `links.trash` 的撤销由处理器回填 `{ id, to: "origin" }`（撤"删链接" = 回到删除前目录，不再是落根）。
- `purge` 与 `purge_batch` 标记 `Destructive` → 首次调用返回 `LP.SEC.003 CONFIRM_REQUIRED` 并下发 60s 一次性令牌。

## 测试

- `ModulesTests.cs` → `TrashModuleTests`（两阶段确认 / 单元子树清除 / **混合批量还原矩阵：origin 缺省与回落 / root 显式 / C2 落回同批单元 / 批内同名编号 / 同 URL 重复计数 / 空批量与坏数据硬失败回滚**）
- `NamingServiceTests.cs` → 还原单元的同层命名（落点层占用名预置；**单元内子层重名兜底**——回收站表无唯一索引，
  坏数据/外部来源的重名在还原时被编号，而不是撞 v4 唯一索引让整条还原失败）
- `CommandCoverageTests.cs` → `TrashQueryCoverageTests`（平铺口径、单元树计数、单元内容、未知单元报错、
  **overview 快照：单元计数/原位置/链接归属（树叶子注入）**）
- 端到端：`ProtocolSmoke` §3（原 ID + `origin_path` 快照 / 树与平铺 / 还原（origin 缺省 + root 显式）/ purge 两阶段）

## 复用点

单元树与子树计数被 Folders 删除路径（写快照）与 Maintenance 重置路径（清空）复用；
UI 侧回收站页消费 `trash.overview`（树 + 主栏 + 面包屑的单一快照）、
`trash.purge` / `trash.purge_batch`（永久删除）；`trash.list` / `trash.tree` / `trash.unit_contents` 保留为引擎能力（其它消费者可用）。

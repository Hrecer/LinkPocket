# LinkPocket.Modules.Trash

回收站唯一权威。黑盒：除 `TrashModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：回收站两表（`trash_links` / `trash_folders`）的读与清空、还原、单元层级与计数、**页面快照（overview）与回收站内搬移（move，改归属/层级）**。
- **不做**：决定"什么进回收站"（由 Folders/Links/Dedup 经各自命令发起）、保留策略调度（调度器接位，尚未落地）、**把搬移当还原**（move 不动主表与原位置快照）。

## 对外命令（10）

| 命令 | 类型 | 要点 |
|---|---|---|
| `trash.list` | 查询 | 平铺 = **单独删除的书签 + 被删文件夹单元根**（不含单元内部条目），按删除时间倒序；声明缓存 |
| `trash.tree` | 查询 | 全部单元节点（含子单元），`link_count` = 单元子树内书签总数；声明缓存 |
| `trash.unit_contents` | 查询 | 单元内容 = 直接子单元 + 子树内全部书签快照（回收站「打开目录」用） |
| `trash.overview` | 查询 | **回收站页主视图快照**（与 `folders.overview` 同哲学）：全量单元（子树计数 + `origin_path`）+ 全量书签快照（每项携归属单元 `trash_folder_id`，null = 根级）——树叶子注入 + 主栏按层过滤一次取齐；声明缓存 |
| `trash.move` | 变更 | **回收站内搬移**（`id` + `is_folder` + `target_trash_folder_id` 缺省 = 根）：链接移入/移出单元、单元改挂；只改归属/层级，**不是还原**（原位置快照与主表不动）、**不入撤销栈**；成环即拒绝（LP.STATE.003） |
| `trash.restore` | 变更 | 还原单条：**缺省落根级**（既有口径）；`to_origin = true` 还原到删除前所在目录（原目录已不存在时落根，结果如实回报落点） |
| `trash.restore_unit` | 变更 | **还原整单元**（整棵被删文件夹子树 + 单元内书签，全部保留原 ID）；`target_parent_id` 指定落点，缺省落根、原父不存在时回落根并如实回报。**是 `folders.delete`(trash_links) 的逆向**，使"删文件夹"可被 Ctrl+Z 撤销（2026-09-19 新增）。同层唯一命名经**唯一命名服务** `uow.Naming`：落点层预置既有名 + 单元内子层随还原累积（都不靠调用方查库） |
| `trash.restore_batch` | 变更 | 批量还原，**固定落根级**；批量原子单事务 |
| `trash.purge` / `trash.purge_batch` | 变更 | 永久删除（单条快照 / 整单元子树）；**破坏性：两阶段确认令牌** |

## 内部组件

- `TrashSupport.CollectSubtreeIds`：单元子树收集（单元树算法的唯一出处）。

## 关键口径（行为等价项）

- 「平铺只含单独删除的书签」是刻意的 Windows 口径：被删文件夹里的书签只在单元内可见，不重复出现在平铺列表。
- 还原**缺省**落根（界面口径不变）；`to_origin = true` 是**新增能力**：用快照里的 `origin_folder_id` 原位还原。
  `origin_folder_id`/`origin_path` 同时用于「原位置」列展示。
- `purge` 与 `purge_batch` 标记 `Destructive` → 首次调用返回 `LP.SEC.003 CONFIRM_REQUIRED` 并下发 60s 一次性令牌。

## 测试

- `ModulesTests.cs` → `TrashModuleTests`（两阶段确认 / 单元子树清除 / 批量还原 / **搬移：移入-移出-成环拒绝**）
- `NamingServiceTests.cs` → 还原单元的同层命名（落点层占用名预置；**单元内子层重名兜底**——回收站表无唯一索引，
  坏数据/外部来源的重名在还原时被编号，而不是撞 v4 唯一索引让整条还原失败）
- `CommandCoverageTests.cs` → `TrashQueryCoverageTests`（平铺口径、单元树计数、单元内容、未知单元报错、
  **overview 快照：单元计数/原位置/链接归属（树叶子注入）**）
- 端到端：`ProtocolSmoke` §3（原 ID + `origin_path` 快照 / 树与平铺 / 还原落根 / purge 两阶段）

## 复用点

单元树与子树计数被 Folders 删除路径（写快照）与 Maintenance 重置路径（清空）复用；
UI 侧回收站页消费 `trash.overview`（树 + 主栏 + 面包屑的单一快照）与 `trash.move`（站内拖拽搬移）、
`trash.purge` / `trash.purge_batch`（永久删除）；`trash.list` / `trash.tree` / `trash.unit_contents` 保留为引擎能力（其它消费者可用）。

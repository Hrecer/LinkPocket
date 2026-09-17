# LinkPocket.Modules.Trash

回收站唯一权威。黑盒：除 `TrashModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：回收站两表（`trash_links` / `trash_folders`）的读与清空、还原、单元层级与计数。
- **不做**：决定"什么进回收站"（由 Folders/Links/Dedup 经各自命令发起）、保留策略调度（调度器接位，尚未落地）。

## 对外命令（7）

| 命令 | 类型 | 要点 |
|---|---|---|
| `trash.list` | 查询 | 平铺 = **单独删除的书签 + 被删文件夹单元根**（不含单元内部条目），按删除时间倒序；声明缓存 |
| `trash.tree` | 查询 | 全部单元节点（含子单元），`link_count` = 单元子树内书签总数；声明缓存 |
| `trash.unit_contents` | 查询 | 单元内容 = 直接子单元 + 子树内全部书签快照（回收站「打开目录」用） |
| `trash.restore` / `trash.restore_batch` | 变更 | 还原，**固定落根级**（既有口径）；批量原子单事务 |
| `trash.purge` / `trash.purge_batch` | 变更 | 永久删除（单条快照 / 整单元子树）；**破坏性：两阶段确认令牌** |

## 内部组件

- `TrashSupport.CollectSubtreeIds`：单元子树收集（单元树算法的唯一出处）。

## 关键口径（行为等价项）

- 「平铺只含单独删除的书签」是刻意的 Windows 口径：被删文件夹里的书签只在单元内可见，不重复出现在平铺列表。
- 还原永远落根，不还原到原目录（`origin_folder_id`/`origin_path` 只是位置快照，用于展示）。
- `purge` 与 `purge_batch` 标记 `Destructive` → 首次调用返回 `LP.SEC.003 CONFIRM_REQUIRED` 并下发 60s 一次性令牌。

## 测试

- `ModulesTests.cs` → `TrashModuleTests`（两阶段确认 / 单元子树清除 / 批量还原）
- `CommandCoverageTests.cs` → `TrashQueryCoverageTests`（平铺口径、单元树计数、单元内容、未知单元报错）
- 端到端：`ProtocolSmoke` §3（原 ID + `origin_path` 快照 / 树与平铺 / 还原落根 / purge 两阶段）

## 复用点

单元树与子树计数被 Folders 删除路径（写快照）与 Maintenance 重置路径（清空）复用；
UI 侧回收站页只消费 `trash.list` / `trash.tree` / `trash.unit_contents` 三条查询。

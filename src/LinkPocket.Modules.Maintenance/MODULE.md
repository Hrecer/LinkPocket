# LinkPocket.Modules.Maintenance

库维护与 schema 演进。黑盒：除 `MaintenanceModule.CreateHandlers(Func<EngineRuntimeStats>?)` 外全部 `internal`。

## 职责边界

- **做**：读取 schema 版本、打包诊断信息、整库重置。
- **不做**：真正建库/升级——那是 `LinkPocket.Data.SchemaMigrator` 的职责（本模块只读版本号）。
  也不做保留策略调度（数据保留/归档属调度器，尚未落地）。

## 对外命令（3）

| 命令 | 类型 | 要点 |
|---|---|---|
| `maintenance.schema_version` | 查询 | 当前 schema 版本（`schema_migrations` 的 MAX(version)） |
| `diagnostics.collect` | 查询 | 应用版本 / schema 版本 / 各表计数 / **运行时可观测读数**（脱敏） |
| `maintenance.reinit` | 变更 | 整库重置：单事务清空全部数据 + 尽力清除图标缓存；**破坏性两阶段确认** |

## 关于 `diagnostics.collect` 的 runtime 段

`runtime` 段由**组合根接线**（`CreateHandlers(runtimeStats)` 传入引擎读数提供方），内容 =
查询缓存条目/命中/未命中/淘汰/失效/命中率 + 事件存储游标。

- 观测面纪律：**未接线时为 `null`**，绝不填 0 假装有数据；
- 单一数据源：读的就是引擎实时读数，不另存一份统计（避免"报表与真相不一致"）。

## 关于 `maintenance.reinit` 的语义

旧实现是"删库文件再建"，引擎语义等价改为**单事务清空业务表**（链接/文件夹/回收站两表；
`audit_log` / `idempotency` / `macros` / `schema_migrations` 有保留策略，不清），
因此不需要停连接、不怕句柄占用。整库重置时引擎**同步清空查询缓存与撤销/重做栈**
（`Impact = Database` → `_cache.Clear()` + `Undo.ClearAsync`——旧撤销条目的目标 ID 已不存在，
留着只会让 `undo.undo` 报 EntityNotFound）。dryRun 下不执行 favicon 清理（文件系统不可回滚，
必须保持零副作用）。`audit_log` 的清理/归档策略尚未落地（`audit.prune` 待实现），长期使用需关注审计表增长。

## 测试

- `ModulesTests.cs` → `MaintenanceModuleTests`（schema 版本 + 诊断 runtime 段接线 / 两阶段重置清空 / 干跑零副作用 / 计数口径）
- `DataReviewFixesTests.cs`（链式递归计数 / 未知目录路径显示——跨模块引用，Data 层口径）
- `SchemaMigratorTests.cs` / `IndexPlanTests.cs`（Data 层测试：建库版本链 / 索引计划，非本模块专用）

## 复用点

`diagnostics.collect` 是排障与 AI 自校验的统一入口；`runtimeStats` 的接线方式（委托注入）是
"模块不引 `LinkPocket.Engine` 实现程序集（`EngineRuntimeStats` 在 Contracts）、又要读引擎状态"的通用解法。

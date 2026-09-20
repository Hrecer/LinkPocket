# LinkPocket.Modules.Maintenance

库维护与 schema 演进。黑盒：除 `MaintenanceModule.CreateHandlers(Func<EngineRuntimeStats>?)` 外全部 `internal`。

## 职责边界

- **做**：读取 schema 版本、打包诊断信息、**审计读侧与保留**、**日志读侧与运行期调级**、整库重置。
- **不做**：真正建库/升级——那是 `LinkPocket.Data.SchemaMigrator` 的职责（本模块只读版本号）。
  也不做**保留策略的调度**（没有定时器：`audit.prune` 是"需要时执行的那一次"，由调用方/宿主决定何时调；
  日志文件的留存由落点自己在开/换文件时重算，见 `文档/ARCHITECTURE.md` §10-8）。
  日志的**写入与落点**也不在这里（唯一入口是契约层 `LpLog` 门面 → `LinkPocket.Diagnostics.LogPipeline`）——
  本模块只经 `LpLog.QuerySource` 读，未装配时如实报 `LP.STATE.005`。

## 对外命令（7）

| 命令 | 类型 | 要点 |
|---|---|---|
| `maintenance.schema_version` | 查询 | 当前 schema 版本（`schema_migrations` 的 MAX(version)） |
| `diagnostics.collect` | 查询 | 应用版本 / schema 版本 / 各表计数 / **运行时可观测读数** / 日志与审计读数（脱敏） |
| `audit.query` | 查询 | **调用史读侧**：按命令/调用方/会话/correlation/批/时间过滤，新→旧分页；`include_payloads` 才带 args/changes/stack |
| `audit.prune` | 变更（破坏性） | 清理保留期之外的审计行（缺省 90 天，两阶段确认；`dry_run` 预演将删行数） |
| `logs.query` | 查询 | **日志读侧**：`source=memory`（缺省，内存环，支持 `cursor` 增量轮询）/ `source=file`（从最新 JSONL 文件向前回读）；level/category 过滤、时间升序；未装配 → `LP.STATE.005` |
| `logs.level` | 变更 | **运行期调级**（进程内生效，不落库、不重启）；未装配 → `LP.STATE.005` |
| `maintenance.reinit` | 变更 | 整库重置：单事务清空全部数据 + 尽力清除图标缓存；**破坏性两阶段确认** |

> 口径差别（别混）：`audit.query` 读**库里的调用史**（结构化、可 SQL 过滤、免内存）；`logs.query` 读**过程记录**
> （内存环 + JSONL 文件，含 UI 与观测面噪音，**不落库**）——两者靠 `correlation_id` 对齐到同一条用户动作。

## 关于 `diagnostics.collect` 的 runtime 段

`runtime` 段由**组合根接线**（`CreateHandlers(runtimeStats)` 传入引擎读数提供方），内容 =
查询缓存条目/命中/未命中/淘汰/失效/命中率 + 事件存储游标。

- 观测面纪律：**未接线时为 `null`**，绝不填 0 假装有数据；
- 单一数据源：读的就是引擎实时读数，不另存一份统计（避免"报表与真相不一致"）。

## 关于 `maintenance.reinit` 的语义

实现 = **单事务清空业务表**（链接/文件夹/回收站两表；`audit_log` / `idempotency` / `macros` /
`schema_migrations` 有保留策略，不清），不做"删库文件再建"——因此不需要停连接、不怕句柄占用。
整库重置时引擎**同步清空查询缓存与撤销/重做栈**
（`Impact = Database` → `_cache.Clear()` + `Undo.ClearAsync`——旧撤销条目的目标 ID 已不存在，
留着只会让 `undo.undo` 报 EntityNotFound）。dryRun 下不执行 favicon 清理（文件系统不可回滚，
必须保持零副作用）。`audit_log` 的保留 = **`audit.prune`**（缺省 90 天，破坏性两阶段确认；**无调度器定时执行**，
需要时由调用方触发），长期使用需关注审计表增长。

## 测试

- `ModulesTests.cs` → `MaintenanceModuleTests`（schema 版本 + 诊断 runtime 段接线 / 两阶段重置清空 / 干跑零副作用 / 计数口径）
- `DataReviewFixesTests.cs`（链式递归计数 / 未知目录路径显示——跨模块引用，Data 层口径）
- `SchemaMigratorTests.cs` / `IndexPlanTests.cs`（Data 层测试：建库版本链 / 索引计划，非本模块专用）

## 复用点

`diagnostics.collect` 是排障与 AI 自校验的统一入口；`runtimeStats` 的接线方式（委托注入）是
"模块不引 `LinkPocket.Engine` 实现程序集（`EngineRuntimeStats` 在 Contracts）、又要读引擎状态"的通用解法。

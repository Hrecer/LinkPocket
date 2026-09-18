# LinkPocket.Modules.Dedup

查重（工页"去重"逻辑的引擎化升格）。黑盒：除 `DedupModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：扫描重复、生成处置计划、执行处置。
- **不做**：真正删除——处置一律**嵌套派发 `links.trash`**（软删可恢复），因此一次 `dedup.apply` 只产生**一条父审计条目 + 若干子记录**。

## 对外命令（3）

| 命令 | 类型 | 要点 |
|---|---|---|
| `dedup.scan` | 查询 | 全量链接一次拉取 + 内存按 URL 分组，返回重复数 > 1 的组（组内按创建时间升序） |
| `dedup.plan` | 查询 | **纯函数零副作用**：策略 + 可选 URL 过滤 + 显式保留者 → 每组的 keep / trash 清单 |
| `dedup.apply` | 变更 | 按计划把多余项移入回收站；支持 `DryRun` 预演（执行但不提交、零事件）；唯一落审计的入口 |

> 读流（scan/plan）免审计：查询命令走读池，不产生审计条目；只有 `dedup.apply` 走写流、落审计。

## 处置策略

| 策略 | 保留者 |
|---|---|
| `keep_most_visited`（缺省） | 访问次数最多者（并列时取最近更新） |
| `keep_newest` | 创建时间最新者（同时刻按 ID 次序取定，跨调用稳定） |
| `keep_explicit` | 调用方显式指定（`explicit_keep` 参数）；**未列出的组跳过**，不并入计划 |

可选 `group_urls` 把处置范围限定到指定 URL 组。

## 内部组件

- `DedupDtos`：`DedupGroup` / `DedupPlan`（组内 keep/trash）等结果模型。

## 这是"复杂命令"的样板

`scan → plan → apply` 三步：scan/plan 是只读查询（天然零副作用，免审计）；apply 是唯一写入口——
嵌套派发 `links.trash` 逐条软删，单父审计条目 + 子记录，可用 `CallOptions.DryRun` 完整预演（回滚、零事件）。
新增类似的多阶段处置能力时照此形状做。

## 测试

- `ModulesTests.cs` → `DedupModuleTests`（scan-plan-apply 全流程、策略生效、keep_explicit 未列组跳过、
  参数类型错 LP.VAL.002、空计划零事件、apply 后无重复组）
- 冒烟：`ProtocolSmoke` §6（batch/干跑语义）间接覆盖嵌套派发与干跑路径

## 复用点

处置复用 `links.trash`，所以回收站口径永远只有一处；分组 = 全量链接内存按 URL 分组
（查重必须全量扫描，不逐 URL 查询；`links.find_by_url` 只服务单 URL 精确查找）。
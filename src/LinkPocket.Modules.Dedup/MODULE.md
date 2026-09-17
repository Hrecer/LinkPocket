# LinkPocket.Modules.Dedup

查重（工页"去重"逻辑的引擎化升格）。黑盒：除 `DedupModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：扫描重复、生成处置计划、执行处置。
- **不做**：真正删除——处置一律**嵌套派发 `links.trash`**（软删可恢复），因此一次 `dedup.apply` 只产生**一条父审计条目 + 若干子记录**。

## 对外命令（3）

| 命令 | 类型 | 要点 |
|---|---|---|
| `dedup.scan` | 查询 | 按 URL 分组，返回重复数 > 1 的组（组内按创建时间升序） |
| `dedup.plan` | 查询 | **纯干跑**：策略 + 可选 URL 过滤 + 显式保留者 → 每组的 keep / trash 清单，零副作用 |
| `dedup.apply` | 变更 | 按计划把多余项移入回收站；支持 `DryRun` 预演（回滚、零事件） |

## 处置策略

| 策略 | 保留者 |
|---|---|
| `keep_most_visited`（缺省） | 访问次数最多者（并列时取更早创建） |
| `keep_newest` | 创建时间最新者 |
| `explicit_keep` | 调用方显式指定（`explicit_keep` 参数） |

可选 `group_urls` 把处置范围限定到指定 URL 组。

## 内部组件

- `DedupDtos`：`DedupGroup` / `DedupPlan`（组内 keep/trash）等结果模型。

## 这是"复杂命令"的样板

`scan → plan → apply` 三步**全可干跑、全可审计**：AI 可以先 plan 看清影响面，再 apply 落地；
任何一步都能用 `CallOptions.DryRun` 完整预演。新增类似的多阶段处置能力时照此形状做。

## 测试

- `ModulesTests.cs` → `DedupModuleTests`（scan-plan-apply 全流程、策略生效、apply 后无重复组）
- 冒烟：`ProtocolSmoke` §6（batch/干跑语义）间接覆盖嵌套派发与干跑路径

## 复用点

分组数据来自 `links.find_by_url`（Links 模块的复用点），所以查重不自己写 URL 查询；
处置复用 `links.trash`，所以回收站口径永远只有一处。

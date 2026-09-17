# LinkPocket.Modules.Backup

`.lpbackup` v2 全量备份与恢复。黑盒：除 `BackupModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：整库导出为单文件、文件完整性校验、导入恢复（可追加 / 可清空后恢复）。
- **不做**：Netscape 互通（→ `Modules.Bookmarks`）。**回收站内容不进备份**（既有口径）。

## 对外命令（3）

| 命令 | 类型 | 要点 |
|---|---|---|
| `backup.export` | 变更 | 导出 `.lpbackup` v2：manifest + SHA-256；`FileIo` |
| `backup.inspect` | 查询 | 只读检查：版本 / 统计 / 完整性校验结果（**导入前评估，零副作用**） |
| `backup.import` | 变更 | `replace=false` 追加；`replace=true` 先清空全部数据再导入 → **破坏性，两阶段确认** |

## 内部组件

- `BackupIO`：文件格式读写（manifest、SHA-256、临时 key 身份模型、拓扑序写入）。
- 导入走**单事务**：任一步失败整体回滚，不留半截数据。

## 关键口径（行为等价项）

- **身份模型 = 临时 key**：文件内用临时 key 表达目录/链接关系，导入时才映射为库内 ID，因此不会与现有数据撞 ID。
- **拓扑序导入**：先目录后链接，避免外键约束与"父目录尚未存在"。
- **篡改拒绝**：manifest 校验和不匹配即拒绝导入（不是警告）。
- 回收站不进备份、也不因导入被清（`replace=true` 时按"清空全部数据"语义处理）。

## 测试

- `ModulesTests.cs` → `BackupModuleTests`（导出-检查-导入往返 / 篡改拒绝）
- 端到端：`ProtocolSmoke` §5（临时 key 身份 / SHA-256 篡改拒绝 / 回收站不备份 / 两阶段确认导入）

## 复用点

`backup.inspect` 与导入计划分离：可在不落数据的前提下回答"这个备份文件里有什么"，
供设置页的导入前评估与将来的调度器（定期导出/校验）复用。撤销层的快照兜底亦以本模块格式为载体。

# LinkPocket.Modules.Bookmarks

Netscape 书签文件互导（Chrome / Edge / Firefox 通用交换格式）。黑盒：除 `BookmarksModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：`.html` 书签文件的只读预检、导入（事务）、导出。
- **不做**：`.lpbackup` 全量备份（→ `Modules.Backup`）、文件准备区（→ `staging.*`，本模块的 `inspect` 被其复用）。

## 对外命令（3）

| 命令 | 类型 | 要点 |
|---|---|---|
| `bookmarks.inspect` | 查询 | 只读预检：格式识别 / 条目统计 / 告警；**零副作用**（导入前展示、导出后校验共用） |
| `bookmarks.import` | 变更 | 追加到现有数据；顶层条目落根级；`LongRunning` + 事务 |
| `bookmarks.export` | 变更 | 导出全部书签；**无归属书签落根级不丢数据**；UTF-8 无 BOM |

## 内部组件

- `NetscapeReader`：单遍线性解析（不递归建树），产出与目录结构等价的内存模型。
- `NetscapeWriter`：一次遍历建树后落盘（避免重复扫表）；无归属链接写成根级条目。

## 关键口径（行为等价项）

- 二次导出与前次导出**逐行一致**（往返一致性）；预检计数与导入条数必须对得上。
- 解析前剥离 BOM；导出不写 BOM（与其他浏览器互认）。
- 导入失败/格式非法 → 明确报错，不做"能导多少算多少"的静默降级。

## 测试

- `ModulesTests.cs` → `BookmarksModuleTests`（预检-导入-导出往返 / 非法文件报错）
- 端到端：`ProtocolSmoke` §4（预检计数 / 结构还原 / 实体解码 / UTF-8 无 BOM / 二次导出逐行一致）、§9（10k 导入性能门槛）

## 复用点

`bookmarks.inspect` 是**只读预检**的通用实现：`staging.inspect` 直接嵌套派发它拿到 InspectionReport，
因此"AI 准备区"与"工具页导入"共用同一套预检口径（不重复实现解析）。

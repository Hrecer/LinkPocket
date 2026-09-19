# LinkPocket.Modules.Locate

ID → 位置解析（一切读取皆查询）。黑盒：除 `LocateModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：把一个 ID（链接或文件夹，ID 在两者之间唯一）解析成"它在目录里的位置"——
  目标类型 / **容器目录**（进入它才能看到目标那一行）/ 容器显示路径 / 目标自身显示路径 / 名称。
- **不做**：**移动界面**。切页、进目录、选中行是界面的事（`IContentLocator` + `IBrowserLocateHost` 原语）；
  本模块只回答"目标在哪"，因此无头宿主 / 批处理 / AI 可以复用同一结果。

## 对外命令（1）

| 命令 | 类型 | 要点 |
|---|---|---|
| `locate.resolve` | 查询 | 按 ID 解析位置；链接优先、文件夹兜底（与界面 ID 跳转同一判别顺序）。ID 不存在 → `ENTITY_NOT_FOUND`（零兼容：查询就报错，不返回 null） |

结果字段：

| 字段 | 语义 |
|---|---|
| `kind` | `link` / `folder` |
| `id` / `name` | 目标自身 |
| `container_folder_id` | **进哪个目录**才能看到目标那一行；`null` = 根（零哨兵，与全库同口径） |
| `container_path` | 容器的显示路径（`全部书签 / A / B`） |
| `path` | 目标自身的显示路径（容器路径 + ` / ` + 名称） |

## 与界面的关系

界面的「跳转」= 切到浏览页 + 进入 `container_folder_id` + 选中 `id` 三个原语；
解析算法（类型判别 / 容器推导 / 路径）在本模块，界面侧 `IContentLocator` 只负责把结果翻译成这三个原语
（内部工具页的 ID 跳转、去重对比页的后续能力都走它）。

## 测试

- `tests/LinkPocket.Modules.Tests` → `LocateModuleTests`：链接 / 文件夹各自解析（容器与路径）、
  根级目标（容器 = null）、ID 不存在 → `ENTITY_NOT_FOUND`、路径含多级祖先。
- 冒烟 `tests/ProtocolSmoke` §2：命令存在 + 基本语义。

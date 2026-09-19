# LinkPocket.Modules.Folders

文件夹域唯一权威。模块本身是黑盒：除入口 `FoldersModule.CreateHandlers()` 外全部类型 `internal`。

## 职责边界

- **做**：目录浏览（contents/tree）、面包屑、按名定位、增删改移、批量移动、深层复制、同级排序。
- **不做**：链接内容与元数据（→ `Modules.Links`）、回收站快照（→ `Modules.Trash`）。
  删除目录时"把子树移入回收站"经**嵌套派发/子服务**完成，本模块不自己写回收站表。

## 对外命令（14）

| 命令 | 类型 | 要点 |
|---|---|---|
| `folders.contents` | 查询 | 目录页 = 直接子目录 + 直接子链接 + 面包屑；`folder_id` 缺省 = 根；声明缓存（内容类） |
| `folders.overview` | 查询 | 浏览页主视图一致快照：目录页 + 全量树 + 根级链接数 + 全量链接（树叶子注入），一次命令内同一 UoW（跨命令漂移消除）；声明缓存 |
| `folders.tree` | 查询 | 全部目录平铺（`link_count` = 递归子链接数）；声明缓存 |
| `folders.get` | 查询 | 单目录；参数缺省（向根寻址）→ `LP.STATE.002 ROOT_NOT_ENTITY`，未知 ID → `LP.STATE.001` |
| `folders.breadcrumb` | 查询 | 名称列表，含根显示名「全部书签」；未知目录回落根 |
| `folders.find` | 查询 | 按名定位（精确/`contains`，大小写不敏感）——★ 引擎能力，不接 UI |
| `folders.cycle_check` | 查询 | 移动是否会成环（`target_parent_id` 缺省 = 根，永不成环） |
| `folders.create` / `folders.update` | 变更 | 建目录（可指定父级与描述）/ 改名称与描述（**不换父**；换父只走 `folders.move`）；两者都做**同层唯一命名** |
| `folders.delete` | 变更 | 三模式：缺省整子树移入回收站、`delete_all`、`move_to_list`（可带 `target_list_id`） |
| `folders.move` / `folders.move_batch` | 变更 | 单移 / 批量原子移动（任一校验失败整批不动）；目标层**同层唯一命名** |
| `folders.copy` | 变更 | 深层复制（含全部子目录与书签，生成新 ID）；顶层与**子树各层**都做**同层唯一命名**（同一张占用表按目标层累积，坏数据不至于让整条复制失败） |
| `folders.sort` | 变更 | 同级重排，`sort_order` = `item_ids` 下标；非同级 ID → `LP.STATE.001` |

## 内部组件

- `FolderSupport`：子目录/链接排序口径、面包屑构造、`ToDto`（注入递归计数）。
- **同层唯一命名**：一律走**唯一命名服务** `uow.Naming`（Kernel `IFolderNaming`；编号算法 `WindowsNamingPolicy`
  程序集内可见 → 本模块**既不自己拼编号、也拿不到算法**）。单条用 `ResolveAsync`，批量（`folders.move_batch`）
  用 `CreateTable()` 取占用表：**先 Seed 目标层被占用名，再逐项累积**。绝不把编号职责留给调用方
  （见 `docs/WARNINGS.md` 第 32 条：双实现 + 一半入口不编号曾造出 6 个同名文件夹）。
- 递归计数、祖先链、成环判定、路径显示一律走 **`Kernel.ITreeService`**（树算法唯一出处，本模块不自己遍历父链）。

## 测试

- `tests/LinkPocket.Modules.Tests/ModulesTests.cs` → `FoldersModuleTests`（建/删/移/复制/批量/干跑/**同层唯一命名全入口**）
- `tests/LinkPocket.Modules.Tests/NamingServiceTests.cs` → 命名服务占用表语义（批量移动 Seed+累积、复制子树子层兜底）
- `tests/LinkPocket.Modules.Tests/CommandCoverageTests.cs` → `FoldersQueryCoverageTests`（find/get/breadcrumb/cycle_check/sort）
- 端到端：`ProtocolSmoke` §2（分页、直接子计数、名称升序、面包屑、递归计数、环检测、同层编号）

## 复用点

面包屑与路径显示被 Search（路径范围展开）、Trash（`origin_path` 快照）、Bookmarks（导入路径）复用；
递归计数被所有需要"目录总数"的场景复用（经 `ITreeService`，不引项目）。

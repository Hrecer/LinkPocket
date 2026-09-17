# LinkPocket.Modules.Links

链接域唯一权威 + 智能列表 + 统计 + 元数据抓取。黑盒：除 `LinksModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：链接 CRUD、软删除、查看记账、批量移动/复制/记账、智能列表、全库统计、结构化查询、导入导出、元数据抓取。
- **不做**：目录层级与路径（→ `Modules.Folders`）、回收站表读写（→ `Modules.Trash`）、查重策略（→ `Modules.Dedup`）。

## 对外命令（16）

| 命令 | 类型 | 要点 |
|---|---|---|
| `links.list` | 查询 | 分页查询（目录/关键词/重要/日期过滤；缺省 `created_at desc`） |
| `links.get` | 查询 | 单条；不存在 → `LP.STATE.001` |
| `links.roots` | 查询 | 根级（未归类）书签，缺省 `created_at desc`，上限 `per_page=50` |
| `links.stats` | 查询 | 全库统计（总数 / 回收站项数 / 根级数 / 按目录计数）；声明缓存（Counting） |
| `links.smart_list` | 查询 | 四预设：`recently_added` / `recently_visited` / `recently_edited` / `most_visited` |
| `links.query` | 查询 | ★ 结构化查询器：filter 白名单 + sort 下推 + page(size=0 全量) + fields 投影；**AI 用，不接 UI** |
| `links.find_by_url` | 查询 | ★ 按 URL 精确找全部同址链接（Dedup 复用点） |
| `links.metadata_fetch` | 查询 | 抓取网页元数据；**闸外执行**（`NetworkOutsideGate`），10s 超时 |
| `links.create` / `links.update` | 变更 | 新建（可选自动补全元数据）/ 编辑（仅显式传入字段） |
| `links.trash` | 变更 | 移入回收站（软删，保留原 ID 与原位置快照） |
| `links.visit_record` / `links.visit_batch` | 变更 | 记账查看：链接 +1，所在目录父链**去重后各刷新一次** |
| `links.move_batch` / `links.copy_batch` | 变更 | 批量原子移动 / 复制（单命令单事务，避免 N× 往返） |
| `links.export` | 变更 | 导出 JSON / CSV（`FileIo`） |

## 内部组件

- `LinkSupport`：排序口径、日期解析、父链计数刷新（`RefreshLinkCountAsync`）。
- `MetadataFetcher`：HttpClient 池化 + 超时 + 正则解析（`Kernel.IMetadataParser` 实现为 `RegexMetadataParser`）。
- `LinkDtos`：模块内结果 DTO（`PagedLinkResult` 等）。

## 隐藏能力（★ 不接 UI）

`links.query` 让消费者（AI / 无头宿主）表达任意过滤组合，字段与操作符均走白名单（防注入，越界报 `LP.VAL.003`）；
`links.export` 与 `links.find_by_url` 同样属引擎能力面。**新增能力一律不接 UI**。

## 测试

- `ModulesTests.cs` → `LinksModuleTests`（生命周期/软删还原/父链刷新/四预设/结构化查询/批量/导出/幂等）
- `CommandCoverageTests.cs` → `LinksQueryCoverageTests`（find_by_url/roots/visit_batch 去重口径）
- 端到端：`ProtocolSmoke` §2（数据流）、§6（links.query 白名单防注入 / batch / 幂等 / 干跑）、§9（10k 性能）

## 复用点

`find_by_url` 供 Dedup 分组；`links.query` 是 `search.links` 的实现底座（Search 模块组合预设而非另写查询）；
元数据解析（`IMetadataParser`）被 Bookmarks 导入路径复用。

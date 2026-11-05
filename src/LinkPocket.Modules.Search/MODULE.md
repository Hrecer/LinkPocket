# LinkPocket.Modules.Search

搜索执行。黑盒：除 `SearchModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：四范围搜索（标题 / 地址 / 描述 / 位置）、命中解释、以及供界面“总数先定 + 占位 + 原位填充”使用的**单一快照会话**。
- **不做**：自己写 SQL 查询器——搜索 = `links.query` 的一个**预设组合**（范围 → 过滤条件），排序复用统一排序口径。

## 对外命令（6）

| 命令 | 类型 | 要点 |
|---|---|---|
| `search.links` | 查询 | 四范围组合；**空查询 = `LP.VAL.001`**（引导空态是界面职责）；排序缺省 `title` 升序；**外部缺省全量**（不传 / `0` / 负数 = 全部命中），显式正数才分页；总数走 `search.count` |
| `search.count` | 查询 | 与 `search.links` **同谓词**的 COUNT（不物化实体）；外部调用与兼容路径使用 |
| `search.explain` | 查询 | ★ 返回每条命中的命中字段列表（title/url/description/path）——给 AI 自校验与"为什么命中"展示用；**外部缺省全量**，显式正数才分页；返回 `total/truncated/page/per_page` |
| `search.session_open` | 查询 | **UI 专用**（外部宿主不注册）：开一个单一快照会话：同一 SQLite 读事务内 count + 首屏（`first_page_size`，0 = 不取首屏）；返回 `SessionId / Total / FirstPage`。纯链接首屏走 `ILinkRepository.CountAndFirstPageAsync` 快路径（纯文本 page-first / path 单扫）；含文件夹时走「目录命中 + count + 拼接页」，首页文件夹行按页补水。用完必须 close |
| `search.session_page` | 查询 | **UI 专用**：从会话取一页；缺省 500，`0` = 引擎单页上限 10000；排序/范围由 open 固定 |
| `search.session_close` | 查询 | **UI 专用**：幂等关闭会话；不存在返回 false |

## 内部组件

- `SearchSupport.BuildScopeAsync`：组装多范围谓词（`LinkSearchScope`）交仓储 **SQL 下推**；
  `search_path` 命中目录名时由目录仓储用**递归 CTE** 一次算出「名称命中 + 严格后代」，
  目录集合经 `folder_id IN (...)`（>512 时 `json_each` 单参数）下推。会话在 open 时**只展开一次**，后续页复用同一 scope。
- **目录命中行只取 id/name/排序键**（`SearchSupport.BuildFolderRows`）：5 万后代不必在 open 时读全列；
  取页时 `FindManyAsync` 按页 IDs 补水描述/父目录/日期/次数（同一读事务快照）。排序键随 `sort_by` 选择，
  「最后查看」为空恒排最后（与链接排序同口径）。
- `SearchSupport.SearchAsync` / `CountAsync`：过滤与排序全部 SQL 下推；`SearchWithTotal` 已移除，
  首屏总数由会话在同一事务内取得（`CountAndFirstPageAsync`），避免跨快照拼接。
- `ILinkRepository.CountAndFirstPageAsync`（Data 层实现）：**纯文本范围先取首页**——不满一页即总数，
  稀疏/无命中只扫一遍；满页再补 count。**path 范围**沿有序索引一次扫描同时计数 + 收首页 ID。
  两种路径都只走 `title` 排序的原生索引；其它排序退回 count + page。
- `SearchSupport.MatchedFields`：对已筛出的候选集标注命中字段（与 SQL 谓词同义，供 explain 与高亮）。
- `SearchSupport.ResolveExternalPage`：外部搜索归一（不传 / 0 / 负数 = **全量**；显式正数 = 该页大小，不夹上限）。
- `SearchSupport.ResolvePage`：仅快照会话使用（缺省 500；0/负数 = `EngineLimits.MaxPageSize`；显式值夹上限）。
- `SearchSessionStore`（internal）：持有 `IUnitOfWork + SqliteTransaction(deferred:true)`；
  count 与全部分页同事务、同快照，**WAL 写者不被长读事务阻塞**（测试见 `Search_Session_Is_One_Snapshot_Across_Pages`）。
  TTL 2 分钟 + 30s 定时清扫 + 16 个会话上限（只淘汰空闲的最久未用会话）；正常完成/取消显式 close。
- **增量对账 `search.reconcile`（界面专用，外部宿主不注册）**：只接收本批变更的 `link_ids`，
  用与 `search.session_open` 相同的范围/谓词返回**当前仍命中的最新 DTO**；未返回的 ID = 已删除或已不命中。
  变更 ID 超过 512 条直接要求全量刷新。界面据此维护活结果集：只更新/插入/移除变化行，按与引擎一致的
  排序键（NOCASE / NULL 恒后 / ID 兜底 / 文件夹块前置）二分定位；文件夹变更、无精确信息、批量超限
  一律回落全量快照——**增量是加速器，正确性由全量兜底**。
  数据源复用既有 `ChangeSet.Touched` 与外部变更流（不新增表、不升 schema 纪元、写路径零额外开销）。
- `LinkPocket.Contracts.SearchDtos`：`SearchSessionOpenResult(SessionId, Total, FirstPage)`、
  `SearchExplanation` 等；explain 仍是独立执行，不与 `search.links` 共快照。
- 界面侧：`SearchViewModel` 从首帧起把 `Results.Count` 设为 `TotalHitCount`，未到页是 `SearchResultRow` 占位，
  后台按 10 000/页**原位填充**；未加载页在滚动时**视口优先**，单页失败重试 2 次；不再有 500 条结果上限，也不做用户可见分页。
  首帧用紧凑行模板（真实标题/URL）先上屏，下一帧恢复完整模板补全列；固定行高让 extent 恒定。

## 关键口径（行为等价项）

- 空查询 = `LP.VAL.001`；范围全不选 = 无命中（引导空态是界面职责，引擎不猜意图）。`search_path` 是"目录名命中 → 该子树下所有书签也算命中"。
- **过滤在 SQL 端**：中缀 `LIKE '%x%'` 仍要扫全库（`IndexPlanTests` 的负向断言记录为"已知可接受的全扫描"）；
  **排序/过滤覆盖索引** `idx_links_search` 让 `ORDER BY title ... LIMIT` 沿索引取页。
- 关键词里的 `%` / `_` / `\` 按**字面**匹配（LIKE 通配符已转义）。
- 匹配大小写口径：SQL `LIKE` 对 ASCII 大小写不敏感（与 `OrdinalIgnoreCase` 一致）；非 ASCII 逐字节精确（已知边界）。
- **严格一致性**：会话内 count 与分页来自同一 SQLite 读事务，不会出现“总数与行集跨快照拼接”；
  外部 `search.links` / `search.count` 是独立的短查询（各自快照），但**默认全量**且外部调用方绕过查询缓存，读到的是最新已提交状态。
- **会话不是分页**：UI 只把 session_page 当内部取数批次，用户可见的仍是一条连续结果集。

## 测试

- `ModulesTests.cs` → `SearchModuleTests`（四范围矩阵 / 路径范围展开子树 / explain / **外部默认全量、显式分页可用** / **分页不重不漏** / **`search.count` 与 `search.links` 同谓词一致** / **快照会话跨页一致性** / **会话上限淘汰最久空闲** / **path 范围首页=总数且降序正确**）
- 端到端：`ProtocolSmoke` §9（10k 库搜索耗时门槛）+ **§9b 100k 搜索门槛**（默认全量 / 无命中 / count / explain 全量 / `per_page=0` 全量）。
- 界面：`SearchViewModelTests`（首帧结果数 = 真实总数、占位/原位填充、Ctrl+A 等全量、**视口优先末页先到 + 乱序补页后导航顺序按行序**）；UiBench `search` 套件
  （真实窗口 100K：首屏 <80ms、全列 ≤250ms、停摆/行容器、已填充数、extent 漂移、补页期拖到底/甩动、内存增量、全量 100000 与最后一行可达）。

## 复用点

`search.explain` 是 AI 调用搜索后的自校验入口；范围谓词与排序一律经 `links` 域仓储下推（复用同一套白名单与排序引擎，不重复实现查询）。

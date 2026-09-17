# LinkPocket.Modules.Search

搜索执行。黑盒：除 `SearchModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：四范围搜索（标题 / 地址 / 描述 / 位置）与命中解释。
- **不做**：自己写 SQL 查询器——搜索 = `links.query` 的一个**预设组合**（范围 → 过滤条件），排序复用统一排序口径。

## 对外命令（2）

| 命令 | 类型 | 要点 |
|---|---|---|
| `search.links` | 查询 | 四范围组合；**空查询 = `LP.VAL.001`**（引导空态是界面职责）；排序缺省 `title` 升序 |
| `search.explain` | 查询 | ★ 返回每条命中的命中字段列表（title/url/description/path）——给 AI 自校验与"为什么命中"展示用 |

## 内部组件

- `SearchSupport.SearchAsync`：组装多范围谓词（`LinkSearchScope`）交仓储 **SQL 下推**——过滤与排序都不再进内存；
  `search_path` 命中目录名时**展开整棵子树**（内存 BFS，目录数量有限），命中目录集合经 `folder_id IN (...)` 下推。
- `SearchSupport.MatchedFields`：对已筛出的候选集标注命中字段（与 SQL 谓词同义，供 explain 与高亮）。
- `SearchDtos`：命中记录 DTO（链接 + 命中字段）。

## 关键口径（行为等价项）

- 空查询 = `LP.VAL.001`；范围全不选 = 无命中（引导空态是界面职责，引擎不猜意图）。`search_path` 是"目录名命中 → 该子树下所有书签也算命中"。
- **过滤在 SQL 端**：中缀 `LIKE '%x%'` 注定全表扫描（这是 10k 库 `search.links` 门槛的依据，
  在 `IndexPlanTests` 里以**负向断言**显式记录为"已知可接受的全扫描"，不为它加无效索引）——
  但扫描发生在库内，不再把全库读进内存逐条比对。
- 关键词里的 `%` / `_` / `\` 按**字面**匹配（LIKE 通配符已转义）：用户输入不得改变匹配语义。
- 匹配大小写口径：SQL `LIKE` 对 ASCII 大小写不敏感（与 `OrdinalIgnoreCase` 一致）；
  非 ASCII 逐字节精确——两者在 Unicode 大小写对上存在理论差异，已记录为已知边界。

## 测试

- `ModulesTests.cs` → `SearchModuleTests`（四范围矩阵 / 路径范围展开子树 / explain）
- 端到端：`ProtocolSmoke` §9（10k 库搜索耗时门槛）

## 复用点

`search.explain` 是 AI 调用搜索后的自校验入口；范围谓词与排序一律经 `links` 域仓储下推（复用同一套白名单与排序引擎，不重复实现查询）。

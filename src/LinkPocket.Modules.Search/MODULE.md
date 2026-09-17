# LinkPocket.Modules.Search

搜索执行。黑盒：除 `SearchModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：四范围搜索（标题 / 地址 / 描述 / 位置）与命中解释。
- **不做**：自己写 SQL 查询器——搜索 = `links.query` 的一个**预设组合**（范围 → 过滤条件），排序复用统一排序口径。

## 对外命令（2）

| 命令 | 类型 | 要点 |
|---|---|---|
| `search.links` | 查询 | 四范围组合；**空查询 = 空结果**；全部范围未选 = 按标题；排序缺省 `title` 升序 |
| `search.explain` | 查询 | ★ 返回每条命中的命中字段列表（title/url/description/path）——给 AI 自校验与"为什么命中"展示用 |

## 内部组件

- `SearchSupport.MatchAsync`：四范围谓词组合；`search_path` 命中目录名时**展开整棵子树**（经 `ITreeService` 祖先/后代解析）。
- `SearchSupport.SortHits`：结果排序（保持既有口径，含命中字段随行携带）。
- `SearchDtos`：命中记录 DTO（链接 + 命中字段）。

## 关键口径（行为等价项）

- 范围全不选 = 引导空态（不猜用户意图）；`search_path` 是"目录名命中 → 该子树下所有书签也算命中"。
- 中缀 `LIKE '%x%'` 注定全表扫描——这是 10k 库 `search.links < 100ms` 门槛的依据，
  在 `IndexPlanTests` 里以**负向断言**显式记录为"已知可接受的全扫描"（不为它加无效索引）。

## 测试

- `ModulesTests.cs` → `SearchModuleTests`（四范围矩阵 / 路径范围展开子树 / explain）
- 端到端：`ProtocolSmoke` §9（10k 库搜索耗时门槛）

## 复用点

`search.explain` 是 AI 调用搜索后的自校验入口；路径展开逻辑与 Folders 的树服务同源（不重复实现父链遍历）。

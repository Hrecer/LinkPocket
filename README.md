# LinkPocket（v2 引擎化重构）

个人书签管理器：本地优先、单用户桌面应用。核心是**一台可被多种消费者以同一方式驱动的引擎**——
一切变更表达为命令、一切读取表达为查询，界面 / 批处理 / 未来的 AI 走同一条路。

- 技术栈：.NET 8 · WPF · EF Core / SQLite（WAL）· xUnit
- 命令目录：**70 条**（52 业务 + 15 编排 + 3 批），全部由描述符机械生成 → [`docs/catalog/COMMANDS.md`](docs/catalog/COMMANDS.md)
- 架构说明：[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)（分层、四条数据流、不变量、性能与验证）
- 避坑清单：[`docs/WARNINGS.md`](docs/WARNINGS.md)（改代码前值得读一遍）

---

## 快速开始

```powershell
# 构建（0 警告 0 错误）
dotnet build LinkPocket.sln -c Release

# 运行界面（WPF）
dotnet run --project src/LinkPocket.App -c Release

# 单元测试（架构 / 引擎 / 模块）
dotnet test LinkPocket.sln -c Release

# 协议冒烟（§0–§11 端到端；Debug 下性能门槛放宽 ×5）
dotnet run --project tests/ProtocolSmoke

# CI 门禁（五道门：清 obj 编译 → 单测 → 冒烟 → 严格性能门槛 → 目录文档漂移检查）
powershell -ExecutionPolicy Bypass -File build\ci.ps1

# 重新生成命令目录文档（禁止手改 docs/catalog）
powershell -ExecutionPolicy Bypass -File build\gen-catalog.ps1
```

CI 门禁产物：`build/artifacts/{build,test,smoke,catalog}.log` + `perf_report.json`（10k 基准逐条实测值）。

---

## 目录结构

```
src/
  LinkPocket.Contracts/        契约层（零依赖）：IEngine / 错误模型 / 命令描述符 / 事件与缓存策略 DTO
  LinkPocket.Kernel/           L0 内核契约：实体 · 强类型 ID · 仓储/工作单元 · 树/命名/排序/解析
  LinkPocket.Engine/           L1–L4：命令与查询管道 · 查询缓存 · 事件总线与存储 · 编排 · 会话 · 目录导出
  LinkPocket.Data/             EF DbContext · WAL · schema 版本链 · 仓储实现 · 排序下推
  LinkPocket.Infrastructure/   过渡期容器：旧协议 LinkPocketApi + UI 支撑类型
  LinkPocket.Modules.*/        9 个业务域模块（各自带 README.md）
  LinkPocket.UIKit/ · LinkPocket.UI.*/ · LinkPocket.App/   界面层
tests/                         架构 / 引擎 / 模块单测 + ProtocolSmoke 可执行冒烟
tools/LinkPocket.CatalogExport/ 目录文档导出（不连数据库）
build/                         ci.ps1 · gen-catalog.ps1
docs/                          ARCHITECTURE.md · WARNINGS.md · catalog/
```

---

## 数据与兼容须知

**旧数据零责任**：本版本不迁移、不读取、不转换任何旧格式数据库。首次启动直接创建全新 schema v3 库；
检测到旧格式库（有用户表但无 `schema_migrations`）会**明确报错拒绝**，不动、不删旧文件——处置权归用户。
开发/测试数据由种子脚本（`开发目录/_seed_db.py`）重建，测试用库一律是临时文件。

---

## 现状边界

- **WPF 界面当前仍走旧协议链**（`AppHost` = `LinkPocketApi` + `InProcessTransport`）；
  新引擎已完整可用并由冒烟/工具/测试消费，UI 切换是后续独立阶段。
  因此"界面行级增量刷新"尚未生效——引擎侧的 ChangeSet 增量投递原语已就位，等切换接上。
- 其余遗留项见 `docs/ARCHITECTURE.md` 第 10 节（诚实清单）。

## 许可

见 [`LICENSE`](LICENSE)。

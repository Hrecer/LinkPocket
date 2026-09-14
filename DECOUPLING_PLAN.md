# LinkPocket 前后端分离重构计划

> 目标：把整个应用拆分为**纯后端（数据与业务逻辑）**与**纯前端（展示与交互）**，
> 两者之间只通过一个 JSON 协议通信层连接。未来无论前端继续用 WPF 还是换成
> Web 技术（SPA / Tauri / WebView2 等），后端 Core 原封不动；反之亦然。
>
> 另一个已确认的产品方向：文件夹浏览将从"卡片向下展开"改为
> **资源管理器 / 网盘式逐级进入**（单击/双击进入文件夹 + 面包屑 + 返回）。
> 后端 API 的 `folders.contents` 协议（子文件夹 + 链接 + 面包屑）就是为此设计的。

## 目标架构

```
┌─────────────────────────── 前端（可替换）───────────────────────────┐
│  当前: WPF (MainWindow / Views / ViewModels / FaviconService)      │
│  未来: Web 技术均可                                                 │
│  职责: 渲染、交互、图片解码；不含任何数据库/业务规则                  │
└──────────────────────────────┬─────────────────────────────────────┘
                               │  ILinkPocketTransport
                               │  JSON-RPC 2.0 协议（唯一连接方式）
┌──────────────────────────────┴─────────────────────────────────────┐
│ LinkPocket.Core（后端，net8.0，无任何 UI 依赖）                      │
│  Api/      ILinkPocketApi + LinkPocketApi + 分发器 + DTO            │
│  Services/ LinkService / FolderService / 导入导出 / Logger          │
│  Data/     EF Core + SQLite (linkpocket.db)                        │
│  Managers/ SelectionManager / ClipboardManager                     │
└────────────────────────────────────────────────────────────────────┘
```

传输实现按部署形态选择：`InProcessTransport`（当前，进程内直连）、
未来的 `HttpTransport`（ASP.NET Core 自宿主，供 Web 前端跨进程访问）。

## 已完成

- [x] **解耦第 1 步**：`IUiCoordinator` 接口切断 ViewModel → MainWindow 反向依赖（commit fb124a2）
- [x] **前后端分离第 1 步**：创建 `LinkPocket.Core` 类库（net8.0，零 WPF 依赖），
      迁移 Data / Models / 六个 Services / SelectionManager / ClipboardManager
- [x] `FaviconService` 拆分：`FaviconStore`（后端磁盘缓存，Core）+ `FaviconService`（前端渲染，WPF）
- [x] **后端 API 契约**：`Core/Api/` 下 25 个协议方法（JSON-RPC 2.0），
      覆盖浏览、文件夹、链接、回收站、搜索、智能列表、统计、导入导出
- [x] **通信层**：`ILinkPocketTransport` + `InProcessTransport` + 分发器 + 强类型调用扩展
- [x] 端到端冒烟测试：24 项协议调用全部通过（建目录→建链接→浏览→搜索→回收站闭环→错误通道）

## 阶段清单（每步独立可交付、单独提交）

### P1 ✅ 进程内分离（本次已完成）
拆出 Core 项目、定义 API 契约与协议、进程内传输。WPF 界面行为不变。

### P2 前端迁移到 API（下一阶段）
把 MainViewModel / LinkViewModel / 各页面中直接调用 `_linkService` /
`_db` 的代码，逐步改为经 `UiCoordinator` 持有的 Transport 调用协议。
- [ ] 组装入口：App 启动时创建 `LinkPocketApi` + `InProcessTransport` 并全局注册
- [ ] MainViewModel 的链接 CRUD、文件夹树加载改走协议
- [ ] 回收站 / 智能列表 / 工具页改走协议
- [ ] 移除 `GetDbForBackup()`，备份导入导出走协议（`backup.export` / `backup.import` 协议待补）
- [ ] 删除 `MainViewModel.ReinitializeDatabaseAsync` 的跨层文件操作，改为协议方法
- [ ] 顺手消灭共享单例 DbContext（P1 冒烟已证明每个 API 实例自管 context 可行）

### P3 事件推送 + 备份协议补全
- [ ] 协议补充：`backup.export`、`backup.import`、`settings.*`、`tools.dedup_report`
- [ ] 后端数据变更事件（`EventReceived`）：`links.changed` / `folders.changed` / `trash.changed`
      前端订阅后刷新对应视图，替代现在的多层事件链
- [ ] 断线语义定义（为未来跨进程传输预留）

### P4 前端界面重组（为资源管理器式浏览做准备）
- [ ] 把 MainWindow 代码构建的 UI 逐块搬成 UserControl：侧栏、主列表、详情面板
- [ ] 主列表改造为**目录视图**：数据源 = `folders.contents`，
      进入 = 加载子文件夹+链接；导航条 = 面包屑 + 返回/前进（历史栈）
- [ ] 消灭渲染路径上的 `GetAwaiter().GetResult()` 同步阻塞
- [ ] 侧栏树降级为可选快速跳转（不再承担"展开即浏览"职责）

### P5 新 UI 正式版（二选一路线，届时定夺）
- 路线 A（保守）：继续 WPF，替换 DataTemplate / 主题，重画资源管理器式交互
- 路线 B（Web）：ASP.NET Core 自宿主 + `HttpTransport`，Web 前端（静态 SPA），
  打包为 WebView2 窗口或独立浏览器访问；Core 不改一行
- [ ] 无论哪条路线：契约测试（对 25 个协议方法的回归用例）先行固化

## 协议方法清单（v1，25 个）

| 域 | 方法 |
|---|---|
| 浏览 | `folders.contents` · `folders.tree` · `folders.breadcrumb` |
| 文件夹 | `folders.create` · `folders.update` · `folders.delete` · `folders.move` · `folders.copy` · `folders.would_create_cycle` · `folders.update_sort` |
| 链接 | `links.list` · `links.create` · `links.update` · `links.trash` · `links.record_visit` |
| 回收站 | `trash.list` · `trash.restore` · `trash.purge` |
| 搜索 | `search` · `smartlist` |
| 元数据/统计 | `meta.fetch` · `stats.counts` |
| 导入导出 | `export.bookmarks_html` · `import.bookmarks_html` |

信封格式：请求 `{"id":"…","method":"域.动作","params":{…}}`，
响应 `{"id":"…","result":…}` 或 `{"id":"…","error":{"code":…,"message":"…}}`。
参数与 DTO 字段均为 snake_case。

## 约定

1. **依赖方向**：前端 → 通信层 → Core 契约。前端永远不 `using LinkPocket.Data`，不碰 DbContext。
2. **DTO 单向兼容**：DTO 字段只增不删；改语义先加新字段。
3. **每阶段结束必须**：两个项目编译 0 警告 0 错误 + 冒烟通过 + 单独 Git 提交。
4. Core 项目禁止引入任何 `System.Windows` / WPF 包（CI 可加检查）。

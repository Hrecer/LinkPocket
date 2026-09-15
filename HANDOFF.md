# LinkPocket 项目交接文档（含 UI 重构路线图）

> 本文档面向接手的 AI / 开发者。目标：让你在不读完全部代码的情况下，准确理解项目现状、
> 架构约定、协议细节，并据此完成"把界面改成资源管理器 / 网盘式文件管理器"的重构。
>
> 文档对应代码版本：commit `5338843`（前后端分离第二步完成）。
> 更新本文档时请同步更新末尾的"文档版本"。

---

## 1. 项目概览

LinkPocket 是一个 Windows 桌面**书签 / 网站管理器**。核心能力：

- 链接增删改查（自动抓取网页 title / description / favicon）
- 多层级文件夹组织
- 回收站（软删除 → 恢复 / 彻底删除）
- 搜索（标题 / URL / 描述 / 路径）
- 智能列表（最近添加 / 最近查看 / 最近编辑 / 最常查看）
- 工具页（链接去重、按 ID 跳转）
- 导入导出：浏览器书签 HTML（Netscape 格式）、`.lpbackup` 全量备份包
- favicon 磁盘缓存、按天日志

技术栈：

| 层 | 技术 |
|---|---|
| 后端（Core） | .NET 8 类库，EF Core 8.0.10 + SQLite，**无任何 UI 依赖** |
| 通信 | JSON-RPC 2.0 风格协议，`ILinkPocketTransport` 抽象（当前进程内直连） |
| 前端（当前） | WPF (net8.0-windows)，MaterialDesignThemes 5.1.0 / MdXaml / XamlFlair |
| 前端（未来） | 未定，可能是 Web（此时只需新增 `HttpTransport`，后端零改动） |

**关键结论：前后端已经完全解耦。前端所有数据访问都经协议；后端不引用任何 UI 类型。**

---

## 2. 快速上手

```bash
cd "G:\LinkPocket 网站管理器开发\开发目录\linkpocket"

# 编译（后端 + 前端）
dotnet build -v q --nologo

# 只编译后端
dotnet build Core/LinkPocket.Core.csproj -v q --nologo

# 运行
bin/Debug/net8.0-windows/LinkPocket.exe
```

运行时文件位置（`AppContext.BaseDirectory`，即 exe 所在目录）：

| 内容 | 路径 |
|---|---|
| 数据库 | `linkpocket.db`（SQLite 单文件） |
| favicon 缓存 | `favicons/`（文件名 = 图标 URL 的 SHA256 + 扩展名） |
| 日志 | `logs/yyyyMMdd.log`（保留最近 10 份） |

提交历史（按时间正序，最近 3 次是本轮重构）：

```
fb124a2  refactor: 引入 IUiCoordinator 接口，解除 ViewModel 对 MainWindow 的直接依赖
bb83a04  refactor: 前后端分离第一步 —— 拆出 LinkPocket.Core 后端项目与 JSON-RPC 通信层
5338843  refactor: 前后端分离第二步 —— 前端全部改走协议调用
```

---

## 3. 架构总览

```
┌──────────────────── 前端（可整体替换）────────────────────┐
│ MainWindow.xaml(.cs)  Views/  ViewModels/                │
│ Services/FaviconService.cs（WPF BitmapImage 渲染）        │
│ Managers/LinkNavigator.cs（依赖 Dispatcher 的导航定位）    │
│ 职责：渲染、交互、图片解码；不碰数据库、不含业务规则        │
└───────────────────────────┬──────────────────────────────┘
                            │  ① 调用 ILinkPocketApi（强类型）
                            │  ② TransportedLinkPocketApi 翻译成 JSON-RPC
                            │  ③ ILinkPocketTransport.SendAsync(json)  ← 唯一通道
┌───────────────────────────┴──────────────────────────────┐
│ LinkPocket.Core（后端，net8.0，零 UI 依赖）                │
│  Api/  LinkPocketApiDispatcher → LinkPocketApi → Services │
│  Services/ LinkService FolderService 导入导出 Logger       │
│  Data/ EF Core + SQLite                                   │
└──────────────────────────────────────────────────────────┘
```

前端启动时的组装（`Services/AppServices.cs`，在 `App` 构造函数里调用一次）：

```csharp
var backend   = new LinkPocketApi();                                  // 后端实现（持有唯一 DbContext）
Transport     = new InProcessTransport(new LinkPocketApiDispatcher(backend));
Api           = new TransportedLinkPocketApi(Transport);              // 前端面向的强类型代理
```

**为什么用代理而不是让前端直接 new LinkPocketApi？**
前端写的是强类型 C#（`api.GetLinksAsync(...)`），但每次调用都真的被序列化成 JSON、
经 `ILinkPocketTransport` 送达后端、再反序列化回来。未来换成 HTTP 只是换一个
`ILinkPocketTransport` 实现 + 一行装配代码，前端业务代码一行不改。

### 分层红线（务必遵守）

1. **前端任何文件不得 `using LinkPocket.Data`、不得出现 `new LinkPocketDbContext()`、
   不得引用 `LinkService` / `FolderService` / `BookmarkImporter` / `BookmarkExporter` /
   `LinkPocketBackupService`。** 需要数据 → 走 `AppServices.Api`。
2. **`Core/` 下不得出现 `System.Windows` / WPF 类型**（当前唯一例外已消除：
   favicon 的磁盘缓存 `FaviconStore` 在后端，`BitmapImage` 解码在前端 `FaviconService`）。
3. 前端 ViewModel 不得直接摸控件。所有界面操控统一走
   `Services/IUiCoordinator.cs` 接口（由 `MainWindow` 实现并在构造时注册到
   `UiCoordinator.Instance`）。

---

## 4. 代码地图

### 4.1 前端（WPF，约 9000 行）

| 文件 | 行数 | 职责 / 备注 |
|---|---|---|
| `App.xaml.cs` | 60 | 启动、全局异常兜底、调用 `AppServices.Initialize()` |
| `MainWindow.xaml` | 996 | 主窗口布局：`MainView`（主列表）、`DetailView`（详情）、`EditLinkView`（编辑）、`NavigationTabs`、侧栏、搜索面板 |
| `MainWindow.xaml.cs` | **2578** | **上帝类**：用 C# 代码手工创建几乎全部列表项控件（`CreateSidebarLinkRow` / `CreateMainListLinkCard` / `RenderFolderNode` / 搜索卡片等），用 4 个 `Dictionary<string, Border>` 手工同步选中态 |
| `Views/ToolsPage.xaml.cs` | 1136 | 工具页（去重），同样是代码建 UI |
| `Views/SettingsPage.xaml.cs` | 422 | 设置页：书签 HTML 导出/导入、日志清理、重置数据库 |
| `Views/BackupPanel.xaml.cs` | 260 | `.lpbackup` 导出/导入面板（含进度遮罩） |
| `Views/TrashPage.xaml.cs` | 252 | 回收站页 |
| `Views/SmartListsPage.xaml.cs` | 256 | 智能列表页 |
| `Views/AddLinkDialog.xaml(.cs)` | 47 | **死代码**：无任何引用（`AddLinkViewModel` 仅被它使用），仅作历史保留 |
| `ViewModels/MainViewModel.cs` | 1275 | 主导航、文件夹树、详情/编辑页状态、搜索、删除、粘贴。**通过 `AppServices.Api` 访问后端** |
| `ViewModels/LinkViewModel.cs` | 457 | 链接列表（含已失效的内存撤销栈，见技术债） |
| `ViewModels/RecycleBinViewModel.cs` | 136 | 回收站数据与选择状态 |
| `ViewModels/SmartListViewModel.cs` / `SmartListResultViewModel.cs` | 97 / 125 | 智能列表卡片与结果 |
| `ViewModels/FolderNode.cs` | 71 | 侧栏/主列表的文件夹树节点 UI 模型 |
| `ViewModels/AddLinkViewModel.cs` | 261 | 添加链接（仅被死代码对话框使用） |
| `Services/IUiCoordinator.cs` | 53 | **VM ↔ View 唯一契约**（17 个方法：页面切换、详情面板、侧栏/主列表刷新、删除确认对话框等） |
| `Services/AppServices.cs` | 32 | 组合根（见上） |
| `Services/FaviconService.cs` | 176 | 前端 favicon 渲染：内存 BitmapImage 缓存 + 解码；磁盘缓存委托给后端 `FaviconStore` |
| `Managers/LinkNavigator.cs` | 204 | 按 ID 跳转并滚动定位（依赖 `Dispatcher`，含 `Task.Delay` 时序编排） |
| `Managers/SelectionManager.cs`（**在 Core**）/ `ClipboardManager.cs`（**在 Core**） | 206 / 63 | 选中状态、应用内复制/剪切/粘贴（纯逻辑，已归后端） |

### 4.2 后端（Core，约 3600 行）

| 文件 | 行数 | 职责 |
|---|---|---|
| `Core/Api/ILinkPocketApi.cs` | 77 | **契约**：29 个方法（前端可用能力的全集） |
| `Core/Api/Dtos.cs` | 99 | 通信 DTO：`LinkDto` `FolderDto` `FolderContentsDto` `PagedLinksDto` `TrashEntryDto` `MetadataDto` `LinkCountsDto` `BackupImportDto` |
| `Core/Api/LinkPocketApi.cs` | 408 | 契约实现：组合各 Service、实体→DTO 映射、搜索排序逻辑、数据库重建 |
| `Core/Api/Transport.cs` | 243 | `ILinkPocketTransport` + `InProcessTransport` + `LinkPocketApiDispatcher`（29 个方法路由）+ 强类型调用扩展 |
| `Core/Api/TransportedLinkPocketApi.cs` | 135 | 传输代理（前端实际持有的对象） |
| `Core/Services/LinkService.cs` | 514 | 链接数据访问、元数据抓取、访问计数 |
| `Core/Services/FolderService.cs` | 364 | 文件夹树、移动、深拷贝、循环检测、删除级联 |
| `Core/Services/LinkPocketBackupService.cs` | 543 | `.lpbackup`（zip: manifest.json + data.json + favicons）导入导出 |
| `Core/Services/BookmarkImporter.cs` / `BookmarkExporter.cs` | 285 / 148 | 浏览器书签 HTML 解析 / 生成 |
| `Core/Services/FaviconStore.cs` | 114 | favicon 磁盘缓存与下载（纯后端，无 UI 类型） |
| `Core/Services/Logger.cs` | 71 | 按天日志 |
| `Core/Data/LinkPocketDbContext.cs` + `Entities/` | 55 + 162 | EF Core 上下文（表名/索引/关系配置）与三个实体 |
| `Core/Managers/SelectionManager.cs` / `ClipboardManager.cs` | 206 / 63 | 选中状态、应用内剪贴板 |

---

## 5. 协议参考

### 信封格式（JSON-RPC 2.0 风格）

请求：

```json
{ "id": "任意字符串", "method": "folders.contents", "params": { "folder_id": "123", "sort_by": "title", "sort_order": "asc" } }
```

成功响应：`{ "id": "...", "result": { ... } }`
失败响应：`{ "id": "...", "error": { "code": -32000, "message": "文件夹 123 不存在" } }`

约定：**所有 params 与 DTO 字段均为 snake_case**；缺参数走默认值，缺必填参数报 `-32602`；
未知方法报 `-32601`。前端侧统一由 `TransportExtensions.InvokeAsync` 解包，失败抛 `LinkPocketApiException`。

### 方法清单（29 个）

| 方法 | 参数（snake_case） | 返回 |
|---|---|---|
| `folders.contents` | `folder_id?`（null/"0"=根）、`sort_by`、`sort_order`、`page`（默认 1）、`per_page`（默认 0=不分页，一次取全部） | `FolderContentsDto` |
| `folders.tree` | — | `FolderDto[]`（扁平，含 `parent_id`） |
| `folders.breadcrumb` | `folder_id?` | `string[]`（`["全部书签","A","B"]`） |
| `folders.create` | `name`, `parent_id?` | `FolderDto` |
| `folders.update` | `id`, `name?`, `description?`, `parent_id?` | `FolderDto` |
| `folders.delete` | `id`, `cascade`（默认 `move_to_parent`；可选 `trash_links`/`target`）, `target_list_id?` | null |
| `folders.move` | `folder_id`, `target_parent_id?` | null |
| `folders.copy` | `folder_id`, `target_parent_id?` | 新文件夹 ID |
| `folders.would_create_cycle` | `folder_id`, `target_parent_id` | bool |
| `folders.update_sort` | `parent_id?`, `item_ids[]` | null |
| `links.list` | `list_id?`, `search?`, `is_important?`, `date_from?`, `date_to?`, `sort_by`, `sort_order`, `page`, `per_page` | `PagedLinksDto` |
| `links.all` | — | `LinkDto[]`（全部活动链接） |
| `links.root` | `sort_by`, `sort_order`, `per_page` | `LinkDto[]`（未归类链接） |
| `links.create` | `url`, `title?`, `description?`, `list_id?`, `is_important?`, `auto_fetch_metadata?`, `favicon_url?` | `LinkDto` |
| `links.update` | `id`, `url?`, `title?`, `description?`, `list_id?`, `is_important?`, `favicon_url?` | `LinkDto` |
| `links.trash` | `id` | null（软删除到回收站） |
| `links.record_visit` | `id` | null |
| `trash.list` | — | `TrashEntryDto[]` |
| `trash.restore` | `link_id` | `LinkDto` |
| `trash.purge` | `link_id` | null（彻底删除） |
| `search` | `query`, `search_title`, `search_url`, `search_description`, `search_path`, `sort_by`, `sort_order` | `LinkDto[]` |
| `smartlist` | `kind`（`recently_added`/`recently_visited`/`recently_edited`/`most_visited`）, `limit` | `LinkDto[]` |
| `meta.fetch` | `url` | `MetadataDto?`（title/description/favicon_url） |
| `stats.counts` | — | `LinkCountsDto`（`total`/`trash`/`root_level`/`by_folder`） |
| `export.bookmarks_html` | `output_path` | 路径 |
| `import.bookmarks_html` | `file_path` | 导入条数（int） |
| `backup.export` | `output_path` | null |
| `backup.import` | `file_path` | `BackupImportDto` |
| `settings.reinit_db` | `reset_data`（true=连数据一起删） | null |

### 为资源管理器式 UI 准备的关键接口

`folders.contents` 是这次 UI 重构的核心数据源，返回：

```json
{
  "folder_id": "123",              // "0" 表示根（全部书签）
  "folder_name": "前端资源",
  "sub_folders": [ { "id": "456", "name": "CSS", "parent_id": "123", "link_count": 7 } ],
  "links": [ { "id": "...", "url": "...", "title": "...", "favicon_url": "...", "created_at": "..." } ],
  "breadcrumb": ["全部书签", "技术", "前端资源"],
  "total_link_count": 7,
  "current_page": 1,               // P3 新增：当前页码（从 1 开始）
  "per_page": 20,                  // P3 新增：每页链接数；0 表示未启用分页
  "last_page": 1                   // P3 新增：链接总页数（按本目录实际链接查询计算）
}
```

一次调用即可渲染"当前目录页"（子文件夹 + 链接 + 面包屑），无需前端拼装。

**计数语义（P3 确认）**：`total_link_count` 只统计**直接子链接**，不递归统计子文件夹；
子文件夹的书签数看 `sub_folders[].link_count`。UI 上的"书签数"以此为准。

### 事件推送（P3 新增）

后端在数据变更后会通过 `ILinkPocketTransport.EventReceived` 推送 JSON 事件，
前端（`MainViewModel`）已订阅并做 300ms 防抖刷新当前视图：

```json
{ "event": "links.changed", "data": { "link_id": "...", "list_id": "..." }, "at": "2026-09-15T..." }
```

| 事件 | 触发时机 |
|---|---|
| `links.changed` | 链接创建/更新/访问/删除级联、HTML 导入、备份导入、重置数据库 |
| `folders.changed` | 文件夹创建/更新/删除/移动/复制/排序、HTML 导入、备份导入、重置数据库 |
| `trash.changed` | 移入回收站、恢复、彻底删除、重置数据库 |

注意：现阶段事件链与既有 `EventHandler` 链**并存**（防抖去重），P4/P6 再逐步替换旧链。

---

## 6. 数据模型

SQLite 三张表（EF Core `EnsureCreated()` 建库，**无迁移体系**）：

**links**（`Core/Data/Entities/Link.cs`）

| 列 | 说明 |
|---|---|
| `link_id` (PK, 20) | 16 位随机 Base62 字符串 |
| `url` (2048, 索引) / `title` (255) / `description` (text) / `favicon_url` (512) | |
| `list_id` (20, FK→folders, 可空) | 空 = 根级"全部书签" |
| `last_visited_at` (索引) / `visit_count` / `is_important` (索引) | |
| `created_at` / `updated_at` | **均为 UTC** |

**folders**（`Core/Data/Entities/Folder.cs`）：`folder_id` (PK, 20)、`name` (255)、`description`、
`parent_id` (自引用, 可空)、`link_count`（**不可靠，见技术债**）、`sort_order`、时间戳。

**trashed_links**（`Core/Data/Entities/TrashedLink.cs`）：以 `link_id` 为主键，字段与 links 基本相同，
外加 `deleted_at`。**设计说明：恢复时不还原原文件夹（`RestoreLinkAsync` 固定 `ListId = null`），
这是产品上的有意设计，不要"顺手修"**。

---

## 7. 已完成的重构里程碑（不要重复做）

1. **`IUiCoordinator` 解耦**（`fb124a2`）
   ViewModel 里原先有 20+ 处 `Application.Current.MainWindow is MainWindow mw` 直接操控控件，
   现已全部改为走 `IUiCoordinator` 接口。删除文件夹的确认对话框也从 VM 迁到了 `MainWindow`。
   → 对新 UI 的意义：**新窗口只需实现这 17 个方法，全部业务逻辑可直接复用。**

2. **前后端分离第一步**（`bb83a04`）
   新增 `LinkPocket.Core`（net8.0，零 UI 依赖），Data/Models/Services/Managers 整体迁入；
   favicon 缓存拆成后端 `FaviconStore` + 前端 `FaviconService`；
   建立 29 个方法的 JSON-RPC 协议 + `InProcessTransport`。

3. **前后端分离第二步**（`5338843`）
   前端全部改走协议：`MainViewModel` / `LinkViewModel` / `RecycleBinViewModel` /
   `SmartList(Result)ViewModel` / `AddLinkViewModel` / `BackupPanel` / `SettingsPage` 均不再持有
   DbContext 或 Service。**共享单例 DbContext 的并发隐患随之消除**（现在后端只有一个上下文，
   且所有访问都串行经过协议层）。
   同时修掉两个真 bug：备份/导出时间戳被 `DateTime.TryParse` 转成本地时间（Unix 时间戳偏移）；
   分发器误用 `WrapVoid` 吞掉 `import.bookmarks_html` 的返回条数。

4. **P3 后端补齐"文件管理器"能力**
   `folders.contents` 支持分页（`page`/`per_page`，`per_page=0` 保持旧行为）；
   `total_link_count` 语义确认（只统计直接子链接）并补充 DTO 分页字段
   （`current_page`/`per_page`/`last_page`）；
   事件推送落地：`ILinkPocketEventSource` + `InProcessTransport.EventReceived`，
   后端推送 `links.changed` / `folders.changed` / `trash.changed`，
   前端 `MainViewModel` 订阅并防抖刷新（与旧事件链并存）；
   冒烟测试固化为 `tests/ProtocolSmoke`（dotnet run 即可运行）。

---

## 8. 当前 UI 的工作方式（将被本次重构替换）

理解现状只需记住三点：

1. **侧栏是一棵可展开的文件夹树**（`MainWindow.RenderFolderNode` / `RefreshSidebar`），
   点击文件夹 → 右侧主列表显示该文件夹的链接；主列表内部**再次**用"卡片向下展开"的方式
   展示子文件夹（`RenderMainListFolderNode`），展开态存在 `_mainExpandedFolders` / `_sidebarExpandedFolders`。
2. **所有列表项都是 C# 代码现场 new 出来的**（`Border` / `StackPanel` / `TextBlock`），
   选中态靠 `_sidebarLinkBorders` / `_mainListCardBorders` / `_sidebarFolderBorders` /
   `_mainListFolderBorders` 四个字典手工改 Border 颜色。
3. **渲染时同步阻塞**：`RenderFolderNode` / `RenderMainListFolderNode` 内有
   `viewModel.GetRootLevelLinksAsync().GetAwaiter().GetResult()`（`MainWindow.xaml.cs`
   第 870、873、1783、1786 行，共 4 处），在 UI 线程上等数据库。这是重构要一并消灭的重点。

导航事件：`MainViewModel.CurrentNavId`（links/search/smartlists/tools/trash/settings）
+ `OnNavigatedToSearch` / `OnSearchRefreshRequested` / `OnToolsDataChanged` 等事件。
`Managers/LinkNavigator.cs` 负责"按 ID 跳转到某链接并滚动到可见"，依赖 `Dispatcher` 与
`Task.Delay` 时序，重构时需要重新设计（建议改为"进入目标所在目录 + 选中项"的语义）。

---

## 9. 目标 UI 规格：资源管理器 / 网盘式

> 一句话：**取消"卡片向下展开"，改为"进入目录"的逐级浏览。**

### 9.1 布局（建议）

```
┌──────────────────────────────────────────────────────────────────────────┐
│ [←][→][↑]  全部书签 › 技术 › 前端资源          [搜索框]      [视图切换]  │  导航栏
├───────────────┬──────────────────────────────────────────────────────────┤
│ 快速访问       │  名称                 书签数   修改时间                  │  列头
│  全部书签      │  📁 CSS                  12     2 天前                   │
│  收藏夹        │  📁 JavaScript            8     1 周前                   │  内容区
│ 回收站         │  🔗 MDN Web Docs          —     昨天      ⭐             │
│ 智能列表       │  🔗 Can I use             —     3 天前                  │
│               │                                                          │
│ (可选)文件夹树 │                                                          │
├───────────────┴──────────────────────────────────────────────────────────┤
│ 共 20 项（3 个文件夹 / 17 个书签）                    状态栏              │
└──────────────────────────────────────────────────────────────────────────┘
```

### 9.2 交互规格（逐条可验收）

| 行为 | 规格 |
|---|---|
| 进入文件夹 | **双击**文件夹行进入（与资源管理器一致）；同时提供**单击选中**。可选：设置项切换为"单击进入"（网盘常见） |
| 返回上级 | 工具栏 `↑` 按钮；`Backspace` |
| 后退/前进 | 工具栏 `←` `→`；`Alt+←` / `Alt+→`；需要**导航历史栈**（存 folderId，不是整页快照） |
| 面包屑 | 显示从"全部书签"到当前目录的路径，**每一级可点击跳转**；过长时中间折叠为 `…` 下拉 |
| 地址栏 | 面包屑可切换为可编辑文本框，支持粘贴文件夹 ID 直接跳转（复用现有 `LinkNavigator` 的 ID 跳转能力） |
| 打开链接 | 双击链接行 → 默认浏览器打开并 `links.record_visit` |
| 选中 | 单击选中；`Ctrl+单击` 多选；`Shift+单击` 范围选择；`Ctrl+A` 全选；空白处单击取消选择 |
| 键盘 | `Enter` 打开/进入、`Delete` 移入回收站、`F2` 重命名（文件夹）、`Ctrl+C/X/V` 复制/剪切/粘贴（走现有 `ClipboardManager` 语义）、`Ctrl+Z` 撤销（见技术债） |
| 右键菜单 | 文件夹：打开 / 重命名 / 复制 / 剪切 / 粘贴 / 删除 / 复制 ID；链接：打开 / 编辑 / 复制 URL / 剪切 / 删除 / 复制 ID |
| 拖拽 | 拖动链接/文件夹到左侧树或面包屑的某一级 = 移动（`folders.move` / `links.update(list_id)`）；拖动时校验循环（`folders.would_create_cycle`）并给出禁止光标 |
| 排序 | 点击列头切换排序字段（名称/书签数/修改时间…），再次点击反转方向；排序参数即 `folders.contents` 的 `sort_by`/`sort_order` |
| 视图模式 | 列表（默认）/ 大图标（仅链接，显示 favicon 大图）；模式持久化到设置 |
| 空状态 | 空文件夹显示"此文件夹为空，拖入书签或从浏览器导入" |
| 搜索 | 在导航栏搜索框输入 → 结果以"平铺列表"替换内容区（不离开当前目录语义），清空后回到目录视图 |
| 状态栏 | 当前目录条目统计；选中若干项时显示"已选中 N 项" |
| 多窗口/分栏（可选） | 后续可加"双栏浏览"，非首期目标 |

### 9.3 技术规格

- **数据源**：一切靠 `folders.contents`（+ `folders.tree` 供可选树、`folders.breadcrumb` 也可由
  `contents.breadcrumb` 直接得到）。
- **导航状态**：前端维护 `currentFolderId` + `Stack<string> back` / `forward`。
  切换目录 = 一次 `folders.contents` 调用 + 重新渲染列表。
- **渲染方式**：必须改为 **XAML `ItemsControl` + `DataTemplate` + 绑定**，
  行视图模型（如 `FolderRowViewModel` / `LinkRowViewModel`）暴露 `IsSelected` 等属性，
  由 `ItemContainerStyle` 驱动选中态。**禁止继续用 C# new 控件 + 字典改颜色**。
- **异步**：所有加载走 `await`，禁止 `GetAwaiter().GetResult()`。
- **导航与 View 的关系**：新页面的"显示哪一页"仍通过 `IUiCoordinator` 暴露给 ViewModel
  （若新 UI 更换窗口类，只需重新实现该接口）。

---

## 10. 分阶段路线图（P3 → P8）

> 每阶段独立提交、可独立验收。**任何阶段结束时必须：两个项目编译 0 错误 0 警告 +
> 协议冒烟测试通过 + 单独 Git 提交。**

### P3 —— 后端补齐"文件管理器"所需能力（已完成 ✅）
- [x] `folders.contents` 增加分页参数（`page`/`per_page`，默认不分页保持旧行为）
- [x] 空目录/链接计数语义确认：`FolderContentsDto.total_link_count` 只统计**直接子链接**
      （子文件夹的链接不递归统计；UI 上"书签数"含义见第 5 节说明）
- [x] 事件推送：实现 `ILinkPocketTransport.EventReceived` 的进程内版本，
      后端在数据变更后推 `links.changed` / `folders.changed` / `trash.changed`；
      前端（`MainViewModel`）订阅并防抖刷新当前目录（与旧事件链并存，P4/P6 再替换）
- [ ] 备份/导入的进度回调协议化（可选，见技术债）
- **验收**：`tests/ProtocolSmoke` 冒烟用例通过（分页、计数语义、三类事件、错误通道）；旧 UI 行为不变。

### P4 —— 导航框架搭建（中等）
- [ ] 新建 `Views/Browser/` 目录，落地 `BrowserView`（导航栏 + 内容区 + 状态栏三段式）
- [ ] 实现 `NavigationController`（`currentFolderId` + 后退/前进历史栈 + 面包屑计算）
- [ ] `BrowserView` 用 `ItemsControl` + `DataTemplate` 渲染行，行 VM 支持 `IsSelected`
- [ ] 在主窗口中以"新页面"形式挂载，通过 `IUiCoordinator` 暴露显示/隐藏
- **验收**：可在新界面里进入文件夹、返回上级、双击打开链接；旧界面仍可用（并存）。

### P5 —— 资源管理器式浏览功能完善（大）
- [ ] 面包屑可点击跳转 + 可编辑地址栏（ID 跳转）
- [ ] 多选（Ctrl/Shift）、全选、状态栏统计
- [ ] 排序（列头点击）、视图模式（列表/大图标）与持久化
- [ ] 空状态、加载态、错误态
- [ ] 删除/重命名/新建文件夹等操作接入（走既有协议 + `IUiCoordinator` 的确认对话框）
- **验收**：第 9.2 节交互规格逐条通过；渲染路径无同步阻塞。

### P6 —— 旧交互退役与侧栏收敛
- [ ] 主列表的"卡片展开文件夹"逻辑整体删除（`RenderMainListFolderNode` 及其展开态集合）
- [ ] 侧栏树降级为"快速跳转"（点击 = 在新浏览器视图里打开该目录），不再承担导航职责
- [ ] 清理 `LinkNavigator` 中基于 `Task.Delay` 的滚动定位，改为"进入目标目录 + 选中行"
- [ ] 删除死代码：`Views/AddLinkDialog.*`、`ViewModels/AddLinkViewModel.cs`（若确认不用）
- **验收**：`MainWindow.xaml.cs` 明显瘦身（目标 < 1200 行）；无功能回归。

### P7 —— 体验与细节
- [ ] 右键菜单、拖拽移动（含循环校验与视觉反馈）
- [ ] 快捷键全集（`Delete` / `F2` / `Ctrl+C X V A Z` / `Backspace` / `Alt+←→`）
- [ ] favicon 懒加载与占位图、长标题省略、时间人性化显示（"2 天前"）
- [ ] 大列表虚拟化（`VirtualizingStackPanel`）与上千条链接的性能验证
- **验收**：1000+ 链接目录滚动流畅；快捷键与资源管理器习惯一致。

### P8 —— 新 UI 定稿（路线二选一，届时决策）
- 路线 A（保守）：继续 WPF，替换主题与视觉稿，删除旧 `MainWindow.xaml` 中的历史布局。
- 路线 B（Web）：`ASP.NET Core` 自宿主 + 新增 `HttpTransport`（协议不变）+ Web 前端
  （静态 SPA，打包为 WebView2 窗口或浏览器访问）。**后端 Core 一行不改**。
- **前置**：无论哪条路线，先把 29 个协议方法固化成自动化回归用例（见第 12 节）。
- **验收**：功能对等清单全绿；卸载旧界面代码。

---

## 11. 交接约定与红线

1. 依赖方向：`前端 → ILinkPocketApi（代理）→ ILinkPocketTransport → 后端`。
   前端不得直接引用 `LinkPocket.Data` 或任何 Service 实现类。
2. `Core/` 禁止引入 WPF（`System.Windows*`）。建议加 CI 检查：
   `grep -r "System.Windows" Core/ && exit 1`。
3. ViewModel 不得直接操作控件；需要界面动作 → 扩展 `IUiCoordinator` 接口
   （接口成员命名用动词短语：`ShowXxx` / `RefreshXxx` / `ConfirmXxx`）。
4. DTO 字段只增不删、语义变更要加新字段；协议方法名保持 `域.动作` 命名。
5. 提交信息用中文，格式 `<type>: <说明>`（`refactor` / `feat` / `fix` / `docs` / `chore`），
   正文列出关键改动与验证结果。
6. 每阶段结束必须跑：`dotnet build`（0 警告）+ 协议冒烟测试；改了 UI 的要人工过一遍第 13 节清单。

---

## 12. 测试与验证方法

### 12.1 协议冒烟测试（当前最有效的自动化手段）

冒烟测试已固化为 `tests/ProtocolSmoke`（不参与主项目编译，独立控制台项目）：

```bash
dotnet run --project tests/ProtocolSmoke
```

它用与前端完全相同的装配方式（LinkPocketApi → Dispatcher → InProcessTransport → TransportedLinkPocketApi），
逐项断言：目录分页、计数语义、三类变更事件、回收站闭环、错误通道。输出"全部通过"即验收。
测试库是测试 exe 目录下的独立 `linkpocket.db`，不会污染正式数据。

若要临时验证新协议方法，可参照该目录的 `Program.cs` 追加断言（历史做法是临时控制台项目，
现在直接改这个文件即可）。

### 12.2 人工验收清单（改 UI 后必过）

- [ ] 首次启动：根目录正常显示文件夹与根级链接
- [ ] 新建文件夹 → 进入 → 新建链接 → 返回上级，计数正确
- [ ] 双击打开链接 → 访问计数 +1、`last_visited` 更新
- [ ] 编辑链接（含 favicon 抓取）、复制/剪切/粘贴、删除进回收站、恢复、彻底删除
- [ ] 搜索（标题/URL/描述/路径四种开关）
- [ ] 智能列表四个入口
- [ ] 工具页去重与 ID 跳转
- [ ] 设置页：书签 HTML 导出与导入、日志清理、重置数据库（**高危：会删库，用副本验证**）
- [ ] `.lpbackup` 导出 → 导入到另一个库，数据完整
- [ ] 中文/特殊字符标题、超长 URL、无 favicon 网站、离线时的表现

---

## 13. 已知技术债与待决事项

| 级别 | 问题 | 位置 / 说明 |
|---|---|---|
| 高 | **`MainWindow.xaml.cs` 2578 行上帝类** | 代码建 UI + 字典管理选中态；P4–P6 的目标就是拆掉它 |
| 高 | **渲染路径同步阻塞** | `GetAwaiter().GetResult()` 在 UI 线程等数据库（`MainWindow.xaml.cs` 第 870/873/1783/1786 行，共 4 处） |
| 中 | `Folder.LinkCount` 字段不可靠 | 依赖未加载的导航属性，多为 0；**API 已改用实时统计 `GetCountsAsync().ByFolder`**，该列可视为废弃字段，重构时不要依赖它 |
| 中 | 文件夹 ID 生成碰撞风险 | `Folder.GenerateFolderId()` 只取 1..9999 作高 16 位，文件夹数量上百后生日碰撞概率上升；建议改为纯随机 64 位 Base62（注意已有数据的兼容） |
| 中 | 无数据库迁移 | 用 `EnsureCreated()`；加字段需删库重建。若后续要演进 schema，先引入 EF Migrations |
| 中 | 备份/导入进度条精度丢失 | 协议当前不支持进度回调，`BackupPanel` 只能显示不定进度（P3 可选补事件推送） |
| 中 | Ctrl+Z 撤销栈只改内存 | `LinkViewModel.SaveForUndo/Undo` 不落库，刷新即回退；要么删掉，要么改走协议 |
| 低 | `SettingsPage` 导出后用"读全文数 `<A HREF=`"校验 | 大文件低效，可改为解析/流式计数 |
| 低 | VS 启动未验证 | 本轮重构后**尚未实际启动 WPF 应用做人工验收**（仅编译 + 协议冒烟）。接手后请先跑一次 12.2 清单 |
| 低 | `Views/VaultPage.xaml` 孤立文件 | 已废弃的密码库功能残留，可直接删除 |
| 决策 | Web 化 or WPF 重画 | P8 时决定；两条路线的前置工作相同（协议回归用例） |

---

## 14. 常见坑

1. **`UiCoordinator.Instance` 在 `MainWindow` 构造函数中注册**。如果新的 UI 换了窗口类，
   记得同样注册（或在新的组合根里完成），否则 ViewModel 里的界面操控会静默失效
   （所有调用都是 `Ui?.Xxx()`，null 时不报错）。
2. **`AppServices.Initialize()` 必须在任何 ViewModel 构造之前**（现在在 `App` 构造函数里）。
3. **时间戳一律 UTC**：写入用 `DateTime.UtcNow`，显示用 `.ToLocalTime()`，
   解析外部字符串必须按 UTC 解释（`DateTimeOffset.TryParse(..., AssumeUniversal | AdjustToUniversal)`）。
   历史 bug 就是 `DateTime.TryParse` 把 `Z` 转成了本地时间。
4. **协议字段是 snake_case**，DTO 上靠 `[JsonPropertyName]` 映射；新增字段别忘了加特性。
5. **前端拿到的都是 DTO**，不是 EF 实体。`MainViewModel` 的公开查询方法返回 `LinkDto`，
   需要 UI 模型时用 `MapToLinkItem(LinkDto)` 转换（`MainViewModel` 内有现成实现）。
6. **不要"顺手修"回收站恢复不还原文件夹**：这是产品上确认过的有意设计。
7. 编译主项目会同时编译 `Core`（ProjectReference）；主项目 csproj 里有
   `<Compile Remove="Core/**/*.cs" />`，新增 Core 文件放在 `Core/` 下即可，不会被主项目重复编译。

---

## 15. 文档版本

- 版本：v1.1（P3 完成；对应代码为 P3 提交）
- 关联文档：[`DECOUPLING_PLAN.md`](DECOUPLING_PLAN.md)（前后端分离的原始规划与协议设计）、
  [`tests/ProtocolSmoke/`](tests/ProtocolSmoke/)（协议冒烟测试，`dotnet run --project tests/ProtocolSmoke`）
- 更新要求：每次完成一个阶段后，更新第 7 节（里程碑）、第 10 节（勾选进度）、第 13 节（技术债）。

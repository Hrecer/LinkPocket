# LinkPocket.Modules.Favicon

图标磁盘缓存与后台预取。黑盒：除 `FaviconModule.CreateHandlers()` 外全部 `internal`。

## 职责边界

- **做**：按 URL 解析图标地址、落磁盘缓存、提供缓存统计、把缺失图标排进后台预取队列。
- **不做**：**解码与显示**——`BitmapImage` 等 WPF 类型属 `UIKit.FaviconImageService`，引擎侧永不出现界面类型。
  也不做真正的联网调度策略（并发/退避在队列内固定为并发 4、失败跳过下次再试）。

## 对外命令（2）

| 命令 | 类型 | 要点 |
|---|---|---|
| `favicon.cache_stats` | 查询 | 缓存文件数 / 总字节 / 缓存目录 |
| `favicon.prefetch` | 变更 | 把缺失图标入队（`link_ids` 缺省 = 全库缺图标者）；**立即返回入队数**，写闸只持入队瞬间 |

## 内部组件

- `FaviconCache`：地址解析（相对 URL → 站点 favicon 约定路径）、缓存文件路径（按解析后地址取哈希）、`EnsureCachedAsync`。
- `PrefetchQueue`：进程内静态队列，并发 4（`SemaphoreSlim`）、**按解析后地址去重**、失败跳过；
  fire-and-forget（异常被吞，图标缺失不阻断任何业务），排水任务空闲自愈。

## 关键口径（预取后台化）

- 目录加载**不等待**任何网络：界面只读缓存，缺图标就显示占位，预取在后台补。
- 入队去重 + 已有缓存文件不重复入队 → 反复触发 prefetch 不会产生重复网络请求。
- 入队数只统计"本次新增"，因此可安全用于"还要拉几张图"的进度播报。

## 测试

- `ModulesTests.cs` → `FaviconModuleTests`（缓存统计）
- `CommandCoverageTests.cs` → `FaviconCoverageTests`（指定链接入队 / 无图标地址入队 0 / 未知链接报错 / 全库入队 + 变更集）
  —— 用 `http://127.0.0.1:1/...` 制造"必然失败且无 DNS 依赖"的预取目标，测试不留网络副作用。

## 复用点

缓存路径解析与 `links.create` 的 `favicon_url` 参数、`links.metadata_fetch` 的 favicon 解析同源；
UI 侧 `UIKit.FaviconImageService` 只消费本模块的磁盘缓存，不重复解析地址。

# LinkPocket.Mcp · 外部 Agent 网关

把 LinkPocket 的能力以 **MCP（Model Context Protocol，stdio）** 暴露给外部 Agent（Claude Desktop、Cline、
WorkBuddy 等）。外部 Agent 由此可以**看数据、改数据、空跑**——与界面、内置助手**同一套命令、同一条引擎管道**。

- 产物：`linkpocket-mcp`（`dotnet src/LinkPocket.Mcp/bin/<cfg>/net8.0/linkpocket-mcp.dll`）
- 依赖面：`LinkPocket.Contracts` + `LinkPocket.Composition` + `LinkPocket.Cli`

## 为什么不是"又一套能力"

| 关心的 | 实现 |
|---|---|
| **工具清单** | 来自契约层 `AiToolCatalog.BuildAll()`——与内置助手发给模型的工具面**逐字同源**（同名、同描述、同参数 schema） |
| **工具调用** | 进程内复用 `LinkPocket.Cli.CliRunner`——与人在终端敲命令**同一条执行路径**（同参数绑定、同引擎管道、同输出） |
| **数据/变更语义** | 引擎管道本身（撤销、审计、事件、写锁、破坏性令牌），网关不自持任何策略 |

因此不存在"外部阉割版"：引擎有 81 条命令，网关就暴露 81 条。

## 启动

```powershell
dotnet src/LinkPocket.Mcp/bin/Release/net8.0/linkpocket-mcp.dll --db <库路径>
```

库路径缺省解析与命令行客户端一致（`--db` → `LINKPOCKET_DB` → 从当前目录上溯找 `linkpocket.db` → 程序目录）。

## 协议面

| 方法 | 说明 |
|---|---|
| `initialize` | 回协议版本（`2025-06-18`）、能力（tools / resources）与 serverInfo |
| `tools/list` | 引擎全部命令（含 `inputSchema` 与 `annotations`：`readOnlyHint` / `destructiveHint` / `openWorldHint`） |
| `tools/call` | `{name, arguments}` → 一次命令行执行；返回 `content[].text`（JSON），失败带 `isError` |
| `resources/list` / `resources/read` | 只读数据视图（见下） |
| `resources/templates/list` | 按 ID 取单个链接 / 目录页 / 回收站单元 |
| `ping` | 心跳 |

**工具参数的两个约定**：

- 参数名 = 命令描述符的 snake_case 名；数组/对象按 JSON 字面量传。
- 每条工具都额外接受布尔参数 **`_dry_run`**：空跑，执行但不提交，返回将发生什么（零副作用）。

## 确认口径

网关对每次调用自动带 `--yes`（把网关挂到自己的 Agent 上即代表用户授权），
破坏性命令由引擎的两阶段令牌在同一次调用内自动完成；需要先看影响面时用 `_dry_run`。
调用方身份固定为 `agent`（不带会话 ID = 宿主自有调用，零能力门约束；内置助手改数据期间的写入冻结对它同样生效）。

## 数据资源

| URI | 内容 |
|---|---|
| `linkpocket://docs` | 全部命令的参考文档（Markdown） |
| `linkpocket://commands` | 机器可读的命令清单（JSON） |
| `linkpocket://snapshot` | 全量快照：目录树 + 全部链接 + 回收站 + 统计 + 诊断 |
| `linkpocket://stats` | 统计（按目录计数 / 回收站 / 根级） |
| `linkpocket://tree` | 目录树 |
| `linkpocket://links` | 全部链接（含归属目录） |
| `linkpocket://trash` | 回收站（含原位置） |
| `linkpocket://diagnostics` | 版本 / 表计数 / 缓存读数 |
| `linkpocket://link/{id}` · `linkpocket://folder/{id}` · `linkpocket://trash/{id}` | 单个链接 / 目录页 / 回收站单元 |

## 客户端配置

```json
{
  "mcpServers": {
    "linkpocket": {
      "command": "dotnet",
      "args": [
        "G:/LinkPocket 网站管理器开发/开发目录/linkpocket/src/LinkPocket.Mcp/bin/Release/net8.0/linkpocket-mcp.dll",
        "--db", "G:/LinkPocket 网站管理器开发/开发目录/linkpocket/src/LinkPocket.App/bin/Debug/net8.0-windows/linkpocket.db"
      ]
    }
  }
}
```

## 输出纪律

**stdout 只承载 MCP 协议帧**；日志走 stderr（`EngineHost(logToStderr: true)`）。stdin 关闭即退出。

## 组合根

与命令行客户端共用 `LinkPocket.Composition.EngineHost`；网关自身只做"MCP 帧 ↔ 命令行"的翻译，
不含任何命令实现与安全策略。

# LinkPocket.Cli · 命令行客户端

在 PowerShell / bash 里直接驱动书签库的**控制台程序**（无窗口、不进界面）。
它不新增任何能力：一切命令都经共享组合根装配的引擎，走界面与内置助手**同一条管道**
（校验 → 幂等 → 能力门 → 写闸 → 事务 → 撤销 → 事件 → 审计），因此撤销、审计、事件、写锁对它同样生效。

- 产物：`linkpocket`（`dotnet src/LinkPocket.Cli/bin/<cfg>/net8.0/linkpocket.dll`）
- 依赖面：`LinkPocket.Contracts` + `LinkPocket.Composition`（不引 Engine / Data / Modules.\*）

## 用法

```powershell
linkpocket describe [类别]                 # 列出全部命令（业务 + 编排 + 批）
linkpocket <命令> [--参数 值 ...]           # 直接调用，如 linkpocket links.list --per_page 20
linkpocket <域> <动作> [--参数 值 ...]      # 两段写法等价（links list == links.list）
linkpocket call <命令> --args '<JSON>'     # 复杂/嵌套入参走 JSON
linkpocket docs                            # 命令大全（Markdown，与 catalog/COMMANDS.md 同源）
linkpocket snapshot [--out 文件]            # 一次取回全量数据视图（树 + 链接 + 回收站 + 统计 + 诊断）
linkpocket dump --out <目录>                # 导出全部数据（bookmarks.html / links.json / links.csv / .lpbackup）
linkpocket help [命令]                      # 帮助
```

参数名 = 命令描述符里的 snake_case 名（`linkpocket describe` 可查）。标量按参数类型自动转换；
集合参数接受 JSON 数组或逗号分隔（`--ids a,b,c`）；带 Schema 片段的复杂参数按 JSON 传。

## 全局选项

| 选项 | 作用 |
|---|---|
| `--db <路径>` | 指定库文件（缺省：从当前目录向上找第一个 `linkpocket.db`，找不到则用程序目录） |
| `--json` | 结构化 JSON 输出（脚本与外部 Agent 用；表格是人对人的缺省形态） |
| `--all` | 表格不截断（缺省最多 100 行） |
| `--dry-run` | 空跑：执行但不提交，返回将发生什么（零副作用，且**不需要确认**） |
| `--yes` | 破坏性命令的确认（不给则只报影响面并以退出码 3 结束） |
| `--out <文件>` | 把 JSON 结果写入文件 |
| `--verbose` | 引擎日志打到 stderr |
| `--version` | 版本 |

## 库路径解析（优先级由高到低）

1. 命令行 `--db <路径>`
2. 环境变量 `LINKPOCKET_DB`
3. 从当前目录向上回溯，取第一个 `linkpocket.db`
4. 程序目录 `linkpocket.db`（与 WPF 宿主同一用户数据约定：同一文件无缝接管，零迁移）

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 引擎/运行错误（`LP.ENG.*` / `LP.STATE.*` / `LP.SEC.*` 等） |
| 2 | 用法或校验错误（未知命令、`LP.VAL.*` 类型不符） |
| 3 | 破坏性命令缺少 `--yes`（尚未执行，可加 `--yes` 重跑） |

## 输出纪律

- **stdout 只放结果**（表格或 JSON）；`# library:` 之类的诊断一行与日志一律走 **stderr**——脚本可安全逐行消费 stdout。
- 查询结果缺省按表格渲染（对象里的数组字段各自成表；不适合表格时自动回退 JSON），`--json` 强制 JSON。
- 变更结果人读形态 = `ok` + 人类摘要 + 受影响实体数 + 字段变更数；`--json` 给出 `{ok, data, audit_ref, changes}`，
  其中 `changes` 与 wire / 审计 / 事件**同一份载荷投影**（`ChangeSetPayload`，diff 上限 2000 条 + 截断标记）。

## 与外部 Agent 的关系

命令行执行器 `CliRunner` 是 **public 的进程内 API**：外部 Agent 网关（`LinkPocket.Mcp`）在进程内复用它，
把每次工具调用翻译成一次命令行执行。因此"外部 Agent 用的命令"与"人在终端敲的命令"是同一条路径，
不存在第二套实现；接入方式见 [`LinkPocket.Mcp/TOOL.md`](../LinkPocket.Mcp/TOOL.md) 与
工作区 `内部资产/文档/EXTERNAL-AGENT.md`。

## 组合根

复用共享 `LinkPocket.Composition.EngineHost`（库路径解析 + `EngineComposer` 全量装配 + 客户端门面 + 目录 +
会话管理器）。它与 WPF 宿主、无头宿主的唯一差别是输出落点，装配口径完全一致。

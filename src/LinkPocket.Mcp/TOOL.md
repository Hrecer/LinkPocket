# LinkPocket.Mcp - external-agent gateway

Exposes LinkPocket capabilities to external agents (Claude Desktop, Cline, WorkBuddy, ...) over
**MCP (Model Context Protocol, stdio)**. An external agent can thus **read data, change data and dry-run** -
through **the same commands and the same engine pipeline** as the UI and the built-in assistant.

- Artifact: `linkpocket-mcp` (`dotnet src/LinkPocket.Mcp/bin/<cfg>/net10.0/linkpocket-mcp.dll`)
- Dependency face: `LinkPocket.Contracts` + `LinkPocket.Composition` + `LinkPocket.Cli`

## Why this is not "yet another capability layer"

| Concern | Implementation |
|---|---|
| **Tool list** | Comes from the contracts layer `AiToolCatalog.BuildAll()` - **byte-for-byte the same** tool face the built-in assistant sends to its model (same names, descriptions, parameter schemas) |
| **Tool calls** | Reuses `LinkPocket.Cli.CliRunner` in-process - the **same execution path** as a human typing commands (same argument binding, same engine pipeline, same output) |
| **Data / change semantics** | The engine pipeline itself (undo, audit, events, write lock, destructive confirmations); the gateway holds no policy of its own |

So there is no "external crippled edition": the engine has 81 commands, the gateway exposes 81.

## Starting it

```powershell
dotnet src/LinkPocket.Mcp/bin/Release/net10.0/linkpocket-mcp.dll --db <database path>
```

Default library-path resolution matches the command-line client (`--db` -> `LINKPOCKET_DB` -> walk up from
the current directory for `data\linkpocket.db` -> `data\linkpocket.db` under the program root; single
implementation = `LinkPocket.Contracts.AppPaths`).

## Protocol surface

| Method | Description |
|---|---|
| `initialize` | Returns protocol version (`2025-06-18`), capabilities (tools / resources) and serverInfo |
| `tools/list` | All engine commands (with `inputSchema` and `annotations`: `readOnlyHint` / `destructiveHint` / `openWorldHint`) |
| `tools/call` | `{name, arguments}` -> one CLI execution; returns `content[].text` (JSON), failures carry `isError` |
| `resources/list` / `resources/read` | Read-only data views (see below) |
| `resources/templates/list` | Fetch a single link / folder page / trash unit by ID |
| `ping` | Heartbeat |

**Two conventions for tool parameters**:

- Parameter names = the snake_case names in the command descriptors; arrays/objects are passed as JSON literals.
- Every tool additionally accepts the boolean parameter **`_dry_run`**: dry-run - execute without committing,
  report what would happen (zero side effects).

## Confirmation policy

The gateway automatically passes `--yes` on every call (attaching the gateway to your agent is the user's
authorization); for destructive commands the engine's two-phase token is completed within the same call.
Use `_dry_run` when you want to see the impact first.
The caller identity is fixed to `agent` (no session ID = host-owned call, zero capability-gate constraints;
the write freeze that applies while the built-in assistant is modifying data applies to it as well).

## Data resources

| URI | Content |
|---|---|
| `linkpocket://docs` | Reference documentation for all commands (Markdown) |
| `linkpocket://commands` | Machine-readable command list (JSON) |
| `linkpocket://snapshot` | Full snapshot: folder tree + all links + trash + stats + diagnostics |
| `linkpocket://stats` | Statistics (per-folder counts / trash / root level) |
| `linkpocket://tree` | Folder tree |
| `linkpocket://links` | All links (with their folder) |
| `linkpocket://trash` | Recycle bin (with original locations) |
| `linkpocket://diagnostics` | Version / table counts / cache readings |
| `linkpocket://link/{id}` · `linkpocket://folder/{id}` · `linkpocket://trash/{id}` | Single link / folder page / trash unit |

## Client configuration

```json
{
  "mcpServers": {
    "linkpocket": {
      "command": "dotnet",
      "args": [
        "<repo>/src/LinkPocket.Mcp/bin/Release/net10.0/linkpocket-mcp.dll",
        "--db", "<data>/linkpocket.db"
      ]
    }
  }
}
```

When installed from a delivery folder, point `command` at the self-contained
`tools\linkpocket-mcp.exe` instead (no .NET runtime needed; ready-to-edit example: `mcp.example.json`).

## Output discipline

**stdout carries MCP protocol frames only**; logs go to stderr (`EngineHost(logToStderr: true)`).
Closing stdin exits the process.

## Composition root

Shares `LinkPocket.Composition.EngineHost` with the command-line client; the gateway itself only translates
"MCP frames <-> command line" and contains no command implementations and no security policy.

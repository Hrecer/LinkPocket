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

So there is no "external crippled edition": the engine exposes 85 external commands
(66 business + 16 orchestration + 3 batch), and the gateway exposes all 85. The desktop host
additionally registers 3 **UI-only** search snapshot-session commands (88 in the in-app catalog);
they are stateful renderer internals and are deliberately not part of the external tool surface.
External reads are full by default: `search.links` / `search.explain` return all hits when
`per_page` is omitted, and `folders.overview` includes `tree_links`.

**Browser capabilities** (metadata render / SVG rasterization / human verification) come from a
local named-pipe bridge (same library path, current user only). The provider is the running desktop
app when available; otherwise the gateway starts the same `LinkPocket.exe` with `--capability-host`
on the first fetch that actually needs it (no main window, no database access, one provider per
library, own WebView2 profiles, exits when idle or when the gateway exits). Ordinary queries never
start it. If no provider can serve, metadata results report `human_verification_available=false` /
`browser_render_available=false` instead of pretending a window was shown.
Batch metadata (`links.metadata_fetch_batch`) never opens a window; retry only the items marked
`needs_human_verification` with `links.metadata_fetch interactive=true`.

External queries are optimized for **accuracy, not latency**: no query cache is used for
external callers, so results always reflect the latest committed database state. `search.links`
and `search.explain` default to all hits; pass `page` / `per_page` only when you deliberately
want a page. `folders.overview` defaults to `include_tree_links=true`; pass
`include_tree_links=false` for the lean projection.

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

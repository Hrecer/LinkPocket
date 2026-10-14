# LinkPocket.Cli - command-line client

A **console program** that drives the LinkPocket library from PowerShell / bash (no window, no UI).
It adds no capabilities of its own: every command goes through the engine assembled by the shared
composition root - the **same pipeline** as the UI and the built-in assistant
(validate → idempotency → capability gate → write gate → transaction → undo → events → audit),
so undo, audit, events and the write lock apply to it exactly as they do to the UI.

- Artifact: `linkpocket` (`dotnet src/LinkPocket.Cli/bin/<cfg>/net10.0/linkpocket.dll`)
- Dependency face: `LinkPocket.Contracts` + `LinkPocket.Composition` (no Engine / Data / Modules.*)

## Usage

```powershell
linkpocket describe [category]              # list every command (business + orchestration + batch)
linkpocket <command> [--param value ...]    # direct call, e.g. linkpocket links.list --per_page 20
linkpocket <domain> <verb> [--param ...]    # two-segment form is equivalent (links list == links.list)
linkpocket call <command> --args '<JSON>'   # complex/nested arguments go through JSON
linkpocket docs                             # full command reference (Markdown, same source as catalog/COMMANDS.md)
linkpocket snapshot [--out file]            # one-shot full data view (tree + links + trash + stats + diagnostics)
linkpocket dump --out <dir>                 # export all data (bookmarks.html / links.json / links.csv / .lpbackup)
linkpocket help [command]                   # help
```

Parameter names = the snake_case names in the command descriptors (check `linkpocket describe`).
Scalars are converted by the parameter type; collection parameters accept JSON arrays or
comma-separated lists (`--ids a,b,c`); parameters carrying a Schema fragment are passed as JSON.

## Global options

| Option | Meaning |
|---|---|
| `--db <path>` | Database file (default: first `data\linkpocket.db` walking up from the current directory, else `data\linkpocket.db` under the program root) |
| `--json` | Structured JSON output (for scripts and external agents; tables are the human default) |
| `--all` | Do not truncate tables (default: at most 100 rows) |
| `--dry-run` | Dry run: execute without committing, report what would happen (zero side effects, **no confirmation needed**) |
| `--yes` | Confirmation for destructive commands (without it: impact summary and exit code 3) |
| `--out <file>` | Write the JSON result to a file |
| `--verbose` | Engine logs to stderr |
| `--version` | Version |

## Library path resolution (highest priority first)

1. `--db <path>` on the command line
2. `LINKPOCKET_DB` environment variable
3. Walk up from the current directory for the first `<dir>\data\linkpocket.db`
   (running inside the data directory tree needs no argument)
4. `data\linkpocket.db` under the program root (program root = nearest directory containing a `data\`
   subdirectory or `LinkPocket.exe`; the CLI lives in `tools\`, so it takes the level above)

Single implementation = `LinkPocket.Contracts.AppPaths` (app / CLI / gateway / internal tools share it).
Since 4.0.0 the data area is always `data\`; the old "flat `linkpocket.db` in the program directory"
is **no longer recognized** (not detected, not reported; migration notes live in the workspace docs).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Engine/runtime error (`LP.ENG.*` / `LP.STATE.*` / `LP.SEC.*` ...) |
| 2 | Usage or validation error (unknown command, `LP.VAL.*` type mismatch) |
| 3 | Destructive command without `--yes` (nothing has run; re-run with `--yes`) |

## Output discipline

- **stdout carries results only** (tables or JSON); diagnostics such as `# library:` and logs always go to
  **stderr** - scripts can safely consume stdout line by line.
- Query results render as tables by default (array fields get their own tables; falls back to JSON when a
  table makes no sense); `--json` forces JSON.
- A mutation result in human form = `ok` + a summary line + affected entity count + field-change count;
  `--json` gives `{ok, data, audit_ref, changes}` where `changes` is the **same payload projection** as the
  wire layer / audit / events (`ChangeSetPayload`, diff capped at 2000 entries plus a truncation marker).

## Relation to external agents

The `CliRunner` executor is a **public in-process API**: the external-agent gateway (`LinkPocket.Mcp`) reuses
it in-process and translates every tool call into one CLI execution. So "commands used by external agents"
and "commands typed by a human" are the same path - there is no second implementation. See
[`../LinkPocket.Mcp/TOOL.md`](../LinkPocket.Mcp/TOOL.md) and the workspace guide `EXTERNAL-AGENT.md`.

## Composition root

Reuses the shared `LinkPocket.Composition.EngineHost` (library path resolution + full `EngineComposer`
assembly + client facade + catalog + session manager). Its only difference from the WPF host and the
headless host is where output goes; the assembly path is identical.

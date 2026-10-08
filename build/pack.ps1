<#
.SYNOPSIS
  Build a delivery folder that an external agent (or a human) can use with NO source code.

.DESCRIPTION
  Why this exists: the command-line client and the MCP gateway are self-describing at runtime
  (`linkpocket help` / `describe` / `docs`; MCP `tools/list` + `resources`), but a folder that
  only contains exe/dll files tells a fresh agent nothing about WHICH entry point to run or how
  to start. So this script publishes the binaries and copies the entry docs, the workspace
  guide and the generated command catalog next to them, then writes a pointer page (AGENTS.md)
  and an MCP config example (mcp.example.json).

  Published:
    linkpocket.exe        command-line client (all 81 commands)
    linkpocket-mcp.exe    MCP gateway over stdio (external agents)
    AGENTS.md             pointer page: which exe to run, quick start, library path rule
    TOOL-cli.md           command-line reference
    TOOL-mcp.md           gateway reference
    EXTERNAL-AGENT.md     full external-agent guide (workspace doc, copied if found)
    COMMANDS.md           generated command catalog (copied if found)
    tools.functions.json  machine-readable capability manifest (copied if found)
    mcp.example.json      ready-to-edit MCP server config

.NOTES
  This script is intentionally ASCII-only (PowerShell 5.1 parses non-BOM files as ANSI; see
  WARNINGS #4). Nothing here touches the user database.

.PARAMETER SelfContained
  Bundle the .NET runtime (much bigger; runs on machines with no .NET 8 installed).

.PARAMETER IncludeApp
  Also publish the WPF desktop app (LinkPocket.exe).

.PARAMETER Output
  Output folder. Default: build/artifacts/dist

.PARAMETER Configuration
  Build configuration. Default: Release

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\pack.ps1
  powershell -ExecutionPolicy Bypass -File build\pack.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$IncludeApp,
    [string]$Output,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$out = if ($Output) { $Output } else { Join-Path $PSScriptRoot "artifacts\dist" }
$workRoot = Split-Path -Parent (Split-Path -Parent $repoRoot)   # workspace root (holds the private asset folder)

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { Write-Host "[PACK] dotnet SDK not found" -ForegroundColor Red; exit 1 }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$publishArgs = @("-c", $Configuration, "--nologo", "-o", $out)
if ($SelfContained) { $publishArgs += @("-r", "win-x64", "--self-contained", "true") }
else { $publishArgs += @("--self-contained", "false") }

foreach ($proj in @("src\LinkPocket.Cli", "src\LinkPocket.Mcp")) {
    Write-Host "[PACK] publishing $proj ..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $repoRoot $proj) @publishArgs
    if ($LASTEXITCODE -ne 0) { Write-Host "[PACK] publish failed: $proj" -ForegroundColor Red; exit 1 }
}

if ($IncludeApp) {
    Write-Host "[PACK] publishing src\LinkPocket.App (WPF) ..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $repoRoot "src\LinkPocket.App") @publishArgs
    if ($LASTEXITCODE -ne 0) { Write-Host "[PACK] publish failed: src\LinkPocket.App" -ForegroundColor Red; exit 1 }
}

# ---- entry docs (in-repo, always present) ----
Copy-Item (Join-Path $repoRoot "src\LinkPocket.Cli\TOOL.md") (Join-Path $out "TOOL-cli.md") -Force
Copy-Item (Join-Path $repoRoot "src\LinkPocket.Mcp\TOOL.md") (Join-Path $out "TOOL-mcp.md") -Force

# ---- workspace assets (live outside the repo): located with ASCII wildcards so this script stays ASCII-only
#      (PowerShell 5.1 parses non-BOM files as ANSI, so a literal non-ASCII path here would silently break - WARNINGS #4) ----
$workspaceAssets = @(
    @{ Pattern = "*\EXTERNAL-AGENT.md";            Dst = "EXTERNAL-AGENT.md" },
    @{ Pattern = "*\catalog\COMMANDS.md";          Dst = "COMMANDS.md" },
    @{ Pattern = "*\catalog\tools.functions.json"; Dst = "tools.functions.json" },
    @{ Pattern = "*\catalog\openapi-lite.json";    Dst = "openapi-lite.json" }
)

$missing = @()
$workspace = $null
foreach ($dir in (Get-ChildItem -LiteralPath $workRoot -Directory -ErrorAction SilentlyContinue)) {
    if (Get-Item -Path (Join-Path $dir.FullName "*\catalog\COMMANDS.md") -ErrorAction SilentlyContinue) {
        $workspace = $dir.FullName
        break
    }
}

foreach ($asset in $workspaceAssets) {
    $found = if ($workspace) {
        Get-Item -Path (Join-Path $workspace $asset.Pattern) -ErrorAction SilentlyContinue | Select-Object -First 1
    } else { $null }
    if ($found) { Copy-Item $found.FullName (Join-Path $out $asset.Dst) -Force }
    else { $missing += $asset.Pattern }
}

# ---- pointer page (ASCII only) ----
$agents = @'
# LinkPocket - delivery folder (no source code required)

You do not need the repository to drive this library. Everything below runs from THIS folder.

## Entry points

| File                | What it is                                                        |
|---------------------|-------------------------------------------------------------------|
| linkpocket.exe      | Command-line client. All 81 engine commands, no UI.               |
| linkpocket-mcp.exe  | MCP gateway over stdio, for external agents (tools/list, tools/call, resources/read). |
| LinkPocket.exe      | WPF desktop app (only present when packed with -IncludeApp).       |

## Discover the capabilities first (do not guess)

    linkpocket.exe help                 # usage
    linkpocket.exe describe             # every command + its parameters
    linkpocket.exe docs                 # full markdown reference
    linkpocket.exe help links.query     # one command in detail

Machine-readable output: add --json (the "# library:" prelude goes to stderr, so stdout stays
parseable). Exit codes: 0 ok / 1 error / 2 usage / 3 destructive command missing --yes.

## Library file (which database is used)

    --db <path>              explicit
    LINKPOCKET_DB            environment variable
    otherwise: the first linkpocket.db found walking up from the current directory,
    otherwise: linkpocket.db next to the executable.

## Typical commands

    linkpocket.exe links stats
    linkpocket.exe links list --list_id <folder-id> --per_page 20 --json
    linkpocket.exe search links --query github --json
    linkpocket.exe folders create --name "Notes"
    linkpocket.exe call batch.run --args "{\"script\":{\"name\":\"x\",\"steps\":[{\"command\":\"...\"}]}}"
    linkpocket.exe undo list            # what is undoable
    linkpocket.exe undo undo            # undo the most recent batch

Add --dry-run to any mutation to execute it with zero side effects.

## Connecting an agent over MCP

Edit mcp.example.json (next to this file), point "command" at the absolute path of
linkpocket-mcp.exe, and register it as an MCP server in your agent host. The gateway then
exposes every command as an MCP tool plus read-only resources such as:
    linkpocket://docs   linkpocket://commands   linkpocket://stats   linkpocket://tree
    linkpocket://snapshot   linkpocket://link/{id}   linkpocket://folder/{id}

## Read this before writing data

TOOL-cli.md / TOOL-mcp.md / EXTERNAL-AGENT.md (when present) document the concurrency contract:
external writes must be transactional batches; the URL column is data and must never be
normalized or rewritten; destructive commands need --yes; each process has its own undo stack.
'@
Set-Content -LiteralPath (Join-Path $out "AGENTS.md") -Value $agents -Encoding utf8

# ---- MCP config example ----
$mcpExe = (Join-Path $out "linkpocket-mcp.exe").Replace("\", "\\")
$mcp = @"
{
  "mcpServers": {
    "linkpocket": {
      "command": "$mcpExe",
      "args": ["--db", "<absolute path to linkpocket.db>"]
    }
  }
}
"@
Set-Content -LiteralPath (Join-Path $out "mcp.example.json") -Value $mcp -Encoding utf8

$files = Get-ChildItem -LiteralPath $out -File
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "[PACK] output: $out"
Write-Host ("[PACK] {0} files, {1:N1} MB" -f $files.Count, ($size / 1MB))
if (-not $SelfContained) {
    Write-Host "[PACK] framework-dependent build: the target machine needs the .NET 8 runtime." -ForegroundColor Yellow
    Write-Host "[PACK] re-run with -SelfContained to bundle it." -ForegroundColor Yellow
}
if ($missing.Count -gt 0) {
    Write-Host "[PACK] workspace assets not found (skipped):" -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host "         $m" -ForegroundColor Yellow }
}
Write-Host "[PACK] done."

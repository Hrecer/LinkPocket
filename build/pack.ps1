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
    LinkPocket.exe        WPF desktop app (only with -IncludeApp). Single file: the whole
                          framework is inside it, so the folder shows ONE exe, not ~540 dlls.
    tools\linkpocket.exe      command-line client (all 81 commands)
    tools\linkpocket-mcp.exe  MCP gateway over stdio (external agents)
    AGENTS.md             pointer page: which exe to run, quick start, library path rule
    TOOL-cli.md           command-line reference
    TOOL-mcp.md           gateway reference
    EXTERNAL-AGENT.md     full external-agent guide (workspace doc, copied if found)
    COMMANDS.md           generated command catalog (copied if found)
    tools.functions.json  machine-readable capability manifest (copied if found)
    mcp.example.json      ready-to-edit MCP server config

  Layout rule: the CLI and MCP exes live in `tools\` and never in the root. Windows paths are
  case-insensitive, so `LinkPocket.exe` (app) and `linkpocket.exe` (cli) silently overwrite each
  other when published side by side.

  The delivery contains no debug symbols (DebugType=none) and only EN + zh-Hans satellite
  resources; everything else in it is meant to be there.

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

.PARAMETER Zip
  Also write a .zip of the whole output folder to this exact path. Use it when the delivery is
  meant to be one file that lands in one place (the folder itself stays behind unless you delete
  it). The archive holds the folder contents flat, with no extra wrapper directory.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\pack.ps1
  powershell -ExecutionPolicy Bypass -File build\pack.ps1 -IncludeApp -Zip ..\LinkPocket_v3.2.3.zip
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$IncludeApp,
    [string]$Output,
    [string]$Zip,
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

# ---- publish ----
# Everything is published as ONE file per exe. A plain publish drops ~540 loose dlls next to the
# app, so the exe is impossible to find in the folder; single-file puts them inside the exe.
#   app    : self-contained (no .NET needed on the target machine), single file -> root
#   cli/mcp: single file -> tools\  (see "Layout rule" in the header for why not in the root)
# DebugType=none: no .pdb files. SatelliteResourceLanguages: keep EN + zh-Hans only, drop the
# other 11 UI languages the framework would otherwise copy in (MSBuild needs %3B for ';').
$rid = "win-x64"
$satLang = "en%3Bzh-Hans"

$toolsDir = Join-Path $out "tools"
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

# NOTE: cli/mcp are NOT published single-file on purpose. LinkPocket.Mcp references LinkPocket.Cli,
# and passing -r to a framework-dependent publish of a referenced exe fails with NETSDK1151
# ("a self-contained executable cannot be referenced by a non self-contained executable").
# They live in tools\, so their dlls never compete with the app exe for attention anyway.
$cliArgs = @("-c", $Configuration, "--nologo", "-o", $toolsDir)
if ($SelfContained) { $cliArgs += @("-r", $rid, "--self-contained", "true") }
else { $cliArgs += @("--self-contained", "false") }

foreach ($proj in @("src\LinkPocket.Cli", "src\LinkPocket.Mcp")) {
    Write-Host "[PACK] publishing $proj ..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $repoRoot $proj) @cliArgs
    if ($LASTEXITCODE -ne 0) { Write-Host "[PACK] publish failed: $proj" -ForegroundColor Red; exit 1 }
}

if ($IncludeApp) {
    Write-Host "[PACK] publishing src\LinkPocket.App (WPF, self-contained single file) ..." -ForegroundColor Cyan
    $appArgs = @("-c", $Configuration, "--nologo", "-r", $rid, "--self-contained", "true",
                 "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                 "-p:DebugType=none", "-p:SatelliteResourceLanguages=$satLang")
    & $dotnet publish (Join-Path $repoRoot "src\LinkPocket.App") @appArgs -o $out
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

| File                       | What it is                                                        |
|----------------------------|-------------------------------------------------------------------|
| LinkPocket.exe             | WPF desktop app (only present when packed with -IncludeApp).       |
| tools\linkpocket.exe       | Command-line client. All 81 engine commands, no UI.               |
| tools\linkpocket-mcp.exe   | MCP gateway over stdio, for external agents (tools/list, tools/call, resources/read). |

Each exe is self-contained as a single file: there are no loose dlls to hunt for, and the
command-line and gateway exes sit in `tools\` so they cannot collide with the desktop app
(Windows paths are case-insensitive: `LinkPocket.exe` and `linkpocket.exe` are the same name).

## Discover the capabilities first (do not guess)

    tools\linkpocket.exe help                 # usage
    tools\linkpocket.exe describe             # every command + its parameters
    tools\linkpocket.exe docs                 # full markdown reference
    tools\linkpocket.exe help links.query     # one command in detail

Machine-readable output: add --json (the "# library:" prelude goes to stderr, so stdout stays
parseable). Exit codes: 0 ok / 1 error / 2 usage / 3 destructive command missing --yes.

## Library file (which database is used)

    --db <path>              explicit
    LINKPOCKET_DB            environment variable
    otherwise: the first linkpocket.db found walking up from the current directory,
    otherwise: linkpocket.db next to the executable.

## Typical commands

    tools\linkpocket.exe links stats
    tools\linkpocket.exe links list --list_id <folder-id> --per_page 20 --json
    tools\linkpocket.exe search links --query github --json
    tools\linkpocket.exe folders create --name "Notes"
    tools\linkpocket.exe call batch.run --args "{\"script\":{\"name\":\"x\",\"steps\":[{\"command\":\"...\"}]}}"
    tools\linkpocket.exe undo list            # what is undoable
    tools\linkpocket.exe undo undo            # undo the most recent batch

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
$mcpExe = (Join-Path $out "tools\linkpocket-mcp.exe").Replace("\", "\\")
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

# ---- no debug symbols in a delivery (belt and braces: DebugType=none should already do it) ----
Get-ChildItem -LiteralPath $out -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$files = Get-ChildItem -LiteralPath $out -Recurse -File
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "[PACK] output: $out"
Write-Host ("[PACK] {0} files, {1:N1} MB" -f $files.Count, ($size / 1MB))
foreach ($f in ($files | Sort-Object Length -Descending | Select-Object -First 5)) {
    Write-Host ("[PACK]   {0,-28} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
}
if (-not $SelfContained) {
    Write-Host "[PACK] framework-dependent build: the target machine needs the .NET 8 runtime." -ForegroundColor Yellow
    Write-Host "[PACK] re-run with -SelfContained to bundle it." -ForegroundColor Yellow
}
if ($missing.Count -gt 0) {
    Write-Host "[PACK] workspace assets not found (skipped):" -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host "         $m" -ForegroundColor Yellow }
}

# ---- optional: one archive, written exactly where the caller asked for it ----
if ($Zip) {
    Write-Host "[PACK] zipping -> $Zip ..." -ForegroundColor Cyan
    $zipParent = Split-Path -Parent $Zip
    if ($zipParent -and -not (Test-Path -LiteralPath $zipParent)) {
        New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $out, $Zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ("[PACK] zip: {0} ({1:N1} MB)" -f $Zip, ((Get-Item -LiteralPath $Zip).Length / 1MB)) -ForegroundColor Green
}

Write-Host "[PACK] done."

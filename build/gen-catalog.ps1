<#
.SYNOPSIS
  重新生成引擎目录文档（docs/catalog/*）。

.DESCRIPTION
  文档内容全部来自命令描述符（Descriptor = 单一事实源），本脚本只负责调用导出工具。
  **不要手改 docs/catalog 下的任何文件** —— 改了会在 CI 的目录漂移检查里被判红。

  产物：
    docs/catalog/COMMANDS.md           人读命令目录（Markdown 表格）
    docs/catalog/tools.functions.json  AI FunctionCalling 工具清单（OpenAI tools 兼容形态）
    docs/catalog/openapi-lite.json     轻量 OpenAPI（每命令一个 path）

.PARAMETER Configuration
  构建配置，缺省 Release（与 CI 口径一致）。

.PARAMETER Check
  只校验"现有文件与当前描述符是否一致"，不写盘；不一致时非零退出（CI 用法）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\gen-catalog.ps1
  powershell -ExecutionPolicy Bypass -File build\gen-catalog.ps1 -Check
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    foreach ($candidate in @("$env:ProgramFiles\dotnet\dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe")) {
        if (Test-Path $candidate) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) { Write-Host "[catalog] 找不到 dotnet SDK" -ForegroundColor Red; exit 1 }

$env:DOTNET_CLI_UI_LANGUAGE = "en"
$toolArgs = @("run", "--project", (Join-Path $repoRoot "tools/LinkPocket.CatalogExport"), "-c", $Configuration)
$toolArgs += "--"
if ($Check) { $toolArgs += "--check" }

Write-Host "[catalog] $(if ($Check) { '校验' } else { '生成' })目录文档 ..." -ForegroundColor Cyan
& $dotnet @toolArgs
exit $LASTEXITCODE

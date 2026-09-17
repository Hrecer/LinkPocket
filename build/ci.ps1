<#
.SYNOPSIS
  LinkPocket CI 门禁（阶段 12：把 10k 性能基准真正接进流水线）。

.DESCRIPTION
  单一入口，任何 CI 提供商（GitHub Actions / Jenkins / 本地预提交）都只需调用本脚本。
  五道门，任一道不过即非零退出：

    1. 清 obj + 全量编译（方案 9：0 警告 0 错误，-warnaserror 强制）
    2. 单元测试（Engine / Modules / Architecture）
    3. 协议冒烟（含 §0~§11 端到端断言）
    4. 10k 性能门槛（Release 构建 + --strict-perf：严格按方案 7.3 原始门槛判定，不享受 Debug 放宽）
    5. 目录文档漂移检查（docs/catalog 必须与命令描述符逐字节一致）

  产物：build/artifacts/{build,test,smoke,catalog}.log + perf_report.json（逐条实测值，供归档与趋势对比）。

.PARAMETER Configuration
  构建配置，缺省 Release（性能门槛的标定口径；Debug 仅供本地快速迭代）。

.PARAMETER SkipClean
  跳过 obj 清理。**不建议**：门禁跑的是"清 obj 全量编译"，跳过会让结果不再等价于 CI 口径。

.PARAMETER NoWarnAsError
  关闭"警告即错误"。仅在排查历史遗留警告时临时使用；正常门禁不要开。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\ci.ps1
  powershell -ExecutionPolicy Bypass -File build\ci.ps1 -SkipClean
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipClean,
    [switch]$NoWarnAsError
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $PSScriptRoot "artifacts"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    foreach ($candidate in @("$env:ProgramFiles\dotnet\dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe")) {
        if (Test-Path $candidate) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) { Write-Host "[CI] 找不到 dotnet SDK" -ForegroundColor Red; exit 1 }

$env:DOTNET_CLI_UI_LANGUAGE = "en"     # 英文输出：日志编码稳定，便于归档与检索
if (-not $env:LP_PERF_REPORT) { $env:LP_PERF_REPORT = Join-Path $artifacts "perf_report.json" }

# —— 1. 清 obj（保留 bin：bin 是交付产物，清 bin 会破坏"产物即事实"的验证口径）——
if (-not $SkipClean) {
    Write-Host "[CI] 清理 obj ..." -ForegroundColor Cyan
    $empty = Join-Path $env:TEMP ("lpci_empty_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    $objectDirs = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -Filter obj -Force |
        Where-Object { $_.FullName -notmatch '\\obj\\' })
    foreach ($dir in $objectDirs) {
        if (-not (Test-Path -LiteralPath $dir.FullName)) { continue }
        robocopy $empty $dir.FullName /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
        try { [System.IO.Directory]::Delete($dir.FullName, $false) } catch { }
    }
    [System.IO.Directory]::Delete($empty, $false)
    Write-Host "[CI] 已清理 $($objectDirs.Count) 个 obj 目录" -ForegroundColor Green
}

# —— 2. 全量编译（0 警告 0 错误）——
Write-Host "[CI] 全量编译（$Configuration，0 警告 0 错误）..." -ForegroundColor Cyan
$buildArgs = @("build", (Join-Path $repoRoot "LinkPocket.sln"), "-c", $Configuration, "--nologo", "-v", "m")
if (-not $NoWarnAsError) { $buildArgs += "-warnaserror" }
& $dotnet @buildArgs 2>&1 | Tee-Object -FilePath (Join-Path $artifacts "build.log")
if ($LASTEXITCODE -ne 0) { Write-Host "[CI] 编译失败（exit=$LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host "[CI] 编译通过" -ForegroundColor Green

# —— 3. 单元测试 ——
Write-Host "[CI] 单元测试 ..." -ForegroundColor Cyan
& $dotnet test (Join-Path $repoRoot "LinkPocket.sln") -c $Configuration --no-build --nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $artifacts "test.log")
if ($LASTEXITCODE -ne 0) { Write-Host "[CI] 单元测试失败（exit=$LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host "[CI] 单元测试通过" -ForegroundColor Green

# —— 4. 协议冒烟 + 严格性能门槛 ——
Write-Host "[CI] 协议冒烟 + 10k 性能门槛（严格）..." -ForegroundColor Cyan
& $dotnet run --project (Join-Path $repoRoot "tests/ProtocolSmoke") -c $Configuration --no-build -- --strict-perf 2>&1 |
    Tee-Object -FilePath (Join-Path $artifacts "smoke.log")
if ($LASTEXITCODE -ne 0) { Write-Host "[CI] 协议冒烟/性能门槛未过（exit=$LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host "[CI] 协议冒烟与性能门槛通过" -ForegroundColor Green

# —— 5. 目录文档漂移检查（Descriptor = 单一事实源，文档必须机械生成）——
Write-Host "[CI] 目录文档漂移检查 ..." -ForegroundColor Cyan
& $dotnet run --project (Join-Path $repoRoot "tools/LinkPocket.CatalogExport") -c $Configuration --no-build -- --check 2>&1 |
    Tee-Object -FilePath (Join-Path $artifacts "catalog.log")
if ($LASTEXITCODE -ne 0) {
    Write-Host "[CI] docs/catalog 与命令描述符不一致（跑 build/gen-catalog.ps1 重新生成）" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "[CI] 目录文档与描述符一致" -ForegroundColor Green

Write-Host ""
Write-Host "[CI] 全部门禁通过。性能报告：$env:LP_PERF_REPORT" -ForegroundColor Green
exit 0

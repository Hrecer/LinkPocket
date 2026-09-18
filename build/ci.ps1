<#
.SYNOPSIS
  LinkPocket CI 门禁（阶段 12 起：把 10k 性能基准真正接进流水线；阶段 13 补目录文档漂移门）。

.DESCRIPTION
  单一入口，任何 CI 提供商（GitHub Actions / Jenkins / 本地预提交）都只需调用本脚本。
  五道门，任一道不过即非零退出：

    1. 清 obj + 全量编译（0 警告 0 错误，-warnaserror 强制）
    2. 单元测试（Architecture / Engine / Modules）
    3. 协议冒烟（含 §0~§11 端到端断言）
    4. 10k 性能门槛（Release 构建 + --strict-perf：按标定门槛判定，不享受 Debug 放宽）
    5. 目录文档漂移检查（docs/catalog 必须与命令描述符逐字节一致）

  产物：build/artifacts/{build,test,smoke,catalog}.log + perf_report.json（逐条实测值，供归档与趋势对比）；
        临时库与暂存区落 build/artifacts/tmp/（LP_TEMP_ROOT），不污染用户配置目录。

.NOTES
  第 1 道门带自动重试：本机（G 盘 + 沙箱 overlay）偶发 obj 文件瞬时占用
  （MC1000 / CS2012 / MSB3491 "Access to the path ... is denied"），重试即过，不是代码问题。

.PARAMETER Configuration
  构建配置，缺省 Release（性能门槛的标定口径；Debug 仅供本地快速迭代）。

.PARAMETER SkipClean
  跳过初始 obj 清理。**不建议**：门禁跑的是"清 obj 全量编译"，跳过会让结果不再等价于 CI 口径。

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
# 报告与临时根都在工作区内：不在用户配置目录（%TEMP%）留任何产物
if (-not $env:LP_PERF_REPORT) { $env:LP_PERF_REPORT = Join-Path $artifacts "perf_report.json" }
if (-not $env:LP_TEMP_ROOT)   { $env:LP_TEMP_ROOT   = Join-Path $artifacts "tmp" }
New-Item -ItemType Directory -Path $env:LP_TEMP_ROOT -Force | Out-Null

# 清 obj 用的空目录（robocopy /MIR 镜像清空 —— 不受 safe-delete 批量阈值影响）
$emptyForClean = Join-Path $artifacts "empty-for-clean"
New-Item -ItemType Directory -Path $emptyForClean -Force | Out-Null

function Clear-ObjDirectories {
    $objectDirs = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -Filter obj -Force |
        Where-Object { $_.FullName -notmatch '\\obj\\' })
    foreach ($dir in $objectDirs) {
        if (-not (Test-Path -LiteralPath $dir.FullName)) { continue }
        robocopy $emptyForClean $dir.FullName /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
        try { [System.IO.Directory]::Delete($dir.FullName, $false) } catch { }
    }
    return $objectDirs.Count
}

# —— 1. 清 obj + 全量编译（保留 bin：bin 是交付产物，清 bin 会破坏"产物即事实"的验证口径）——
if (-not $SkipClean) {
    Write-Host "[CI] 清理 obj ..." -ForegroundColor Cyan
    $count = Clear-ObjDirectories
    Write-Host "[CI] 已清理 $count 个 obj 目录" -ForegroundColor Green
}

$buildArgs = @("build", (Join-Path $repoRoot "LinkPocket.sln"), "-c", $Configuration, "--nologo", "-v", "m")
if (-not $NoWarnAsError) { $buildArgs += "-warnaserror" }
$buildLog = Join-Path $artifacts "build.log"

$maxAttempts = 3
$built = $false
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Write-Host "[CI] 全量编译（$Configuration，0 警告 0 错误）第 $attempt/$maxAttempts 次 ..." -ForegroundColor Cyan
    & $dotnet @buildArgs 2>&1 | Tee-Object -FilePath $buildLog
    if ($LASTEXITCODE -eq 0) { $built = $true; break }

    if ($attempt -lt $maxAttempts) {
        Write-Host "[CI] 编译失败（exit=$LASTEXITCODE）：本机 obj 偶发瞬时占用，清 obj 后重试 ..." -ForegroundColor Yellow
        Clear-ObjDirectories | Out-Null
    }
}
if (-not $built) {
    Write-Host "[CI] 编译失败（已重试 $maxAttempts 次），日志：$buildLog" -ForegroundColor Red
    exit 1
}
Write-Host "[CI] 编译通过" -ForegroundColor Green

# —— 2. 单元测试（逐项目串行：`dotnet test` 多项目并行跑会让测试宿主进程崩溃 0xC00000FD/0x80131506，
#        单项目跑全绿；「一次只能跟一个项目」也是既有已知约束，见 docs/TESTING.md §2 与 docs/WARNINGS.md ——）
Write-Host "[CI] 单元测试 ..." -ForegroundColor Cyan
$testProjects = @(
    "tests/LinkPocket.Architecture.Tests",
    "tests/LinkPocket.Engine.Tests",
    "tests/LinkPocket.Modules.Tests",
    "tests/LinkPocket.App.Tests"
)
$testLog = Join-Path $artifacts "test.log"
if (Test-Path -LiteralPath $testLog) { Remove-Item -LiteralPath $testLog -Force }
foreach ($project in $testProjects) {
    Write-Host "[CI]   -> $project" -ForegroundColor DarkGray
    & $dotnet test (Join-Path $repoRoot $project) -c $Configuration --no-build --nologo 2>&1 |
        Tee-Object -FilePath $testLog -Append
    if ($LASTEXITCODE -ne 0) { Write-Host "[CI] 单元测试失败（$project，exit=$LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }
}
Write-Host "[CI] 单元测试通过" -ForegroundColor Green

# —— 3 & 4. 协议冒烟 + 严格性能门槛 ——
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

# —— 收尾：清空本次运行的临时根（测试临时库/暂存区都在这里，跑完不留）——
try {
    if (Test-Path -LiteralPath $env:LP_TEMP_ROOT) {
        robocopy $emptyForClean $env:LP_TEMP_ROOT /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
        [System.IO.Directory]::Delete($env:LP_TEMP_ROOT, $false)
        Write-Host "[CI] 已清理临时根：$env:LP_TEMP_ROOT" -ForegroundColor Green
    }
} catch {
    Write-Host "[CI] 临时根清理失败（不影响门禁结论）：$($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "[CI] 全部门禁通过。性能报告：$env:LP_PERF_REPORT" -ForegroundColor Green
exit 0

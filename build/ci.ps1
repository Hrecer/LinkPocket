<#
.SYNOPSIS
  LinkPocket CI 门禁（10k 性能基准接进流水线）。

.DESCRIPTION
  单一入口，任何 CI 提供商（GitHub Actions / Jenkins / 本地预提交）都只需调用本脚本。
  四道门，任一道不过即非零退出：

    1. 清 obj + 全量编译（0 警告 0 错误，-warnaserror 强制）
       · 清 obj 时保留 NuGet restore 资产（project.assets.json / *.nuget.g.props / *.nuget.g.targets
         / *.nuget.dgspec.json / project.nuget.cache）：编译产物在 obj/<config>/ 下照样被清，
         仍然强制全量重编，但省掉每次都重新 restore 全部项目（实测 5~9s）。
    2. 单元测试（Architecture / Engine / Modules / App，逐项目串行）
    3. 协议冒烟（含 §0~§11 端到端断言）
    4. 10k 性能门槛（Release 构建 + --strict-perf：按标定门槛判定，不享受 Debug 放宽）

  收尾：关闭本次门禁起的常驻编译服务器（MSBuild 节点 / VBCSCompiler），并打印分阶段耗时表。

  产物：build/artifacts/{build,test,smoke}.log + perf_report.json（逐条实测值，供归档与趋势对比）
        + timing_history.log（每次运行一行分阶段耗时，供跨运行对比）；
        临时库与暂存区落 build/artifacts/tmp/（LP_TEMP_ROOT），不污染用户配置目录。

.NOTES
  第 1 道门带自动重试：本机（G 盘 + 沙箱 overlay）偶发 obj 文件瞬时占用
  （MC1000 / CS2012 / MSB3491 "Access to the path ... is denied"）。重试分两档：
  **第 2 次原样重试**（瞬时占用多为几秒自解，不动 obj 可省掉一整轮清 obj + 全量重编，实测每次约 13s），
  **第 3 次才清 obj 重试**（覆盖"工具视图 ≠ 编译视图"那类必须重建 obj 的场景）。

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

# —— 分阶段计时（门禁自己报告每道门花了多少秒；见文件末尾 Write-TimingSummary）——
$ciTotal = [System.Diagnostics.Stopwatch]::StartNew()
$timings = [ordered]@{}
$testTimings = [ordered]@{}
$buildAttempts = @()

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

# 清 obj 时保留的 NuGet restore 资产：清 obj 的目的是"强制全量重编"，编译产物都在 obj/<config>/ 下，
# 照样被清；但 restore 状态不该陪着一起没 —— 否则每次门禁都要重新 restore 全部项目（实测 5~9s）。
# 保留这五个文件让后续 restore 成为 no-op，且不含任何编译产物，门的判据（0 警告 0 错误 + 全量重编）不变。
$objKeepFiles = @('project.assets.json', 'project.nuget.cache', '*.nuget.g.props', '*.nuget.g.targets', '*.nuget.dgspec.json')

function Clear-ObjDirectories {
    $objectDirs = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -Filter obj -Force |
        Where-Object { $_.FullName -notmatch '\\obj\\' })
    foreach ($dir in $objectDirs) {
        if (-not (Test-Path -LiteralPath $dir.FullName)) { continue }
        robocopy $emptyForClean $dir.FullName /MIR /XF $objKeepFiles /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
        try { [System.IO.Directory]::Delete($dir.FullName, $false) } catch { }
    }
    return $objectDirs.Count
}

# 打印分阶段耗时表，并把本行追加进 timing_history.log（跨运行对比用）。
# 任何一处 exit 之前都调用它 —— 门禁失败时同样要知道时间花在哪道门。
function Write-TimingSummary {
    $total = $ciTotal.Elapsed.TotalSeconds
    Write-Host ""
    Write-Host "[CI] ===== 分阶段耗时 =====" -ForegroundColor Cyan
    foreach ($key in $timings.Keys) {
        Write-Host ("[CI]   {0,-20} {1,8:N2}s" -f $key, [double]$timings[$key]) -ForegroundColor Cyan
        if ($key -eq '② 编译' -and $buildAttempts.Count -gt 1) {
            $detail = ($buildAttempts | ForEach-Object { '{0:N2}s' -f $_ }) -join ' + '
            Write-Host ("[CI]     （{0} 次尝试：{1}）" -f $buildAttempts.Count, $detail) -ForegroundColor Cyan
        }
        if ($key -eq '③ 单测') {
            foreach ($project in $testTimings.Keys) {
                Write-Host ("[CI]     {0,-28} {1,8:N2}s" -f $project, [double]$testTimings[$project]) -ForegroundColor DarkCyan
            }
        }
    }
    Write-Host ("[CI]   {0,-20} {1,8:N2}s" -f '合计', $total) -ForegroundColor Cyan
    try {
        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        $flat = ($timings.Keys | ForEach-Object { '{0}={1:N2}s' -f $_, [double]$timings[$_] }) -join '  '
        Add-Content -LiteralPath (Join-Path $artifacts "timing_history.log") -Encoding UTF8 `
            -Value ("{0}  TOTAL={1:N2}s  {2}" -f $stamp, $total, $flat)
    } catch {
        Write-Host "[CI] 耗时归档失败（不影响门禁结论）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# —— 1. 清 obj + 全量编译（保留 bin：bin 是交付产物，清 bin 会破坏"产物即事实"的验证口径）——
if (-not $SkipClean) {
    Write-Host "[CI] 清理 obj ..." -ForegroundColor Cyan
    $swClean = [System.Diagnostics.Stopwatch]::StartNew()
    $count = Clear-ObjDirectories
    $swClean.Stop()
    $timings['① 清理 obj'] = $swClean.Elapsed.TotalSeconds
    Write-Host "[CI] 已清理 $count 个 obj 目录（保留 NuGet restore 资产）" -ForegroundColor Green
}

$buildArgs = @("build", (Join-Path $repoRoot "LinkPocket.sln"), "-c", $Configuration, "--nologo", "-v", "m")
if (-not $NoWarnAsError) { $buildArgs += "-warnaserror" }
$buildLog = Join-Path $artifacts "build.log"

$maxAttempts = 3
$built = $false
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Write-Host "[CI] 全量编译（$Configuration，0 警告 0 错误）第 $attempt/$maxAttempts 次 ..." -ForegroundColor Cyan
    $swAttempt = [System.Diagnostics.Stopwatch]::StartNew()
    # 直接重定向到文件（不经管道）——理由同下方单测段：native 命令走管道 + Tee-Object 会死锁。
    & $dotnet @buildArgs *> $buildLog
    $buildExit = $LASTEXITCODE
    $swAttempt.Stop()
    $buildAttempts += $swAttempt.Elapsed.TotalSeconds
    if ($buildExit -eq 0) { $built = $true; break }
    Get-Content -LiteralPath $buildLog -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }

    if ($attempt -lt $maxAttempts) {
        if ($attempt -eq $maxAttempts - 1) {
            Write-Host "[CI] 编译失败（exit=$buildExit）：第 $($attempt + 1) 次尝试前清 obj 后重试 ..." -ForegroundColor Yellow
            Clear-ObjDirectories | Out-Null
        } else {
            Write-Host "[CI] 编译失败（exit=$buildExit）：obj 瞬时占用多为几秒自解，原样重试 ..." -ForegroundColor Yellow
        }
    }
}
$timings['② 编译'] = ($buildAttempts | Measure-Object -Sum).Sum
if (-not $built) {
    Write-Host "[CI] 编译失败（已重试 $maxAttempts 次），日志：$buildLog" -ForegroundColor Red
    Write-TimingSummary
    exit 1
}
Write-Host "[CI] 编译通过" -ForegroundColor Green

# —— 2. 单元测试（逐项目串行：`dotnet test` 多项目并行跑会让测试宿主进程崩溃 0xC00000FD/0x80131506，
#        单项目跑全绿；「一次只能跟一个项目」也是既有已知约束，）
Write-Host "[CI] 单元测试 ..." -ForegroundColor Cyan
$testProjects = @(
    "tests/LinkPocket.Architecture.Tests",
    "tests/LinkPocket.Engine.Tests",
    "tests/LinkPocket.Diagnostics.Tests",
    "tests/LinkPocket.Ai.Tests",
    "tests/LinkPocket.Modules.Tests",
    "tests/LinkPocket.App.Tests"
)
$testLog = Join-Path $artifacts "test.log"
if (Test-Path -LiteralPath $testLog) { Remove-Item -LiteralPath $testLog -Force }
$swTests = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($project in $testProjects) {
    Write-Host "[CI]   -> $project" -ForegroundColor DarkGray
    $swTest = [System.Diagnostics.Stopwatch]::StartNew()
    # ⚠️ 绝对不要写成 `& $dotnet test ... 2>&1 | Tee-Object -FilePath ...`：
    #    原生命令的输出走**管道**时会先填满管道缓冲区；PowerShell 对原生命令的管道不是流式转发，
    #    于是"子进程写满缓冲等读、父进程等子进程结束"→ **死锁式永久挂起**（实测：CI 卡几十分钟不返回，
    #    而同一命令手敲 `dotnet test` 却正常结束）。改为**直接重定向到文件**：不经过管道，不可能死锁，
    #    且天然保留全部输出（含 stderr）供失败时回看。
    $projectLog = Join-Path $artifacts ("test-" + (Split-Path -Leaf $project) + ".log")
    & $dotnet test (Join-Path $repoRoot $project) -c $Configuration --no-build --nologo *> $projectLog
    $testExit = $LASTEXITCODE
    # 汇总进总日志（失败时才有用；文件可能为空，故容错）
    if (Test-Path -LiteralPath $projectLog) {
        $projectText = Get-Content -LiteralPath $projectLog -Raw
        if ($projectText) { Add-Content -LiteralPath $testLog -Value $projectText }
    }
    # 只回显尾部（全量已在文件里），避免刷屏又不丢信息
    Get-Content -LiteralPath $projectLog -Tail 4 | ForEach-Object { Write-Host "      $_" }
    $swTest.Stop()
    $testTimings[(Split-Path -Leaf $project)] = $swTest.Elapsed.TotalSeconds
    if ($testExit -ne 0) {
        Write-Host "[CI] 单元测试失败（$project，exit=$testExit）" -ForegroundColor Red
        $swTests.Stop()
        $timings['③ 单测'] = $swTests.Elapsed.TotalSeconds
        Write-TimingSummary
        exit $LASTEXITCODE
    }
}
$swTests.Stop()
$timings['③ 单测'] = $swTests.Elapsed.TotalSeconds
Write-Host "[CI] 单元测试通过" -ForegroundColor Green

# —— 3 & 4. 协议冒烟 + 严格性能门槛 ——
Write-Host "[CI] 协议冒烟 + 10k 性能门槛（严格）..." -ForegroundColor Cyan
$swSmoke = [System.Diagnostics.Stopwatch]::StartNew()
# 直接重定向到文件（不经管道）——理由同单测段：native 命令走管道 + Tee-Object 会死锁。
$smokeLog = Join-Path $artifacts "smoke.log"
& $dotnet run --project (Join-Path $repoRoot "tests/ProtocolSmoke") -c $Configuration --no-build -- --strict-perf *> $smokeLog
$smokeExit = $LASTEXITCODE
$swSmoke.Stop()
$timings['④ 冒烟 + 10k 性能'] = $swSmoke.Elapsed.TotalSeconds
Get-Content -LiteralPath $smokeLog -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
if ($smokeExit -ne 0) {
    Write-Host "[CI] 协议冒烟/性能门槛未过（exit=$smokeExit）" -ForegroundColor Red
    Write-TimingSummary
    exit $smokeExit
}
Write-Host "[CI] 协议冒烟与性能门槛通过" -ForegroundColor Green

# —— 收尾：关掉本次门禁起的常驻编译服务器 ——
# `dotnet build` 默认 nodeReuse:true，跑完会常驻约 15 分钟（24 核机器实测 23 个进程 / ~3.3GB），
# 多代叠加会逼近本机提交上限（曾致测试宿主 0xC00000FD，第 26 条）。
# 门禁是"跑完即净"的场景：收尾统一关掉（代价：下次编译冷启动约 +7s）。
try {
    $null = & $dotnet build-server shutdown 2>&1
    Write-Host "[CI] 已关闭常驻编译服务器（MSBuild 节点 / VBCSCompiler）" -ForegroundColor Green
} catch {
    Write-Host "[CI] 关闭编译服务器失败（不影响门禁结论）：$($_.Exception.Message)" -ForegroundColor Yellow
}

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

Write-TimingSummary

Write-Host ""
Write-Host "[CI] 全部门禁通过。性能报告：$env:LP_PERF_REPORT" -ForegroundColor Green
exit 0

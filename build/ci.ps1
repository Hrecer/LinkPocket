<#
.SYNOPSIS
  LinkPocket CI 门禁（10k 性能基准接进流水线）。

.DESCRIPTION
  单一入口，任何 CI 提供商（GitHub Actions / Jenkins / 本地预提交）都只需调用本脚本。
  五道门，任一道不过即非零退出：

    1. 全量编译（0 警告 0 错误，-warnaserror + --no-incremental 强制全量重编，**不清 obj**）
       · 实测（本机 2026-10-09）：清 obj 本身~50s（robocopy /MIR 清 893 个文件），
         而"清 obj 后重编"与"直接 --no-incremental"编译时长相同（23~32s）→ 那 50s 是纯开销。
    1.5 生成物漂移校验（tools/LinkPocket.ClientGen --check：EngineClient 便利层必须与命令描述符一致）
    2. 单元测试（Architecture / Engine / Modules / App；默认逐项目串行，-ParallelTests 可两两并行）
    3. 协议冒烟（含 §0~§13 端到端断言）
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
  **已默认不做任何事**（保留仅为兼容旧调用）。历史口径是"先清 obj 再全量编译"，现在全量由
  `dotnet build --no-incremental` 保证（实测清 obj 本身要 ~50s，而编译两者同为 23~32s——纯开销），
  故默认跳过；仅在编译失败且怀疑"工具视图 ≠ 编译视图"时，第 3 次重试会清 obj。

.PARAMETER ParallelTests
  单元测试改为**两两并行 + 批间串行**（默认关 = 逐项目串行，唯一在受限宿主里也稳的口径）。
  并行依赖"允许并发子进程"的执行环境：受限宿主会拦 Start-Job / Process.Start，
  症状是"每个项目 1~2s 秒判失败、日志没更新"——那是环境拒绝，不是测试红。

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
    [switch]$ParallelTests,
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

# —— 1. 记录是否需要清 obj（默认不清）——
# 历史口径是"清 obj + 全量编译"；现在全量由 `--no-incremental` 保证（见 $buildArgs 处的实测数据），
# 清 obj 只在**编译自身失败且怀疑工具视图 ≠ 编译视图**时才需要（第 3 次重试仍在做）。
# 保留 -SkipClean 参数以兼容既有调用（显式传它 = 恢复"先清 obj 再编译"的旧口径）。
if (-not $SkipClean) {
    Write-Host "[CI] obj 清理已跳过（全量重编由 --no-incremental 保证）" -ForegroundColor Cyan
}

$buildArgs = @("build", (Join-Path $repoRoot "LinkPocket.sln"), "-c", $Configuration, "--nologo", "-v", "m")
if (-not $NoWarnAsError) { $buildArgs += "-warnaserror" }
# 强制全量重编，但**不清 obj**：`--no-incremental` 与"清 obj 后重编"语义等价（都由 MSBuild 重写全部产物），
# 而实测（本机 2026-10-09）清 obj 本身要 ~50s（robocopy /MIR 清 893 个文件），编译两者都是 23~32s
# —— 那 50s 是纯开销。门禁的判据（0 警告 0 错误 + 全量重编）一条不少。
$buildArgs += "--no-incremental"
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

# —— 1.5. 生成物漂移校验：EngineClient 便利层由命令描述符机械生成（tools/LinkPocket.ClientGen）。
#         生成文件与描述符不一致 = "改了描述符忘了重新生成" → 门禁不过，从结构上杜绝手写层漂移。
Write-Host "[CI] 生成物漂移校验（EngineClient 便利层）..." -ForegroundColor Cyan
$generatedLog = Join-Path $artifacts "generated.log"
$swGenerated = [System.Diagnostics.Stopwatch]::StartNew()
& $dotnet run --project (Join-Path $repoRoot "tools/LinkPocket.ClientGen") -c $Configuration -- --check *> $generatedLog
$generatedExit = $LASTEXITCODE
$swGenerated.Stop()
$timings['②.5 生成物校验'] = $swGenerated.Elapsed.TotalSeconds
if ($generatedExit -ne 0) {
    Get-Content -LiteralPath $generatedLog -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
    Write-Host "[CI] EngineClient.Generated.cs 与命令描述符不一致（exit=$generatedExit），日志：$generatedLog" -ForegroundColor Red
    Write-TimingSummary
    exit 1
}
Write-Host "[CI] 生成物与描述符一致" -ForegroundColor Green

# —— 2. 单元测试（**分批并行**：批内两个项目同时跑，批与批之间串行）
# 分批理由：① 串行 6 个项目共 ~170s（实测，真在跑用例，不是启动开销）；
#   ② 并行度受**内存**约束——`LinkPocket.App` 是 net8.0-windows（WPF）、`Modules` 拉起 EF、
#   `Ai` 拉起 AI 运行时，三者同时跑正是 `WARNINGS` 记录的"测试宿主 0xC00000FD"成因；
#   ③ 故取"两两并行 + 批间串行"：最重的一批是 Modules+App（约 40s / 58s，取大者）。
# ④ 受限宿主（沙箱/受限 runner）会拦并发子进程 → 并行开关默认关闭；确认环境允许再用 -ParallelTests。
Write-Host "[CI] 单元测试 ..." -ForegroundColor Cyan
$testBatches = @(
    @("tests/LinkPocket.Architecture.Tests", "tests/LinkPocket.Diagnostics.Tests"),  # 两个都极轻
    @("tests/LinkPocket.Engine.Tests",       "tests/LinkPocket.Ai.Tests"),
    @("tests/LinkPocket.Modules.Tests",      "tests/LinkPocket.App.Tests")
)
$testLog = Join-Path $artifacts "test.log"
if (Test-Path -LiteralPath $testLog) { Remove-Item -LiteralPath $testLog -Force }
$swTests = [System.Diagnostics.Stopwatch]::StartNew()
$testFailures = @()

if ($ParallelTests) {
    # 可选的"两两并行"。⚠️ **默认关闭**，因为它依赖"允许并发子进程"的执行环境：
    # 受限宿主（沙箱 / 受限 CI runner）会拦掉 Start-Job 或 Process.Start，表现为
    # "每个项目 1~2s 秒判失败、日志没更新"——那种失败是**环境拒绝**，不是测试红了。
    # 确认环境允许并发后再开：-ParallelTests
    foreach ($batch in $testBatches) {
        Write-Host ("[CI]   -> " + ($batch -join "  +  ") + "（并行）") -ForegroundColor DarkGray
        $swBatch = [System.Diagnostics.Stopwatch]::StartNew()
        $jobs = @()
        foreach ($project in $batch) {
            $projectLog = Join-Path $artifacts ("test-" + (Split-Path -Leaf $project) + ".log")
            $jobs += Start-Job -Name ([IO.Path]::GetFileNameWithoutExtension($project)) -ScriptBlock {
                param($dotnet, $repo, $project, $cfg, $log)
                $env:DOTNET_CLI_UI_LANGUAGE = "en"
                & $dotnet test (Join-Path $repo $project) -c $cfg --no-build --nologo *> $log
                $LASTEXITCODE
            } -ArgumentList $dotnet, $repoRoot, $project, $Configuration, $projectLog
        }

        $swBatch.Stop()
        foreach ($project in $batch) {
            $name = [IO.Path]::GetFileNameWithoutExtension($project)
            $job = $jobs | Where-Object { $_.Name -eq $name }
            $code = Receive-Job -Job $job -ErrorAction SilentlyContinue | Select-Object -Last 1
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
            $testTimings[(Split-Path -Leaf $project)] = [double]($swBatch.Elapsed.TotalSeconds)
            $projectLog = Join-Path $artifacts ("test-" + (Split-Path -Leaf $project) + ".log")
            if (Test-Path -LiteralPath $projectLog) {
                $projectText = Get-Content -LiteralPath $projectLog -Raw
                if ($projectText) { Add-Content -LiteralPath $testLog -Value $projectText }
                Get-Content -LiteralPath $projectLog -Tail 3 | ForEach-Object { Write-Host "      $_" }
            }
            if ($code -ne 0) { $testFailures += $name }
        }
    }
}
else {
    # 默认：逐项目串行（唯一在受限宿主里也稳的口径）
    foreach ($batch in $testBatches) {
        foreach ($project in $batch) {
            Write-Host "[CI]   -> $project（串行）" -ForegroundColor DarkGray
            $swTest = [System.Diagnostics.Stopwatch]::StartNew()
            $projectLog = Join-Path $artifacts ("test-" + (Split-Path -Leaf $project) + ".log")
            & $dotnet test (Join-Path $repoRoot $project) -c $Configuration --no-build --nologo *> $projectLog
            if ($LASTEXITCODE -ne 0) { $testFailures += [IO.Path]::GetFileNameWithoutExtension($project) }
            $swTest.Stop()
            $testTimings[(Split-Path -Leaf $project)] = $swTest.Elapsed.TotalSeconds
            if (Test-Path -LiteralPath $projectLog) {
                $projectText = Get-Content -LiteralPath $projectLog -Raw
                if ($projectText) { Add-Content -LiteralPath $testLog -Value $projectText }
                Get-Content -LiteralPath $projectLog -Tail 3 | ForEach-Object { Write-Host "      $_" }
            }
        }
    }
}

$swTests.Stop()
$timings['③ 单测'] = $swTests.Elapsed.TotalSeconds
if ($testFailures.Count -gt 0) {
    Write-Host "[CI] 单元测试失败：$($testFailures -join ', ')" -ForegroundColor Red
    Write-TimingSummary
    exit 1
}
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

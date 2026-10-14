# Shared helpers for the update drills (drill-update.ps1 / drill-update-bridge.ps1 / drill-migrate-manual.ps1).
# Dot-source this file; it defines functions and the check counters but performs no work by itself.
# ASCII only (PowerShell 5.1 parses non-BOM files as ANSI; see WARNINGS).

$script:Passed = 0
$script:Failed = 0

function CheckOk([string]$msg)   { $script:Passed++; Write-Host ("  ok   " + $msg) }
function CheckFail([string]$msg) { $script:Failed++; Write-Host ("  FAIL " + $msg); }
function Check([bool]$cond, [string]$msg) { if ($cond) { CheckOk $msg } else { CheckFail $msg } }
function Section([string]$msg) { Write-Host ''; Write-Host "== $msg" }

function Write-Utf8([string]$path, [string]$text) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# expand 8.3 short names ($env:TEMP may be C:\Users\ADMINI~1\...): children FullName must line up with the root string
function Resolve-Full([string]$path) { return (Get-Item -LiteralPath $path -Force).FullName }

function New-DrillWorkDir([string]$WorkDir) {
    $work = if ($WorkDir -ne '') { $WorkDir } else { Join-Path $env:TEMP ("LinkPocket-Drill-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    return (Get-Item -LiteralPath $work -Force).FullName
}

# Native commands can write to stderr (the CLI writes its "# library:" line there);
# with ErrorActionPreference=Stop that would throw, so run them with a local preference.
function Invoke-NativeCapture([string]$exe, [string[]]$arguments, [bool]$mergeStderr) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($mergeStderr) { $output = & $exe @arguments 2>&1 | Out-String }
        else { $output = & $exe @arguments 2>$null | Out-String }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    return @{ Output = $output; Code = $code }
}

function Invoke-Robocopy([string]$Source, [string]$Destination, [string[]]$ExtraArgs) {
    $args = @($Source, $Destination, '/E', '/MT:8', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1') + $ExtraArgs
    $rc = Invoke-NativeCapture 'robocopy' $args $true
    if ($rc.Code -ge 8) { throw "robocopy failed (exit $($rc.Code)): $Source -> $Destination" }
}

function Get-TreeHashes([string]$root) {
    $result = @{}
    $full = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $full -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($full.Length + 1).Replace('\', '/')
        $result[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $result
}

function Test-Excluded([string]$rel, [string[]]$exclude) {
    foreach ($e in $exclude) { if ($rel -eq $e -or $rel.StartsWith($e)) { return $true } }
    return $false
}

function Compare-Tree([hashtable]$baseline, [string]$root, [string[]]$exclude, [string]$label) {
    $now = Get-TreeHashes $root
    $before = $script:Failed
    foreach ($rel in $baseline.Keys) {
        if (Test-Excluded $rel $exclude) { continue }
        if (-not $now.ContainsKey($rel)) { CheckFail "$label : missing '$rel'" }
        elseif ($now[$rel] -ne $baseline[$rel]) { CheckFail "$label : different bytes in '$rel'" }
    }
    foreach ($rel in $now.Keys) {
        if (Test-Excluded $rel $exclude) { continue }
        if (-not $baseline.ContainsKey($rel)) { CheckFail "$label : unexpected new file '$rel'" }
    }
    if ($script:Failed -eq $before) { CheckOk "$label : byte-identical ($($baseline.Count) baseline files)" }
}

function Invoke-DrillUpdater {
    param([string]$Staging, [string]$PackageDir, [string]$InstallDir, [int]$WaitPid = 0)
    $logPath = Join-Path $Staging 'update.log'
    $resultPath = Join-Path $Staging 'update-result.json'
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $exe = Join-Path $Staging 'LinkPocket-Update.exe'
    $cmdArgs = @('--package', $PackageDir, '--install', $InstallDir)
    if ($WaitPid -gt 0) { $cmdArgs += @('--wait-pid', "$WaitPid") }
    $captured = Invoke-NativeCapture $exe $cmdArgs $true
    $log = ''
    if (Test-Path -LiteralPath $logPath) { $log = Get-Content -LiteralPath $logPath -Raw }
    $result = $null
    if (Test-Path -LiteralPath $resultPath) { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json }
    return @{ ExitCode = $captured.Code; Output = $captured.Output; Log = $log; Result = $result; LogPath = $logPath; ResultPath = $resultPath }
}

# Mirrors LinkPocket.Update.PackageLayout.IsSkipped (dir entries end with '\', everything else is an exact file match).
function Test-SkipEntry([string]$rel, [string[]]$entries) {
    $r = $rel.Replace('/', '\')
    foreach ($e in $entries) {
        $n = $e.Replace('/', '\')
        if ($n.EndsWith('\')) {
            if ($r.StartsWith($n, [StringComparison]::OrdinalIgnoreCase)) { return $true }
            if ($r -eq $n.Substring(0, $n.Length - 1)) { return $true }
        }
        elseif ($r -eq $n) { return $true }
    }
    return $false
}

function Get-SkipEntries([string]$packageDir) {
    $entries = @()
    foreach ($line in (Get-Content -LiteralPath (Join-Path $packageDir 'update\skip-install.txt'))) {
        $t = $line.Trim()
        if ($t -ne '' -and -not $t.StartsWith('#')) { $entries += $t }
    }
    return $entries + @('LinkPocket-Update.exe', 'update\')
}

function Assert-NoSiblings([string]$workDir, [string]$label) {
    $leftovers = @(Get-ChildItem -LiteralPath $workDir -Force | Where-Object {
        $_.Name -like 'data.pending-*' -or $_.Name -like '*.old-*' })
    Check ($leftovers.Count -eq 0) "$label : no data.pending-* / *.old-* left beside the install"
    foreach ($l in $leftovers) { Write-Host "         left over: $($l.FullName)" }
}

# The program area of the installed tree must be exactly "package minus skip list", plus the launch marker.
# data\ is excluded from the install side: it is carried (direct move) or rebuilt (bridge), never installed.
function Assert-ProgramMirror([string]$PackageDir, [string]$InstallDir, [string]$Label) {
    $skipEntries = Get-SkipEntries $PackageDir
    $packageFiles = @(Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Force)
    $expectedPaths = @($packageFiles |
        ForEach-Object { $_.FullName.Substring($PackageDir.Length + 1) } |
        Where-Object { -not (Test-SkipEntry $_ $skipEntries) }) + @('launched.txt')
    $installPaths = @(Get-ChildItem -LiteralPath $InstallDir -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($InstallDir.Length + 1) } |
        Where-Object { -not $_.StartsWith('data\') })
    $missing = @($expectedPaths | Where-Object { $installPaths -notcontains $_ })
    $extra = @($installPaths | Where-Object { $expectedPaths -notcontains $_ })
    Check (($missing.Count -eq 0) -and ($extra.Count -eq 0)) "$Label : install file set == package - skipped + launched.txt ($($packageFiles.Count) package files, $($skipEntries.Count) skip entries)"
    foreach ($m in $missing) { Write-Host "         missing: $m" }
    foreach ($x in $extra) { Write-Host "         extra  : $x" }
}

function Get-FileVersionString([string]$exePath) {
    $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
    $p = [version]$v
    return ("{0}.{1}.{2}" -f $p.Major, $p.Minor, [Math]::Max($p.Build, 0))
}

function Run-Cli([string]$cli, [string]$db, [string[]]$cliArgs, [bool]$expectJson = $true) {
    $full = @('--db', $db) + $cliArgs
    if ($expectJson) { $full += '--json' }
    $captured = Invoke-NativeCapture $cli $full $false
    if ($captured.Code -ne 0) { throw "cli failed (exit $($captured.Code)): $($cliArgs -join ' ')" }
    return $captured.Output
}

# Builds a tiny "LinkPocket.exe" with the given FileVersion; when launched it writes launched.txt next to itself.
# The package's app executable in the drills is this stub (the real UI is never started); for the bridge drill
# it also relabels the old-side app executable to a >= 4.0.0 version (the app exe is version-read only there).
function Build-LinkPocketStub {
    param([string]$Dir, [string]$Version, [string]$Out)
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    $stubProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>LinkPocket</AssemblyName>
    <RootNamespace>LinkPocketDrillStub</RootNamespace>
    <Version>$Version</Version>
    <DebugType>none</DebugType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
"@
    $stubProgram = @'
using System;
using System.IO;

var marker = Path.Combine(AppContext.BaseDirectory, "launched.txt");
File.WriteAllText(marker, "launched " + DateTime.UtcNow.ToString("o") + Environment.NewLine);
return 0;
'@
    Write-Utf8 (Join-Path $Dir 'stub.csproj') $stubProject
    Write-Utf8 (Join-Path $Dir 'Program.cs') $stubProgram
    $build = Invoke-NativeCapture 'dotnet' @('publish', $Dir, '-c', 'Release', '-o', $Out, '--nologo', '-v:q') $true
    if ($build.Code -ne 0) { throw "stub build failed (exit $($build.Code)): $($build.Output)" }
}

function Complete-Drill([string]$Title, [string]$WorkDir, [bool]$Keep) {
    Write-Host ''
    Write-Host ('=' * 60)
    if ($script:Failed -eq 0) {
        Write-Host "$Title PASSED: $($script:Passed) checks"
        if (-not $Keep -and $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
        exit 0
    } else {
        Write-Host "$Title FAILED: $($script:Failed) of $($script:Passed + $script:Failed) checks failed" -ForegroundColor Red
        Write-Host "work dir kept: $WorkDir"
        exit 1
    }
}

function Abort-Drill([string]$Message, [string]$WorkDir) {
    Write-Host ''
    Write-Host "DRILL ABORTED: $Message" -ForegroundColor Red
    Write-Host "work dir kept: $WorkDir"
    exit 1
}

# LinkPocket update drill 1 - same-epoch direct move, end to end (UPDATE.md 5.3 direct / batch H).
#
# Drives the REAL LinkPocket-Update.exe (extracted from a real package) through the full
# direct-move flow against a throw-away install built from that same package:
#   S0  stale recovery artifacts beside the install  -> refused, nothing touched
#   S2  a malformed settings step                    -> failure + full rollback to original bytes
#   S1  happy path: synthetic step chain, runtime-trace prune, settings-manifest
#       reconciliation, skip list, launch, byte-identical data, engine-count reconciliation
#
# The package's app executable is replaced by a tiny stub with the right FileVersion
# (it writes a launch marker and exits) so the drill never opens the real UI; the real
# tools\linkpocket.exe from the package seeds and verifies the database.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build\drill-update.ps1
#         powershell -ExecutionPolicy Bypass -File build\drill-update.ps1 -Package <dir> -WorkDir <dir> -Keep
# Exit:   0 = all checks passed; 1 = failures (the work directory is kept for inspection).

[CmdletBinding()]
param(
    [string]$Package = '',
    [string]$WorkDir = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'drill-common.ps1')

if ($Package -eq '') { $Package = Join-Path $PSScriptRoot 'artifacts\dist' }
$script:work = New-DrillWorkDir $WorkDir

function Run-Direct {
    $work = $script:work
    if (-not (Test-Path -LiteralPath $Package)) { throw "the package directory does not exist: $Package (run pack.ps1 first)" }
    $packageSrc = Resolve-Full $Package
    $installDir = Join-Path $work 'LinkPocket'
    $packageDir = Join-Path $work 'package'
    $stagingDir = Join-Path $work 'staging'

    Write-Host "LinkPocket update drill 1 (same-epoch direct move)"
    Write-Host "  package  : $packageSrc"
    Write-Host "  work dir : $work"

    foreach ($required in @('LinkPocket.exe', 'LinkPocket-Update.exe', 'update\settings-manifest.json', 'update\skip-install.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageSrc $required))) {
            throw "the package is missing '$required' - run pack.ps1 first: $packageSrc"
        }
    }

    $vOld = Get-FileVersionString (Join-Path $packageSrc 'LinkPocket.exe')
    $p = [version]$vOld
    $vNew1 = "{0}.{1}.{2}" -f $p.Major, $p.Minor, ($p.Build + 1)
    $vNew2 = "{0}.{1}.{2}" -f $p.Major, $p.Minor, ($p.Build + 2)
    Write-Host "  app version: $vOld  (chain: $vOld -> $vNew1 -> $vNew2)"

    Section 'build the version-carrying stub (package app executable)'
    Build-LinkPocketStub -Dir (Join-Path $work 'stub') -Version $vNew2 -Out (Join-Path $work 'stub-out')
    $stubOut = Join-Path $work 'stub-out'
    Check ((Get-FileVersionString (Join-Path $stubOut 'LinkPocket.exe')) -eq $vNew2) "stub carries version $vNew2"

    Section 'build the throw-away trees (install = current app, package = delivered content)'
    # /XF takes full paths here: a bare "LinkPocket.exe" would also exclude tools\linkpocket.exe (name match)
    Invoke-Robocopy $packageSrc $installDir @('/XF', (Join-Path $packageSrc 'LinkPocket-Update.exe'), '/XD', 'update')
    Invoke-Robocopy $packageSrc $packageDir @('/XF', (Join-Path $packageSrc 'LinkPocket.exe'))
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageSrc 'LinkPocket-Update.exe') -Destination $stagingDir -Force
    Copy-Item -Path (Join-Path $stubOut '*') -Destination $packageDir -Force

    Check (Test-Path -LiteralPath (Join-Path $installDir 'LinkPocket.exe')) 'install keeps the current app executable'
    Check (Test-Path -LiteralPath (Join-Path $packageDir 'tools\linkpocket.exe')) 'package keeps tools\linkpocket.exe (the root-exe exclusion did not catch it)'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $vOld) "install app carries version $vOld"
    Check ((Get-FileVersionString (Join-Path $packageDir 'LinkPocket.exe')) -eq $vNew2) "package app carries version $vNew2"
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'LinkPocket-Update.exe'))) 'install starts without the updater (as installed trees do)'
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'update'))) 'install starts without update\ (as installed trees do)'

    Section 'seed real data through the real CLI (linkpocket.db)'
    $cli = Join-Path $installDir 'tools\linkpocket.exe'
    $db = Join-Path $installDir 'data\linkpocket.db'
    $folderJson = Run-Cli $cli $db @('folders', 'create', '--name', 'Drill')
    $folderId = ($folderJson | ConvertFrom-Json).data.id
    Check ([bool]$folderId) "folder created (id $folderId)"
    Run-Cli $cli $db @('links', 'create', '--url', 'https://drill.example/one', '--title', 'One') | Out-Null
    Run-Cli $cli $db @('links', 'create', '--url', 'https://drill.example/two', '--title', 'Two', '--list_id', $folderId) | Out-Null
    # PS 5.1 does not escape embedded double quotes when passing to native commands: escape them ourselves.
    $macroArgs = '{"name":"drill-macro","script":{"steps":[{"ref":"s1","command":"links.stats","args":{}}]}}'.Replace('"', '\"')
    Run-Cli $cli $db @('call', 'macro.save', '--args', $macroArgs) | Out-Null
    $preStats = Run-Cli $cli $db @('links', 'stats')
    $preQuery = Run-Cli $cli $db @('links', 'query')
    Check (Test-Path -LiteralPath (Join-Path $installDir 'data\linkpocket-ops.db')) 'seeded mutations created linkpocket-ops.db'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'data\macros.json')) 'macro.save created macros.json'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'data\undo-journal.cli.json')) 'seeded mutations created undo-journal.cli.json'

    Section 'plant data fixtures (settings, caches, runtime traces, foreign files)'
    $data = Join-Path $installDir 'data'
    Write-Utf8 (Join-Path $data 'ui-preferences.json') '{"version":1,"appearance":{"themeName":"drill","legacyFlag":true},"language":"zh-CN"}'
    Write-Utf8 (Join-Path $data 'ai\preferences.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $data 'ai\providers.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $data 'ai\credentials.json') '{"version":8,"drill":true}'
    Write-Utf8 (Join-Path $data 'ai\skills.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $data 'ai\sessions\drill-session.json') '{"drill":true}'
    Write-Utf8 (Join-Path $data 'ai\usage.json') '{"drill":true}'
    Write-Utf8 (Join-Path $data 'update-preferences.json') '{"auto_check":true}'
    Write-Utf8 (Join-Path $data 'undo-journal.json') '{"drill":true}'
    $random = New-Object System.Random 424242
    $fontBytes = New-Object byte[] 256
    $random.NextBytes($fontBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $data 'fonts\drill-font.ttf') -Force).FullName, $fontBytes)
    $iconBytes = New-Object byte[] 128
    $random.NextBytes($iconBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $data 'favicons\ab\deadbeef.png') -Force).FullName, $iconBytes)
    $foreignBytes = New-Object byte[] 1024
    $random.NextBytes($foreignBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $data 'weird\custom.bin') -Force).FullName, $foreignBytes)
    CheckOk 'fixtures planted (they must survive byte-identical unless they are a prune target)'

    Section 'write the synthetic step chain + patched manifest into the package'
    $stepsDir = Join-Path $packageDir 'update\steps'
    $manifestPath = Join-Path $packageDir 'update\settings-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($store in $manifest.stores) { if ($store.file -eq 'ui-preferences.json') { $store.version = 3 } }
    Write-Utf8 $manifestPath ($manifest | ConvertTo-Json -Depth 6)
    Write-Utf8 (Join-Path $stepsDir "$vOld-$vNew1.json") @"
{
  "from": "$vOld",
  "to": "$vNew1",
  "changes": [
    {
      "file": "ui-preferences.json",
      "expect_version": 1,
      "set_version": 2,
      "ops": [
        { "op": "rename", "from": "appearance.themeName", "to": "appearance.theme" }
      ]
    }
  ]
}
"@
    Write-Utf8 (Join-Path $stepsDir "$vNew1-$vNew2.json") @"
{
  "from": "$vNew1",
  "to": "$vNew2",
  "changes": [
    {
      "file": "ui-preferences.json",
      "expect_version": 2,
      "set_version": 3,
      "ops": [
        { "op": "set_default", "path": "appearance.fontScale", "value": 1.0 },
        { "op": "remove", "path": "appearance.legacyFlag" }
      ]
    }
  ]
}
"@
    Write-Utf8 (Join-Path $stepsDir '0.0.1-0.0.2.json') @"
{
  "from": "0.0.1",
  "to": "0.0.2",
  "changes": [
    { "file": "ui-preferences.json", "ops": [ { "op": "set_default", "path": "ancientStepRan", "value": true } ] }
  ]
}
"@
    Write-Utf8 (Join-Path $stepsDir '99.0.0-99.1.0.json') @"
{
  "from": "99.0.0",
  "to": "99.1.0",
  "changes": [
    { "file": "ui-preferences.json", "ops": [ { "op": "set_default", "path": "futureStepRan", "value": true } ] }
  ]
}
"@
    CheckOk "chain written: $vOld->$vNew1, $vNew1->$vNew2 apply; 0.0.1->0.0.2 and 99.0.0->99.1.0 must not"

    $baseline = Get-TreeHashes $data
    Write-Host "  baseline: $($baseline.Count) files under data\"

    # ---------------- S0: stale recovery artifacts -> refuse, touch nothing ----------------
    Section 'S0: stale recovery artifacts beside the install are refused'
    $staleDir = Join-Path $work 'data.pending-drilltest'
    New-Item -ItemType Directory -Path $staleDir -Force | Out-Null
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir
    $err = ''
    if ($run.Result) { $err = "$($run.Result.error)" }
    Check ($run.ExitCode -eq 1) "S0: exit code 1 (got $($run.ExitCode))"
    Check ($err.Contains('interrupted')) "S0: error names the interrupted-previous-update clue"
    Check ($run.Result -and -not $run.Result.ok) 'S0: update-result.json says ok=false'
    Compare-Tree $baseline $data @() 'S0'
    Remove-Item -LiteralPath $staleDir -Recurse -Force

    # ---------------- S2: malformed step -> failure + full rollback ----------------
    Section 'S2: malformed settings step fails the update and rolls back to the original bytes'
    $brokenStep = Join-Path $stepsDir 'zz-broken.json'
    Write-Utf8 $brokenStep '{ "from": "1.0.0",'
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir
    $err = ''
    if ($run.Result) { $err = "$($run.Result.error)" }
    Check ($run.ExitCode -eq 1) "S2: exit code 1 (got $($run.ExitCode))"
    Check ($err.Contains('zz-broken.json') -or $run.Log.Contains('zz-broken.json')) 'S2: the broken step is named'
    Check ($err.Contains('not valid JSON')) 'S2: error says the step is not valid JSON'
    Check ($run.Result -and -not $run.Result.ok) 'S2: update-result.json says ok=false'
    Check ($run.Log.Contains('rolling back')) 'S2: log shows the rollback began'
    Check ($run.Log.Contains('old install restored')) 'S2: log shows the old install was restored'
    Check ($run.Log.Contains('data restored')) 'S2: log shows the data directory was restored'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $vOld) "S2: install app version is back to $vOld"
    Compare-Tree $baseline $data @() 'S2 (rollback)'
    Assert-NoSiblings $work 'S2'
    Check ((Test-Path -LiteralPath $run.ResultPath) -and (Test-Path -LiteralPath $run.LogPath)) 'S2: staging keeps update.log + update-result.json'
    Remove-Item -LiteralPath $brokenStep -Force

    # ---------------- S1: happy path ----------------
    Section 'S1: happy path (direct move, synthetic chain, prune, reconciliation, launch)'
    $dummy = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c ping -n 5 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir -WaitPid $dummy.Id
    Check ($run.ExitCode -eq 0) "S1: exit code 0 (got $($run.ExitCode))"
    if ($run.ExitCode -ne 0) { Write-Host $run.Output; return }

    $log = $run.Log
    Check ($log.Contains("waiting for process $($dummy.Id)")) 'S1: waited for --wait-pid'
    Check ($log.Contains("process $($dummy.Id) exited")) 'S1: the waited-for process exited'
    Check ($log.Contains("mode: direct ($vOld -> $vNew2)")) "S1: routed to the direct path ($vOld -> $vNew2)"
    $idx = -1; $ordered = $true
    foreach ($stage in @('move-data', 'move-old', 'install', 'restore-data', 'convert', 'cleanup-old', 'launch')) {
        $i = $log.IndexOf("stage: $stage")
        if ($i -lt 0 -or $i -le $idx) { $ordered = $false; break }
        $idx = $i
    }
    Check $ordered 'S1: stages appear in the frozen order'
    Check ($log.Contains("applying settings step $vOld -> $vNew1")) 'S1: chain step 1 applied'
    Check ($log.Contains("applying settings step $vNew1 -> $vNew2")) 'S1: chain step 2 applied'
    Check ($log.Contains('new version launched')) 'S1: new version launch line present'

    Section 'S1: update-result.json'
    $result = $run.Result
    Check ($result -and $result.ok) 'result: ok=true'
    Check ($result.mode -eq 'direct') 'result: mode=direct'
    Check ($result.from_version -eq $vOld) "result: from_version=$vOld"
    Check ($result.to_version -eq $vNew2) "result: to_version=$vNew2"
    $notes = @($result.notes)
    Check (($notes.Count -eq 1) -and ("$($notes[0])".Contains('ai/credentials.json'))) 'result: exactly one note - the dropped out-of-version settings file'

    Section 'S1: install tree (skip list, completeness, launch)'
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'update'))) 'install has no update\ directory'
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'LinkPocket-Update.exe'))) 'install has no updater executable'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $vNew2) "install app version is $vNew2"
    Check (Test-Path -LiteralPath (Join-Path $installDir 'tools\linkpocket.exe')) 'install has tools\linkpocket.exe'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'mcp.example.json')) 'install has mcp.example.json'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'AGENTS.md')) 'install has AGENTS.md'
    $markerPath = Join-Path $installDir 'launched.txt'
    # the stub is a real child process: give it a moment to start and write its marker
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $markerPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check (Test-Path -LiteralPath $markerPath) 'new version launched (launch marker written)'
    if (Test-Path -LiteralPath $markerPath) { Check ((Get-Content -LiteralPath $markerPath -Raw).StartsWith('launched ')) 'launch marker content looks right' }
    Assert-ProgramMirror $packageDir $installDir 'S1'
    Assert-NoSiblings $work 'S1'

    Section 'S1: data area (prune, conversion, byte-identity)'
    foreach ($rel in @('linkpocket-ops.db', 'linkpocket-ops.db-wal', 'linkpocket-ops.db-shm',
                       'undo-journal.json', 'undo-journal.cli.json', 'undo-journal.cli.json.parts',
                       'ai\usage.json', 'favicons')) {
        Check (-not (Test-Path -LiteralPath (Join-Path $data $rel))) "pruned: $rel is gone"
    }
    Check (Test-Path -LiteralPath (Join-Path $data 'linkpocket.db')) 'the database stayed in place'
    $prefsRaw = Get-Content -LiteralPath (Join-Path $data 'ui-preferences.json') -Raw
    $prefs = $prefsRaw | ConvertFrom-Json
    Check ($prefs.version -eq 3) 'ui-preferences: set_version walked 2 then 3'
    Check ($prefs.appearance.theme -eq 'drill') 'ui-preferences: field renamed to appearance.theme'
    Check ($prefs.appearance.fontScale -eq 1.0) 'ui-preferences: default added'
    $appearanceKeys = @($prefs.appearance.PSObject.Properties.Name)
    Check (-not ($appearanceKeys -contains 'themeName')) 'ui-preferences: old field name gone'
    Check (-not ($appearanceKeys -contains 'legacyFlag')) 'ui-preferences: obsolete field removed'
    Check ($prefs.language -eq 'zh-CN') 'ui-preferences: untouched field kept'
    Check ((-not $prefsRaw.Contains('ancientStepRan')) -and (-not $prefsRaw.Contains('futureStepRan'))) 'out-of-window steps did not run'
    Check (-not (Test-Path -LiteralPath (Join-Path $data 'ai\credentials.json'))) 'out-of-version settings file was dropped'
    Compare-Tree $baseline $data @('ui-preferences.json', 'ai/credentials.json',
                                   'linkpocket-ops.db', 'undo-journal.json', 'undo-journal.cli.json',
                                   'ai/usage.json', 'favicons/') 'S1 (data)'

    Section 'S1: the moved database is still the same database to the engine'
    $postStats = Run-Cli (Join-Path $installDir 'tools\linkpocket.exe') (Join-Path $data 'linkpocket.db') @('links', 'stats')
    $postQuery = Run-Cli (Join-Path $installDir 'tools\linkpocket.exe') (Join-Path $data 'linkpocket.db') @('links', 'query')
    Check (($preStats -eq $postStats) -and $preStats) 'links.stats identical before/after the update'
    Check (($preQuery -eq $postQuery) -and $preQuery) 'links.query identical before/after the update'
}

try {
    Run-Direct
} catch {
    Abort-Drill "$_" $script:work
}

Complete-Drill 'DRILL 1' $script:work $Keep

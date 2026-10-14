# LinkPocket update drill 2 - cross-epoch bridge, end to end (UPDATE.md 5.3 bridge / batch H).
#
# Drives the REAL LinkPocket-Update.exe through the export/import bridge against an old-epoch install.
# The bridge serves future cross-epoch updates; today the only real older-epoch data in existence is
# the 3.2.4 cohort (user_version = 8), and its tools\linkpocket.exe is exactly the frozen old-side CLI
# the bridge drives. So the drill builds the constellation from the real LinkPocket_v3.2.4.zip:
#   - old side : the extracted 3.2.4 release + a real epoch-8 database seeded by its own CLI.
#                Placing the db at data\linkpocket.db is the artificial part (the old cohort stored it
#                flat; the bridge expects data\).
#   - relabel  : the old app executable is replaced by a stub carrying version 4.0.0. The app exe is
#                version-read only in this flow (never launched), and the relabel simulates a future
#                cross-epoch update whose install clears the "before 4.0.0" gate. The old CLI stays
#                the REAL 3.2.4 binary - it does the actual export work.
#   - package  : the current package with its app executable stubbed to 4.0.2 (never opens the UI).
#   - chain    : synthetic settings steps 4.0.0 -> 4.0.1 -> 4.0.2 (the settings conversion face of
#                the bridge), with out-of-window negative controls.
#
# Scenarios:
#   S0  old CLI missing            -> preflight refuses, nothing touched
#   S2  package importer sabotaged -> export succeeded, import fails, full rollback; the staging
#                                     area keeps the exported backup
#   S1  happy path: export -> carry set + settings chain -> install -> import + count reconciliation
#       -> settings land -> cleanup -> launch; the trash is not carried
#
# Usage:  powershell -ExecutionPolicy Bypass -File build\drill-update-bridge.ps1
#         powershell -ExecutionPolicy Bypass -File build\drill-update-bridge.ps1 -Package <dir> -OldZip <path> -Keep
# Exit:   0 = all checks passed; 1 = failures (the work directory is kept for inspection).

[CmdletBinding()]
param(
    [string]$Package = '',
    [string]$OldZip = '',
    [string]$WorkDir = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'drill-common.ps1')

if ($Package -eq '') { $Package = Join-Path $PSScriptRoot 'artifacts\dist' }
if ($OldZip -eq '') { $OldZip = Join-Path $PSScriptRoot '..\..\..\LinkPocket_v3.2.4.zip' }
$script:work = New-DrillWorkDir $WorkDir

$OldStubVersion = '4.0.0'   # see the header: the old side is relabeled to clear the "before 4.0.0" gate
$NewStubVersion = '4.0.2'

function Run-Bridge {
    $work = $script:work
    if (-not (Test-Path -LiteralPath $Package)) { throw "the package directory does not exist: $Package (run pack.ps1 first)" }
    $packageSrc = Resolve-Full $Package
    if (-not (Test-Path -LiteralPath $OldZip)) { throw "the old 3.2.4 release zip is missing: $OldZip" }
    $oldZipPath = Resolve-Full $OldZip
    $installDir = Join-Path $work 'LinkPocket'
    $packageDir = Join-Path $work 'package'
    $stagingDir = Join-Path $work 'staging'

    Write-Host "LinkPocket update drill 2 (cross-epoch bridge)"
    Write-Host "  package  : $packageSrc"
    Write-Host "  old zip  : $oldZipPath"
    Write-Host "  work dir : $work"

    foreach ($required in @('LinkPocket.exe', 'LinkPocket-Update.exe', 'update\settings-manifest.json', 'update\skip-install.txt', 'tools\linkpocket.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageSrc $required))) {
            throw "the package is missing '$required' - run pack.ps1 first: $packageSrc"
        }
    }

    Section 'extract the old 3.2.4 release (a real epoch-8 data source + the real old CLI)'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($oldZipPath, $installDir)
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'data'))) 'the old release ships without data\ (the old cohort stored the db flat)'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq '3.2.4') 'the extracted old app is version 3.2.4'
    $oldCli = Join-Path $installDir 'tools\linkpocket.exe'
    Check ((Test-Path -LiteralPath $oldCli) -and ((Get-Item -LiteralPath $oldCli).Length -gt 50MB)) 'the old CLI is the real 3.2.4 binary (not a stub)'
    $oldCliHash = (Get-FileHash -LiteralPath $oldCli -Algorithm SHA256).Hash

    Section 'build the version-carrying stubs (old side relabel + package app)'
    Build-LinkPocketStub -Dir (Join-Path $work 'stub-old') -Version $OldStubVersion -Out (Join-Path $work 'stub-old-out')
    Build-LinkPocketStub -Dir (Join-Path $work 'stub-new') -Version $NewStubVersion -Out (Join-Path $work 'stub-new-out')
    Copy-Item -Path (Join-Path $work 'stub-old-out\*') -Destination $installDir -Force
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $OldStubVersion) "old side relabeled to $OldStubVersion (app exe is version-read only here)"
    Check ((Get-FileHash -LiteralPath $oldCli -Algorithm SHA256).Hash -eq $oldCliHash) 'the old CLI is untouched by the relabel'

    Section 'build the package tree (delivered content, app exe stubbed)'
    Invoke-Robocopy $packageSrc $packageDir @('/XF', (Join-Path $packageSrc 'LinkPocket.exe'))
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageSrc 'LinkPocket-Update.exe') -Destination $stagingDir -Force
    Copy-Item -Path (Join-Path $work 'stub-new-out\*') -Destination $packageDir -Force
    Check ((Get-FileVersionString (Join-Path $packageDir 'LinkPocket.exe')) -eq $NewStubVersion) "package app carries version $NewStubVersion"
    Check (Test-Path -LiteralPath (Join-Path $packageDir 'tools\linkpocket.exe')) 'package keeps the real new CLI (used for the import)'

    Section 'seed the old-format database with the old CLI (epoch 8, artificial data\ placement)'
    $oldDb = Join-Path $installDir 'data\linkpocket.db'
    $folderJson = Run-Cli $oldCli $oldDb @('folders', 'create', '--name', 'OldFolderA')
    $folderId = ($folderJson | ConvertFrom-Json).data.id
    Run-Cli $oldCli $oldDb @('folders', 'create', '--name', 'OldFolderB') | Out-Null
    Run-Cli $oldCli $oldDb @('links', 'create', '--url', 'https://old.example/one', '--title', 'OldOne', '--list_id', $folderId) | Out-Null
    Run-Cli $oldCli $oldDb @('links', 'create', '--url', 'https://old.example/two', '--title', 'OldTwo', '--list_id', $folderId) | Out-Null
    Run-Cli $oldCli $oldDb @('links', 'create', '--url', 'https://old.example/three', '--title', 'OldThree') | Out-Null
    $trashedJson = Run-Cli $oldCli $oldDb @('links', 'create', '--url', 'https://old.example/trashed-one', '--title', 'OldTrashed')
    $trashedId = ($trashedJson | ConvertFrom-Json).data.id
    Run-Cli $oldCli $oldDb @('links', 'trash', '--id', $trashedId) | Out-Null
    $oldMacroArgs = '{"name":"OldMacro","script":{"steps":[{"ref":"s1","command":"links.stats","args":{}}]}}'.Replace('"', '\"')
    Run-Cli $oldCli $oldDb @('call', 'macro.save', '--args', $oldMacroArgs) | Out-Null
    CheckOk 'old db seeded: 2 folders, 3 active links, 1 trashed link, 1 in-db macro (epoch 8)'

    Section 'plant carry-set fixtures in the old data area'
    $oldData = Join-Path $installDir 'data'
    Write-Utf8 (Join-Path $oldData 'ui-preferences.json') '{"version":1,"appearance":{"themeName":"drill","legacyFlag":true},"language":"zh-CN"}'
    Write-Utf8 (Join-Path $oldData 'ai\preferences.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $oldData 'ai\providers.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $oldData 'ai\credentials.json') '{"version":8,"drill":true}'
    Write-Utf8 (Join-Path $oldData 'ai\skills.json') '{"version":1,"drill":true}'
    Write-Utf8 (Join-Path $oldData 'ai\sessions\drill-session.json') '{"drill":true}'
    Write-Utf8 (Join-Path $oldData 'ai\usage.json') '{"drill":true}'
    Write-Utf8 (Join-Path $oldData 'update-preferences.json') '{"auto_check":true}'
    Write-Utf8 (Join-Path $oldData 'macros.json') '{"version":1,"macros":[{"name":"FileMacro","script_json":"{\"steps\":[{\"ref\":\"s1\",\"command\":\"links.stats\",\"args\":{}}]}","created_at":"2026-01-01T00:00:00+00:00","updated_at":"2026-01-01T00:00:00+00:00"}]}'
    Write-Utf8 (Join-Path $oldData 'undo-journal.json') '{"drill":true}'
    $random = New-Object System.Random 424242
    $fontBytes = New-Object byte[] 256
    $random.NextBytes($fontBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $oldData 'fonts\drill-font.ttf') -Force).FullName, $fontBytes)
    $iconBytes = New-Object byte[] 128
    $random.NextBytes($iconBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $oldData 'favicons\ab\deadbeef.png') -Force).FullName, $iconBytes)
    $foreignBytes = New-Object byte[] 1024
    $random.NextBytes($foreignBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $oldData 'weird\custom.bin') -Force).FullName, $foreignBytes)
    $planted = @{
        'update-preferences.json'        = (Get-FileHash -LiteralPath (Join-Path $oldData 'update-preferences.json') -Algorithm SHA256).Hash
        'macros.json'                    = (Get-FileHash -LiteralPath (Join-Path $oldData 'macros.json') -Algorithm SHA256).Hash
        'fonts\drill-font.ttf'           = (Get-FileHash -LiteralPath (Join-Path $oldData 'fonts\drill-font.ttf') -Algorithm SHA256).Hash
        'ai\preferences.json'            = (Get-FileHash -LiteralPath (Join-Path $oldData 'ai\preferences.json') -Algorithm SHA256).Hash
        'ai\sessions\drill-session.json' = (Get-FileHash -LiteralPath (Join-Path $oldData 'ai\sessions\drill-session.json') -Algorithm SHA256).Hash
    }
    CheckOk 'fixtures planted: carry set (must land byte-identical) + decoys (must not land)'

    Section 'write the synthetic step chain + patched manifest into the package'
    $stepsDir = Join-Path $packageDir 'update\steps'
    $manifestPath = Join-Path $packageDir 'update\settings-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($store in $manifest.stores) { if ($store.file -eq 'ui-preferences.json') { $store.version = 3 } }
    Write-Utf8 $manifestPath ($manifest | ConvertTo-Json -Depth 6)
    Write-Utf8 (Join-Path $stepsDir "$OldStubVersion-4.0.1.json") @"
{
  "from": "$OldStubVersion",
  "to": "4.0.1",
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
    Write-Utf8 (Join-Path $stepsDir "4.0.1-$NewStubVersion.json") @"
{
  "from": "4.0.1",
  "to": "$NewStubVersion",
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
    CheckOk "chain written: $OldStubVersion->4.0.1->$NewStubVersion apply; 0.0.1->0.0.2 and 99.0.0->99.1.0 must not"

    $baseline = Get-TreeHashes $oldData
    Write-Host "  baseline: $($baseline.Count) files under the old data\"

    # ---------------- S0: old CLI missing -> preflight refuses, touch nothing ----------------
    Section 'S0: a missing old CLI is refused before anything is touched'
    $oldCliBackup = $oldCli + '.drillbak'
    Move-Item -LiteralPath $oldCli -Destination $oldCliBackup
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir
    $err = ''
    if ($run.Result) { $err = "$($run.Result.error)" }
    Check ($run.ExitCode -eq 1) "S0: exit code 1 (got $($run.ExitCode))"
    Check ($err.Contains('old command-line client')) 'S0: error names the missing old CLI'
    Compare-Tree $baseline $oldData @() 'S0'
    Assert-NoSiblings $work 'S0'
    Move-Item -LiteralPath $oldCliBackup -Destination $oldCli
    Check ((Get-FileHash -LiteralPath $oldCli -Algorithm SHA256).Hash -eq $oldCliHash) 'S0: the old CLI is back in place'

    # ---------------- S2: sabotaged importer -> export ok, import fails, full rollback ----------------
    Section 'S2: a broken new CLI fails the import and rolls back (the export stays in staging)'
    $newCliInPackage = Join-Path $packageDir 'tools\linkpocket.exe'
    Write-Utf8 $newCliInPackage 'not-a-real-executable'
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir
    $err = ''
    if ($run.Result) { $err = "$($run.Result.error)" }
    Check ($run.ExitCode -eq 1) "S2: exit code 1 (got $($run.ExitCode))"
    Check ($err.Contains('backup.import')) 'S2: error names backup.import'
    Check ($run.Log.Contains('exported 2 folders / 3 bookmarks')) 'S2: the export had already succeeded'
    Check ($run.Log.Contains('rolling back')) 'S2: log shows the rollback began'
    Check ($run.Log.Contains('old install restored')) 'S2: log shows the old install was restored'
    Check ($run.Log.Contains('the staging area is kept')) 'S2: log says the staging area is kept'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $OldStubVersion) "S2: old install is back (app version $OldStubVersion)"
    Check ((Get-FileHash -LiteralPath $oldCli -Algorithm SHA256).Hash -eq $oldCliHash) 'S2: the old CLI is back (real 3.2.4 binary)'
    # the old CLI's export legitimately appended an audit row to its own ops database (runtime records,
    # never carried); everything else - above all the main database - must be back byte-identical.
    Compare-Tree $baseline $oldData @('linkpocket-ops.db') 'S2 (rollback)'
    Check ((Get-FileHash -LiteralPath (Join-Path $oldData 'linkpocket.db') -Algorithm SHA256).Hash -eq $baseline['linkpocket.db']) 'S2: the main database is byte-identical after rollback'
    Assert-NoSiblings $work 'S2'
    $exportedBackup = Join-Path $stagingDir 'bridge\bookmarks.lpbackup'
    Check (Test-Path -LiteralPath $exportedBackup) 'S2: the staging area holds the exported backup'
    Copy-Item -LiteralPath (Join-Path $packageSrc 'tools\linkpocket.exe') -Destination $newCliInPackage -Force
    Check ((Get-Item -LiteralPath $newCliInPackage).Length -gt 50MB) 'S2: the real new CLI is restored in the package'

    # ---------------- S1: happy path ----------------
    Section 'S1: happy path (bridge: export, carry, import, reconcile, land, launch)'
    $dummy = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c ping -n 5 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageDir -InstallDir $installDir -WaitPid $dummy.Id
    Check ($run.ExitCode -eq 0) "S1: exit code 0 (got $($run.ExitCode))"
    if ($run.ExitCode -ne 0) { Write-Host $run.Output; return }

    $log = $run.Log
    Check ($log.Contains("waiting for process $($dummy.Id)")) 'S1: waited for --wait-pid'
    Check ($log.Contains("mode: bridge ($OldStubVersion -> $NewStubVersion)")) "S1: routed to the bridge ($OldStubVersion -> $NewStubVersion)"
    $idx = -1; $ordered = $true
    foreach ($stage in @('export', 'copy-settings', 'move-old', 'install', 'import', 'land-settings', 'cleanup-old', 'launch')) {
        $i = $log.IndexOf("stage: $stage")
        if ($i -lt 0 -or $i -le $idx) { $ordered = $false; break }
        $idx = $i
    }
    Check $ordered 'S1: stages appear in the frozen order'
    Check ($log.Contains('exported 2 folders / 3 bookmarks (sha256')) 'S1: export ran with the old CLI and reported counts'
    Check ($log.Contains('imported 2 folders / 3 bookmarks (counts match the export)')) 'S1: import counts match the export'
    Check ($log.Contains("applying settings step $OldStubVersion -> 4.0.1")) 'S1: chain step 1 applied on the carry copy'
    Check ($log.Contains("applying settings step 4.0.1 -> $NewStubVersion")) 'S1: chain step 2 applied on the carry copy'
    Check ($log.Contains('not carried: ai\usage.json')) 'S1: usage statistics are not carried'
    Check ($log.Contains('carried: ui-preferences.json')) 'S1: settings are carried'
    Check ($log.Contains('new version launched')) 'S1: new version launch line present'

    Section 'S1: update-result.json'
    $result = $run.Result
    Check ($result -and $result.ok) 'result: ok=true'
    Check ($result.mode -eq 'bridge') 'result: mode=bridge'
    Check ($result.from_version -eq $OldStubVersion) "result: from_version=$OldStubVersion"
    Check ($result.to_version -eq $NewStubVersion) "result: to_version=$NewStubVersion"
    Check (($result.counts.exported_folders -eq 2) -and ($result.counts.exported_links -eq 3)) 'result: exported counts recorded'
    Check (($result.counts.imported_folders -eq 2) -and ($result.counts.imported_links -eq 3)) 'result: imported counts recorded'
    $notes = @($result.notes)
    Check (($notes.Count -ge 1) -and ("$($notes -join ' ')".Contains('ai/credentials.json'))) 'result: notes name the dropped out-of-version settings file'

    Section 'S1: install tree + launch'
    Check ((Get-FileVersionString (Join-Path $installDir 'LinkPocket.exe')) -eq $NewStubVersion) "install app version is $NewStubVersion"
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'update'))) 'install has no update\ directory'
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'LinkPocket-Update.exe'))) 'install has no updater executable'
    $markerPath = Join-Path $installDir 'launched.txt'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $markerPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check (Test-Path -LiteralPath $markerPath) 'new version launched (launch marker written)'
    Assert-ProgramMirror $packageDir $installDir 'S1'
    Assert-NoSiblings $work 'S1'

    Section 'S1: the imported data (engine view through the new CLI)'
    $newData = Join-Path $installDir 'data'
    $newCli = Join-Path $installDir 'tools\linkpocket.exe'
    $newDb = Join-Path $newData 'linkpocket.db'
    Check (Test-Path -LiteralPath $newDb) 'the imported database exists'
    $stats = (Run-Cli $newCli $newDb @('links', 'stats')) | ConvertFrom-Json
    Check ($stats.total -eq 3) "links.stats total is 3 (got $($stats.total))"
    Check ($stats.trash -eq 0) 'the trash was NOT carried (trash count is 0)'
    Check ($stats.root_level -eq 1) 'root-level link count survived'
    $query = (Run-Cli $newCli $newDb @('links', 'query')) | ConvertFrom-Json
    $urls = @($query.items | ForEach-Object { $_.url })
    $expectedUrls = @('https://old.example/one', 'https://old.example/two', 'https://old.example/three')
    Check ((($urls | Sort-Object) -join ',') -eq (($expectedUrls | Sort-Object) -join ',')) 'the three active links survived (urls match)'
    Check ($urls -notcontains 'https://old.example/trashed-one') 'the trashed link stayed out'
    $tree = Run-Cli $newCli $newDb @('folders', 'tree')
    Check ($tree.Contains('OldFolderA') -and $tree.Contains('OldFolderB')) 'both folders survived'
    $macroList = Run-Cli $newCli $newDb @('macro', 'list')
    Check ($macroList.Contains('FileMacro')) 'the carried file macro is live to the new engine'
    Check (-not $macroList.Contains('OldMacro')) 'the old in-db macro did not cross (documented gap)'

    Section 'S1: carried settings (byte-identical) and decoys (absent)'
    foreach ($rel in $planted.Keys) {
        $target = Join-Path $newData $rel
        Check ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $planted[$rel])) "carried byte-identical: $rel"
    }
    $prefsRaw = Get-Content -LiteralPath (Join-Path $newData 'ui-preferences.json') -Raw
    $prefs = $prefsRaw | ConvertFrom-Json
    Check ($prefs.version -eq 3) 'ui-preferences: set_version walked 2 then 3 on the carry copy'
    Check ($prefs.appearance.theme -eq 'drill') 'ui-preferences: field renamed to appearance.theme'
    Check ($prefs.appearance.fontScale -eq 1.0) 'ui-preferences: default added'
    $appearanceKeys = @($prefs.appearance.PSObject.Properties.Name)
    Check (-not ($appearanceKeys -contains 'themeName')) 'ui-preferences: old field name gone'
    Check (-not ($appearanceKeys -contains 'legacyFlag')) 'ui-preferences: obsolete field removed'
    Check ($prefs.language -eq 'zh-CN') 'ui-preferences: untouched field kept'
    Check ((-not $prefsRaw.Contains('ancientStepRan')) -and (-not $prefsRaw.Contains('futureStepRan'))) 'out-of-window steps did not run'
    foreach ($absent in @('ai\usage.json', 'ai\credentials.json', 'favicons', 'weird', 'undo-journal.json')) {
        Check (-not (Test-Path -LiteralPath (Join-Path $newData $absent))) "not carried (as designed): $absent"
    }
    Check (Test-Path -LiteralPath (Join-Path $stagingDir 'update-result.json')) 'staging keeps update-result.json'
    Check (Test-Path -LiteralPath (Join-Path $stagingDir 'bridge\bookmarks.lpbackup')) 'staging keeps the exported backup'
}

try {
    Run-Bridge
} catch {
    Abort-Drill "$_" $script:work
}

Complete-Drill 'DRILL 2' $script:work $Keep

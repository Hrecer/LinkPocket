# LinkPocket manual migration drill - 3.2.4 -> the current version (UPDATE.md 7 / batch H).
#
# Exercises the documented manual migration path for the pre-4.0.0 cohort with the real binaries
# (the old side is the untouched LinkPocket_v3.2.4.zip: real old app exe, real old CLI, real
# flat-layout database). No updater ever touches the old install here - that is the point.
#
# Scenarios:
#   S0  the new updater pointed at an as-is 3.2.4 install -> refused by the version gate;
#       the whole old install (including the flat db) stays byte-identical. Without that gate
#       the direct-move path would see "no data directory" and delete the old db with the old tree.
#   S1  the manual flow from UPDATE.md 7 / the release notes:
#         1. export bookmarks with the old version's own CLI
#         2. install the new version fresh (its data\ starts empty)
#         3. import the old export with the new CLI (append into the empty db)
#         4. copy settings by hand (ui-preferences.json / ai\ / fonts\)
#         5. a v8 database dropped into data\ is refused and left untouched;
#            in-db macros do not migrate (documented gap)
#
# Usage:  powershell -ExecutionPolicy Bypass -File build\drill-migrate-manual.ps1
#         powershell -ExecutionPolicy Bypass -File build\drill-migrate-manual.ps1 -Package <dir> -OldZip <path> -Keep
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

function Run-Migration {
    $work = $script:work
    if (-not (Test-Path -LiteralPath $Package)) { throw "the package directory does not exist: $Package (run pack.ps1 first)" }
    $packageSrc = Resolve-Full $Package
    if (-not (Test-Path -LiteralPath $OldZip)) { throw "the old 3.2.4 release zip is missing: $OldZip" }
    $oldZipPath = Resolve-Full $OldZip
    $oldInstall = Join-Path $work 'OldInstall'
    $newInstall = Join-Path $work 'NewInstall'
    $stagingDir = Join-Path $work 'staging'

    Write-Host "LinkPocket manual migration drill (3.2.4 -> current)"
    Write-Host "  package  : $packageSrc"
    Write-Host "  old zip  : $oldZipPath"
    Write-Host "  work dir : $work"

    foreach ($required in @('LinkPocket.exe', 'LinkPocket-Update.exe', 'tools\linkpocket.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageSrc $required))) {
            throw "the package is missing '$required' - run pack.ps1 first: $packageSrc"
        }
    }

    Section 'extract the old 3.2.4 release and give it its data (flat layout, as the old cohort had it)'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($oldZipPath, $oldInstall)
    $oldCli = Join-Path $oldInstall 'tools\linkpocket.exe'
    $oldDb = Join-Path $oldInstall 'linkpocket.db'   # flat: the old cohort stored the database next to the exe
    Check ((Get-FileVersionString (Join-Path $oldInstall 'LinkPocket.exe')) -eq '3.2.4') 'the extracted old app is version 3.2.4'
    Check (-not (Test-Path -LiteralPath (Join-Path $oldInstall 'data'))) 'the old release ships without data\ (old flat layout)'

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
    Write-Utf8 (Join-Path $oldInstall 'ui-preferences.json') '{"version":1,"drill":"old-flat"}'
    Write-Utf8 (Join-Path $oldInstall 'ai\preferences.json') '{"version":1,"drill":"old-flat"}'
    $random = New-Object System.Random 424242
    $fontBytes = New-Object byte[] 256
    $random.NextBytes($fontBytes)
    [IO.File]::WriteAllBytes((New-Item -ItemType File -Path (Join-Path $oldInstall 'fonts\drill-font.ttf') -Force).FullName, $fontBytes)
    CheckOk 'old install seeded: flat db (2 folders / 3 active links / 1 trashed / 1 in-db macro) + flat settings files'

    # ---------------- S0: the updater refuses the as-is 3.2.4 install ----------------
    Section 'S0: the new updater refuses to update the 3.2.4 install in place (version gate)'
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageSrc 'LinkPocket-Update.exe') -Destination $stagingDir -Force
    $oldBaseline = Get-TreeHashes $oldInstall
    $run = Invoke-DrillUpdater -Staging $stagingDir -PackageDir $packageSrc -InstallDir $oldInstall
    $err = ''
    if ($run.Result) { $err = "$($run.Result.error)" }
    Check ($run.ExitCode -eq 1) "S0: exit code 1 (got $($run.ExitCode))"
    Check ($err.Contains('3.2.4') -and $err.Contains('cannot be updated in place')) 'S0: error names the version and refuses in-place update'
    Check ($err.Contains('export a backup')) 'S0: error points at the manual migration'
    Compare-Tree $oldBaseline $oldInstall @() 'S0 (the old install, including the flat db)'
    Assert-NoSiblings $work 'S0'

    # ---------------- S1: the manual flow ----------------
    Section 'S1-1: export bookmarks with the old version (its own CLI)'
    $exportPath = Join-Path $work 'old-export.lpbackup'
    $exportJson = Run-Cli $oldCli $oldDb @('backup', 'export', '--output_path', $exportPath)
    $exportData = $exportJson | ConvertFrom-Json
    Check ((Test-Path -LiteralPath $exportPath) -and ((Get-Item -LiteralPath $exportPath).Length -gt 0)) 'the old version wrote the export'
    Check (($exportData.data.total_folders -eq 2) -and ($exportData.data.total_links -eq 3)) "the export holds 2 folders / 3 bookmarks (got $($exportData.data.total_folders) / $($exportData.data.total_links))"

    Section 'S1-2: install the new version fresh (data\ starts empty)'
    Invoke-Robocopy $packageSrc $newInstall @()
    Check (-not (Test-Path -LiteralPath (Join-Path $newInstall 'data'))) 'the fresh install has no data\ yet'

    Section 'S1-3: import the old export with the new CLI'
    $newCli = Join-Path $newInstall 'tools\linkpocket.exe'
    $newDb = Join-Path $newInstall 'data\linkpocket.db'
    $importJson = Run-Cli $newCli $newDb @('backup', 'import', '--file_path', $exportPath, '--yes')
    $importData = $importJson | ConvertFrom-Json
    Check (($importData.data.folders_created -eq 2) -and ($importData.data.links_created -eq 3)) "the import created 2 folders / 3 bookmarks (got $($importData.data.folders_created) / $($importData.data.links_created))"
    $stats = (Run-Cli $newCli $newDb @('links', 'stats')) | ConvertFrom-Json
    Check ($stats.total -eq 3) "links.stats total is 3 (got $($stats.total))"
    Check ($stats.trash -eq 0) 'the trash was not carried (trash count is 0)'
    $query = (Run-Cli $newCli $newDb @('links', 'query')) | ConvertFrom-Json
    $urls = @($query.items | ForEach-Object { $_.url })
    $expectedUrls = @('https://old.example/one', 'https://old.example/two', 'https://old.example/three')
    Check ((($urls | Sort-Object) -join ',') -eq (($expectedUrls | Sort-Object) -join ',')) 'the three active links survived (urls match)'
    Check ($urls -notcontains 'https://old.example/trashed-one') 'the trashed link stayed out'
    $tree = Run-Cli $newCli $newDb @('folders', 'tree')
    Check ($tree.Contains('OldFolderA') -and $tree.Contains('OldFolderB')) 'both folders survived'

    Section 'S1-4: copy settings by hand (UPDATE.md 7 step 3)'
    Copy-Item -LiteralPath (Join-Path $oldInstall 'ui-preferences.json') -Destination (Join-Path $newInstall 'data\ui-preferences.json') -Force
    Copy-Item -LiteralPath (Join-Path $oldInstall 'ai') -Destination (Join-Path $newInstall 'data\ai') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $oldInstall 'fonts') -Destination (Join-Path $newInstall 'data\fonts') -Recurse -Force
    Check (Test-Path -LiteralPath (Join-Path $newInstall 'data\ui-preferences.json')) 'settings file copied by hand'
    Check (Test-Path -LiteralPath (Join-Path $newInstall 'data\ai\preferences.json')) 'ai\ copied by hand'
    Check (Test-Path -LiteralPath (Join-Path $newInstall 'data\fonts\drill-font.ttf')) 'fonts\ copied by hand'

    Section 'S1-5: a v8 database dropped into data\ is refused and left untouched'
    Check (-not (Test-Path -LiteralPath (Join-Path $newInstall 'data\macros.json'))) 'in-db macros did not migrate (documented gap; no macros.json appeared)'
    $macroList = Run-Cli $newCli $newDb @('macro', 'list')
    Check (-not $macroList.Contains('OldMacro')) 'the old in-db macro is not present in the new library'
    $sandbox = Join-Path $work 'RefusalSandbox\data'
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    $droppedDb = Join-Path $sandbox 'linkpocket.db'
    Copy-Item -LiteralPath $oldDb -Destination $droppedDb -Force
    $droppedHash = (Get-FileHash -LiteralPath $droppedDb -Algorithm SHA256).Hash
    $refusal = Invoke-NativeCapture $newCli @('--db', $droppedDb, 'links', 'stats', '--json') $true
    Check ($refusal.Code -ne 0) "the v8 database is refused (exit $($refusal.Code))"
    Check ($refusal.Output.Contains('not compatible')) 'the refusal names the format incompatibility'
    Check ($refusal.Output.Contains('Export a backup')) 'the refusal points at the migration channel'
    Check ((Test-Path -LiteralPath $droppedDb) -and ((Get-FileHash -LiteralPath $droppedDb -Algorithm SHA256).Hash -eq $droppedHash)) 'the dropped v8 file is left untouched'
}

try {
    Run-Migration
} catch {
    Abort-Drill "$_" $script:work
}

Complete-Drill 'DRILL 3' $script:work $Keep

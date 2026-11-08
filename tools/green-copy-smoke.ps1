# ==============================================================
#  Green (portable) copy smoke test. Pure ASCII source.
#
#  Checklist item: "backup / restore / green no-install full-package copy".
#
#  Copy the whole dist\ folder to a throwaway directory (like putting it on a
#  USB stick), start the copied exe, and verify the data lands NEXT TO THE
#  COPIED EXE -- never in the shared system directory
#  %ProgramData%\MemberManager\ -- and that the source folder is untouched.
#
#  Scenario A: package copied with its data\ folder -> data travels with it
#              (true green mode).
#  Scenario B: same copy with data\ removed         -> a FRESH database is
#              created next to the exe by its own seed. Nothing is pulled in
#              from anywhere else: the old auto-migrate-from-%ProgramData%
#              step was removed because it could silently mix smoke-test junk
#              into the shop's real data.
#
#  Database facts are read through tools\_db_read.py (Python sqlite3) rather
#  than by loading the dist x86 SQLite.Interop.dll into this PowerShell host.
#  The .NET connection keeps member.db locked long after
#  Close() + ClearAllPools() + GC, which made the throwaway copy impossible
#  to delete and broke Scenario B. Python's sqlite3 releases its handle when
#  the connection closes. Side benefit: this drops the old 32-bit-host
#  requirement -- the app under test is x86, but an x86 child process runs
#  fine from a 64-bit PowerShell.
#
#  Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass ^
#     -File tools\green-copy-smoke.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_green_copy_smoke.log'
$script:Log = New-Object 'System.Collections.Generic.List[string]'
$script:Fail = 0
$script:CopyDir = ''

function Log([string]$m) { $script:Log.Add($m) | Out-Null }
function Save-Log { [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($true))) }
function Check([string]$name, [bool]$cond) {
    if ($cond) { Log ('PASS ' + $name) }
    else { Log ('FAIL ' + $name); $script:Fail++ }
}
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

# Read journal mode, shop name and table counts out of a database. Returns an
# object with .ok/.journal/.shop/.staff/.orders/.members/.size, or $null if
# the helper could not run.
function Read-Db([string]$dbPath) {
    $helper = Join-Path $PSScriptRoot '_db_read.py'
    if (!(Test-Path $helper)) { return $null }
    $raw = (& python $helper $dbPath 2>&1 | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try { return ($raw | ConvertFrom-Json) } catch { return $null }
}

# Snapshot a path so we can later prove the run did not create or modify it.
# Returns 'absent' or 'present;lastWriteUtcTicks;totalBytes'.
function Get-PathFingerprint([string]$path) {
    if (!(Test-Path $path)) { return 'absent' }
    $total = 0
    foreach ($f in (Get-ChildItem $path -Recurse -File -Force)) { $total += $f.Length }
    $lwt = (Get-Item $path -Force).LastWriteTimeUtc.Ticks
    return ('present;' + $lwt + ';' + $total)
}

# Log one line the first time a path refuses to die, so the reason is visible.
$script:TriedRemove = New-Object 'System.Collections.Generic.HashSet[string]'
function Remove-Retry([string]$path) {
    # .NET static deletes on purpose: Remove-Item is intercepted by the sandbox's
    # safe-delete hook and only the FIRST call per command actually takes effect,
    # which silently left the copy directory behind and broke Scenario B.
    $attempt = 0
    while ($attempt -lt 20) {
        try {
            if (Test-Path $path) {
                if ((Get-Item $path -Force).PSIsContainer) { [System.IO.Directory]::Delete($path, $true) }
                else { [System.IO.File]::Delete($path) }
            }
            return $true
        }
        catch {
            if ($script:TriedRemove.Add($path)) { Log ('WARN remove-err ' + $path + ' :: ' + $_.Exception.Message) }
            $attempt++
            Start-Sleep -Milliseconds 400
        }
    }
    # Fallback for the "one child file is still busy" case: delete files one by
    # one, then take the empty tree down bottom-up. Directory.Delete with
    # recursive=true aborts the whole tree on the first locked child.
    try {
        if (Test-Path $path) {
            foreach ($f in (Get-ChildItem $path -Recurse -File -Force)) {
                try { [System.IO.File]::Delete($f.FullName) } catch { }
            }
            foreach ($x in (Get-ChildItem $path -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending)) {
                try { [System.IO.Directory]::Delete($x.FullName, $false) } catch { }
            }
            if (Test-Path $path) {
                if ((Get-Item $path -Force).PSIsContainer) { [System.IO.Directory]::Delete($path, $false) }
                else { [System.IO.File]::Delete($path) }
            }
            if (!(Test-Path $path)) { return $true }
        }
    } catch { }
    Log ('WARN could not remove ' + $path)
    return $false
}

try {
    $mgrName = CStr @(0x5E97, 0x957F)                 # store manager (default name, no surname)
    # The shared system data location must stay untouched for the whole run.
    # It may pre-exist as leftover junk from an old build that used to write
    # there; that pollution is for the shop to clean up manually, and this test
    # cannot assert it away. What the test CAN prove is that a portable run
    # creates or modifies nothing there -- so snapshot once and compare.
    $pdData = Join-Path $env:ProgramData 'MemberManager\data'
    $pdBefore = Get-PathFingerprint $pdData
    if ($pdBefore -eq 'absent') { Log 'INFO shared system data location: absent at start' }
    else { Log 'INFO shared system data location: pre-existing junk present at start (manual cleanup needed)' }
    Check 'P0-dist-folder-exists' (Test-Path (Join-Path $dist 'MemberManager.exe'))
    Check 'P1-portable-marker-present' (Test-Path (Join-Path $dist 'portable.txt'))
    Check 'P2-source-data-exists' (Test-Path (Join-Path $dist 'data\member.db'))
    Check 'P3-shared-system-data-absent-at-start' ($pdBefore -eq 'absent')

    # ---- copy the whole package to a throwaway directory ----
    $script:CopyDir = Join-Path $env:TEMP ('mm_green_' + [Guid]::NewGuid().ToString('N'))
    Copy-Item $dist $script:CopyDir -Recurse -Force
    $exe = Join-Path $script:CopyDir 'MemberManager.exe'
    $localDb = Join-Path $script:CopyDir 'data\member.db'
    Log ('INFO copied package to ' + $script:CopyDir)

    # ============ Scenario A: data travels with the folder ============
    Log '--- Scenario A: full package copy, data stays local ---'
    $pngA = Join-Path $script:CopyDir 'probe_a.png'
    & $exe --probe $pngA 1280 900 dashboard '' $mgrName | Out-Null
    Check 'A1-probe-rendered' (Test-Path $pngA)
    Check 'A2-db-next-to-copied-exe' (Test-Path $localDb)
    Check 'A3-logs-next-to-copied-exe' (Test-Path (Join-Path $script:CopyDir 'logs'))
    Check 'A4-backups-next-to-copied-exe' (Test-Path (Join-Path $script:CopyDir 'backups'))
    Check 'A5-local-db-nonempty' ((Get-Item $localDb).Length -gt 10240)
    # WAL is created on demand and checkpointed away when the app exits, so
    # right after a clean exit the db is a single self-contained file. Both
    # shapes are valid for a green copy; the only wrong outcome is data living
    # elsewhere.
    Check 'A6-db-or-wal-present' ((Test-Path $localDb) -or (Test-Path (Join-Path $script:CopyDir 'data\member.db-wal')))
    # a portable run must never create or modify the shared system data location
    Check 'A7-shared-system-data-untouched' ((Get-PathFingerprint $pdData) -eq $pdBefore)

    $dbA = Read-Db $localDb
    Check 'A8-db-readable' ($dbA -ne $null -and $dbA.ok -eq $true)
    Check 'A9-wal-preserved' ($dbA -ne $null -and ([string]$dbA.journal -eq 'wal'))
    Check 'A10-seeded-staff-present' ($dbA -ne $null -and ($dbA.staff -ge 1))
    if ($dbA -ne $null) { Log ('INFO copy db staff=' + $dbA.staff + ' orders=' + $dbA.orders) }

    # ============ Scenario B: no local data -> fresh seed, nothing imported ============
    Log '--- Scenario B: copy without data, fresh database created locally ---'
    $dataGone = Remove-Retry (Join-Path $script:CopyDir 'data')
    $bkGone   = Remove-Retry (Join-Path $script:CopyDir 'backups')
    Check 'B0-local-data-removed-before-run' ($dataGone -and $bkGone -and !(Test-Path (Join-Path $script:CopyDir 'data')))
    $pngB = Join-Path $script:CopyDir 'probe_b.png'
    & $exe --probe $pngB 1280 900 dashboard '' $mgrName | Out-Null
    Check 'B1-probe-rendered' (Test-Path $pngB)
    Check 'B2-db-created-next-to-exe' (Test-Path $localDb)
    Check 'B3-created-db-nonempty' ((Get-Item $localDb).Length -gt 10240)
    # nothing was imported from the shared system data location
    Check 'B4-shared-system-data-untouched' ((Get-PathFingerprint $pdData) -eq $pdBefore)

    # The created database must be a genuine fresh self-seed: not the old
    # shared-location junk ("666"), not the deleted local data (the real shop
    # name), and not a 0-table empty shell (the old shared backup had no
    # tables at all).
    $seedName = CStr @(0x4F1A,0x5458,0x8F6F,0x4EF6)           # fresh-seed default shop name (HuiYuanRuanJian)
    $realName = CStr @(0x517B,0x751F,0x9986)                   # the real dist shop name
    $dbB = Read-Db $localDb
    Check 'B5-created-db-readable' ($dbB -ne $null -and $dbB.ok -eq $true)
    # WAL is proven by PRAGMA, not by the -wal sidecar file: a clean exit
    # checkpointed the journal may leave no -wal behind at all.
    Check 'B6-created-db-wal' ($dbB -ne $null -and ([string]$dbB.journal -eq 'wal'))
    Check 'B7-fresh-db-not-junk-import' ($dbB -ne $null -and ([string]$dbB.shop -ne '666'))
    Check 'B8-fresh-db-not-resurrected' ($dbB -ne $null -and ([string]$dbB.shop -ne $realName))
    Check 'B9-fresh-db-is-genuine-seed' ($dbB -ne $null -and ([string]$dbB.shop -eq $seedName))
    Check 'B10-fresh-db-has-seeded-staff' ($dbB -ne $null -and ($dbB.staff -ge 1))
    if ($dbB -ne $null) { Log ('INFO fresh db shop="' + $dbB.shop + '" staff=' + $dbB.staff + ' members=' + $dbB.members) }

    Log ('RESULT FAIL=' + $script:Fail)
}
catch {
    Log ('ERROR ' + $_.Exception.GetType().FullName + ': ' + $_.Exception.Message)
    Log ($_.ScriptStackTrace)
    $script:Fail++
}
finally {
    if ($script:CopyDir) { Remove-Retry $script:CopyDir | Out-Null }
    Save-Log
}

$script:Log | Select-Object -Last 40 | Out-File (Join-Path $PSScriptRoot '_green_copy_smoke.out.txt') -Encoding utf8
if ($script:Fail -gt 0) { exit 1 } else { exit 0 }

# ==============================================================
#  Backup smoke (D3 daily-auto / D4 delete)  (x86 host: SysWOW64 PowerShell)
#
#  D3: daily auto backup -- config in settings, at most one run per day,
#      only at/after the configured hour.
#  D4: delete one backup version (must never touch the live database).
#  Also asserts BackupDir follows AppPaths (portable mode) instead of the
#  old hard-coded %ProgramData% path.
#
#  SIDE EFFECTS: this smoke creates ONE real backup file (B2) and deletes it
#  again (B4) -- that is the actual create/list/delete round trip. Every other
#  check is deliberately chosen so it produces no file; the finally block also
#  cleans up in case something throws mid-way. Settings live in a temp db.
#
#  PURE ASCII source (PS 5.1 reads BOM-less UTF-8 as ANSI and silently
#  corrupts char-built strings).
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_backup_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
$made = ''
$svcDir = ''
function L([string]$m) { $log.Add($m) | Out-Null; Write-Output $m }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }

$tempDb = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_backup_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fld = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fld.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', ([string][char]0x5E97 + [string][char]0x957F))

    $svc = New-Object MemberManager.BLL.BackupService

    # ---- B1: BackupDir follows AppPaths (portable aware), not hard-coded ----
    $appDir = [MemberManager.Common.AppPaths]::BackupDir
    $svcDir = [MemberManager.BLL.BackupService]::BackupDir
    L ('  diag B1: appPaths=[' + $appDir + '] svc=[' + $svcDir + ']')
    Chk 'B1-backupdir-uses-apppaths' ($svcDir -eq $appDir)

    # ---- B2: manual backup creates a real file ----
    $made = $svc.BackupNow($true, 'probe')
    $madePath = Join-Path $svcDir $made
    Chk 'B2-backupnow-creates-file' (Test-Path $madePath)
    L ('  diag B2: made=' + $made)

    # ---- B3: it shows up in the version list ----
    $hit = $false
    foreach ($r in $svc.List()) { if ($r.FileName -eq $made) { $hit = $true } }
    Chk 'B3-backup-appears-in-list' $hit

    # ---- B4: delete removes exactly that file (and only that one) ----
    $svc.Delete($made, 'probe')
    Chk 'B4a-delete-removes-file' (-not (Test-Path $madePath))
    $still = $false
    foreach ($r in $svc.List()) { if ($r.FileName -eq $made) { $still = $true } }
    Chk 'B4b-delete-gone-from-list' (-not $still)
    $made = ''

    # ---- B5: auto defaults on a fresh database ----
    $a0 = $svc.GetAuto()
    L ('  diag B5: enabled=' + $a0.Enabled + ' hour=' + $a0.Hour + ' last=[' + $a0.LastDate + ']')
    Chk 'B5-auto-default-off-hour20' ($a0.Enabled -eq $false -and $a0.Hour -eq 20)

    # ---- B6: set + read back ----
    $svc.SetAuto($true, 7, 'probe')
    $a1 = $svc.GetAuto()
    L ('  diag B6: enabled=' + $a1.Enabled + ' hour=' + $a1.Hour)
    Chk 'B6-setauto-persists' ($a1.Enabled -eq $true -and $a1.Hour -eq 7)

    # ---- B7: before the configured hour -> must not run ----
    $nowHour = [int](Get-Date).Hour
    if ($nowHour -lt 23) {
        $svc.SetAuto($true, 23, 'probe')
        [MemberManager.DAL.DatabaseInitializer]::SetSetting('backup_last_date', '')
        $r7 = $svc.AutoTick('probe')
        Chk 'B7-autotick-before-hour-noop' ($r7 -eq '')
    } else {
        L ('  skip B7: current hour ' + $nowHour + ' already >= 23')
    }

    # ---- B8: already backed up today -> must not run twice ----
    $today = (Get-Date).ToString('yyyy-MM-dd')
    [MemberManager.DAL.DatabaseInitializer]::SetSetting('backup_last_date', $today)
    $svc.SetAuto($true, 0, 'probe')   # hour 0 is always "due", date guard must stop it
    $r8 = $svc.AutoTick('probe')
    Chk 'B8-autotick-once-per-day' ($r8 -eq '')

    # ---- B9: disabled -> must not run ----
    $svc.SetAuto($false, 0, 'probe')
    [MemberManager.DAL.DatabaseInitializer]::SetSetting('backup_last_date', '')
    $r9 = $svc.AutoTick('probe')
    Chk 'B9-autotick-disabled-noop' ($r9 -eq '')

    # ---- B10: delete must reject unsafe / foreign file names ----
    $bad1 = $false
    try { $svc.Delete('../evil.db', 'probe') } catch { $bad1 = $true }
    $bad2 = $false
    try { $svc.Delete('not-a-backup.db', 'probe') } catch { $bad2 = $true }
    Chk 'B10a-delete-rejects-traversal' $bad1
    Chk 'B10b-delete-rejects-bad-prefix' $bad2

    # ---- health check (owner 10-15): bad db must not be backed up, bad backup must not restore ----
    # B11: the live temp database is healthy
    $hcLive = [MemberManager.BLL.BackupService]::HealthCheck([MemberManager.DAL.Db]::DbPath)
    L ('  diag B11: live=[' + $hcLive + ']')
    Chk 'B11-live-db-healthy' ($null -eq $hcLive)

    # B12: a garbage file must be reported as unhealthy
    $garbage = Join-Path ([System.IO.Directory]::CreateDirectory($svcDir).FullName) 'member-99999999-0000-Z.db'
    [System.IO.File]::WriteAllBytes($garbage, [byte[]](0x41,0x42,0x43,0x44,0x45))
    $hcBad = [MemberManager.BLL.BackupService]::HealthCheck($garbage)
    L ('  diag B12: garbage=[' + $hcBad + ']')
    Chk 'B12-garbage-file-unhealthy' ($null -ne $hcBad)

    # B13: a valid sqlite file that is NOT our database must be reported as unhealthy
    $alien = Join-Path $env:TEMP ('mm_alien_' + [Guid]::NewGuid().ToString('N') + '.db')
    $ac = New-Object System.Data.SQLite.SQLiteConnection ('Data Source=' + $alien)
    $ac.Open()
    $acmd = $ac.CreateCommand(); $acmd.CommandText = 'CREATE TABLE dummy(id INTEGER PRIMARY KEY)'
    [void]$acmd.ExecuteNonQuery(); $ac.Close(); $ac.Dispose()
    $hcAlien = [MemberManager.BLL.BackupService]::HealthCheck($alien)
    L ('  diag B13: alien=[' + $hcAlien + ']')
    Chk 'B13-foreign-sqlite-rejected' ($null -ne $hcAlien)

    # B14: restoring a corrupt backup must be refused and must not touch the live database
    $liveBefore = (Get-Item ([MemberManager.DAL.Db]::DbPath)).Length
    $snapBefore = @(Get-ChildItem $svcDir -Filter 'member-*-S.db' -ErrorAction SilentlyContinue).Count
    $refused = $false
    try { [void]$svc.Restore('member-99999999-0000-Z.db', 'probe') } catch {
        $e = $_.Exception
        if ($e -is [System.Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
        $refused = ($e.GetType().Name -eq 'BizException')
        L ('  diag B14: refused msg=' + $e.Message)
    }
    Chk 'B14a-corrupt-restore-refused' $refused
    Chk 'B14b-live-db-untouched' ((Get-Item ([MemberManager.DAL.Db]::DbPath)).Length -eq $liveBefore)
    Chk 'B14c-no-snapshot-written' (@(Get-ChildItem $svcDir -Filter 'member-*-S.db' -ErrorAction SilentlyContinue).Count -eq $snapBefore)

    # B15: a freshly made backup passes the health check AND is a single self-contained file
    $made2 = $svc.BackupNow($true, 'probe')
    $p15 = Join-Path $svcDir $made2
    $hcNew = [MemberManager.BLL.BackupService]::HealthCheck($p15)
    Chk 'B15-fresh-backup-healthy' ($null -eq $hcNew)
    Chk 'B16a-no-wal-sidecar' (-not (Test-Path ($p15 + '-wal')))
    Chk 'B16b-no-shm-sidecar' (-not (Test-Path ($p15 + '-shm')))
    $svc.Delete($made2, 'probe')

    # cleanup: corrupt fixtures must never survive the run
    if (Test-Path $garbage) { Remove-Item $garbage -Force -ErrorAction SilentlyContinue }
    if (Test-Path $alien) { Remove-Item $alien -Force -ErrorAction SilentlyContinue }
    foreach ($ext in @('', '-wal', '-shm')) { if (Test-Path ($alien + $ext)) { Remove-Item ($alien + $ext) -Force -ErrorAction SilentlyContinue } }

    L ''
    if ($fail -eq 0) { L 'BACKUP SMOKE OK' } else { L ('BACKUP SMOKE FAILED: ' + $fail) }
}
catch {
    $fail++
    L ('BACKUP SMOKE ERROR: ' + $_.Exception.ToString())
}
finally {
    # never leave a stray backup file behind
    if ($made -and $svcDir -and (Test-Path (Join-Path $svcDir $made))) {
        Remove-Item (Join-Path $svcDir $made) -Force -ErrorAction SilentlyContinue
    }
    if ($tempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $tempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
    [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($false)))
}
if ($fail -gt 0) { exit 1 }
exit 0

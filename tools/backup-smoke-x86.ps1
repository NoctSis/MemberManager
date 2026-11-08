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

# Redirect the whole data root to a throw-away dir BEFORE any AppPaths call.
# C11 exercises rotation, and rotation DELETES files -- pointing it at the real
# beta\backups would destroy real backups, which is irreversible. AppPaths honours
# MEMBERMANAGER_ROOT when set, so everything (backups/logs/data) lands in temp.
$tempRoot = Join-Path $env:TEMP ('mm_backup_root_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$env:MEMBERMANAGER_ROOT = $tempRoot

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

    # ---- B5: auto backup defaults ON (owner 11-08: backups must not rely on the owner remembering) ----
    $a0 = $svc.GetAuto()
    L ('  diag B5: enabled=' + $a0.Enabled + ' hour=' + $a0.Hour + ' last=[' + $a0.LastDate + ']')
    Chk 'B5-auto-default-on-hour20' ($a0.Enabled -eq $true -and $a0.Hour -eq 20)

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
    try { [void]$svc.Restore('member-99999999-0000-Z.db', 'probe', '') } catch {
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

    # ==========================================================
    #  C) Backup encryption (owner 11-08)
    #     Covers: cipher round trip, wrong password, tamper, on-disk
    #     format, BackupNow producing .dbk, restore by password,
    #     disabling encryption, and the single-instance guard being
    #     present in the shell assembly.
    # ==========================================================
    $pwdOk = 'Probe#Pwd2026'
    $encFiles = New-Object 'System.Collections.Generic.List[string]'

    # C1: encrypt/decrypt round trip preserves every byte
    $srcTxt = Join-Path $env:TEMP ('mm_enc_' + [Guid]::NewGuid().ToString('N') + '.bin')
    $encTxt = $srcTxt + '.dbk'
    $decTxt = $srcTxt + '.out'
    $bytes = New-Object 'byte[]' 4096
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [byte](($i * 37) % 251) }
    [System.IO.File]::WriteAllBytes($srcTxt, $bytes)
    [MemberManager.BLL.BackupCrypto]::Encrypt($srcTxt, $encTxt, $pwdOk)
    Chk 'C1a-encrypt-writes-file' (Test-Path $encTxt)
    Chk 'C1b-encrypted-looks-encrypted' ([MemberManager.BLL.BackupCrypto]::IsEncryptedFile($encTxt))
    Chk 'C1c-plain-is-not-encrypted' (-not [MemberManager.BLL.BackupCrypto]::IsEncryptedFile($srcTxt))
    [MemberManager.BLL.BackupCrypto]::Decrypt($encTxt, $decTxt, $pwdOk)
    $back = [System.IO.File]::ReadAllBytes($decTxt)
    $same = ($back.Length -eq $bytes.Length)
    if ($same) { for ($i = 0; $i -lt $bytes.Length; $i++) { if ($back[$i] -ne $bytes[$i]) { $same = $false; break } } }
    Chk 'C1d-roundtrip-bytes-match' $same

    # C2: wrong password must be refused with a BizException
    $wrong = $false
    try { [MemberManager.BLL.BackupCrypto]::Decrypt($encTxt, ($srcTxt + '.bad'), 'WrongPwd999') } catch {
        $e = $_.Exception
        if ($e -is [System.Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
        $wrong = ($e.GetType().Name -eq 'BizException')
        L ('  diag C2: wrong-pwd msg=' + $e.Message)
    }
    Chk 'C2-wrong-password-refused' $wrong
    Chk 'C2b-no-half-output' (-not (Test-Path ($srcTxt + '.bad')))

    # C3: flipping one ciphertext byte must be detected (HMAC)
    $tampered = $srcTxt + '.tamper'
    [System.IO.File]::Copy($encTxt, $tampered, $true)
    $tb = [System.IO.File]::ReadAllBytes($tampered)
    $tb[60] = [byte]($tb[60] -bxor 0xFF)
    [System.IO.File]::WriteAllBytes($tampered, $tb)
    $detected = $false
    try { [MemberManager.BLL.BackupCrypto]::Decrypt($tampered, ($srcTxt + '.tamper.out'), $pwdOk) } catch { $detected = $true }
    Chk 'C3-tamper-detected' $detected

    # C4: password verifier round trip (no plaintext password stored)
    $ver = [MemberManager.BLL.BackupCrypto]::MakeVerifier($pwdOk)
    Chk 'C4a-verifier-accepts-correct' ([MemberManager.BLL.BackupCrypto]::CheckVerifier($pwdOk, $ver))
    Chk 'C4b-verifier-rejects-wrong' (-not [MemberManager.BLL.BackupCrypto]::CheckVerifier('WrongPwd999', $ver))
    Chk 'C4c-verifier-has-no-plaintext' (-not ($ver -like ('*' + $pwdOk + '*')))

    # C5: turning encryption on makes the next backup a .dbk with no plaintext twin
    $svc.SetPassword('', $pwdOk, 'smoke hint', 'probe')
    $st5 = $svc.GetEncryptStatus()
    Chk 'C5a-encrypt-status-on' ($st5.Enabled -eq $true -and $st5.HasPassword -eq $true)
    $encName = $svc.BackupNow($true, 'probe')
    $encPath = Join-Path $svcDir $encName
    $encFiles.Add($encPath)
    L ('  diag C5: made=' + $encName)
    Chk 'C5b-backup-ends-with-dbk' ($encName.EndsWith('.dbk'))
    Chk 'C5c-encrypted-file-on-disk' ([MemberManager.BLL.BackupCrypto]::IsEncryptedFile($encPath))
    $plainTwin = [System.IO.Path]::ChangeExtension($encPath, '.db')
    Chk 'C5d-no-plaintext-left-behind' (-not (Test-Path $plainTwin))

    # C6: it shows up flagged as encrypted and restore needs the password
    $flag = $false
    foreach ($r in $svc.List()) { if ($r.FileName -eq $encName) { $flag = $r.Encrypted } }
    Chk 'C6a-list-marks-encrypted' $flag
    $refNoPwd = $false
    try { [void]$svc.Restore($encName, 'probe', '') } catch { $refNoPwd = $true }
    Chk 'C6b-restore-without-password-refused' $refNoPwd
    $refWrong = $false
    try { [void]$svc.Restore($encName, 'probe', 'WrongPwd999') } catch { $refWrong = $true }
    Chk 'C6c-restore-wrong-password-refused' $refWrong
    $snapName = $svc.Restore($encName, 'probe', $pwdOk)
    $snapPath = Join-Path $svcDir $snapName
    $encFiles.Add($snapPath)
    Chk 'C6d-restore-with-correct-password-ok' ($snapName -like 'member-*-S.db*')
    Chk 'C6e-snapshot-also-encrypted' ([MemberManager.BLL.BackupCrypto]::IsEncryptedFile($snapPath))

    # C7: disable needs the password; afterwards backups are plain again
    $offBad = $false
    try { $svc.DisableEncrypt('WrongPwd999', 'probe') } catch { $offBad = $true }
    Chk 'C7a-disable-rejects-wrong-password' $offBad
    $svc.DisableEncrypt($pwdOk, 'probe')
    Chk 'C7b-disable-turns-flag-off' ($svc.GetEncryptStatus().Enabled -eq $false)
    $plainName = $svc.BackupNow($true, 'probe')
    $plainPath = Join-Path $svcDir $plainName
    $encFiles.Add($plainPath)
    L ('  diag C7: made=' + $plainName)
    Chk 'C7c-new-backup-is-plain' ($plainName.EndsWith('.db') -and -not [MemberManager.BLL.BackupCrypto]::IsEncryptedFile($plainPath))

    # C8: single-instance guard survives compilation (real double launch is
    #     verified manually -- launching a GUI inside the smoke would hang CI)
    $siOk = $false
    try {
        $asShell = [System.Reflection.Assembly]::LoadFrom((Join-Path $dist 'MemberManager.exe'))
        $siType = $asShell.GetType('MemberManager.Shell.SingleInstance')
        $siOk = ($null -ne $siType) -and ($null -ne $siType.GetMethod('TryAcquire')) -and ($null -ne $siType.GetMethod('ActivateExisting'))
    } catch {
        L ('  diag C8: ' + $_.Exception.Message)
    }
    Chk 'C8-single-instance-guard-present' $siOk

    # C9: weak passwords are refused at the source (open source => password is
    #     the only real secret, so all-digit / too-short must not be accepted)
    $weakDigits = $false
    try { [void][MemberManager.BLL.BackupCrypto]::MakeVerifier('88888888') } catch { $weakDigits = $true }
    Chk 'C9a-all-digit-password-rejected' $weakDigits
    $weakShort = $false
    try { [void][MemberManager.BLL.BackupCrypto]::MakeVerifier('Ab1#') } catch { $weakShort = $true }
    Chk 'C9b-short-password-rejected' $weakShort
    $weakCommon = $false
    try { [void][MemberManager.BLL.BackupCrypto]::MakeVerifier('password') } catch { $weakCommon = $true }
    Chk 'C9c-common-password-rejected' $weakCommon

    # C8b: the guard must be machine-wide (Global\), not per-session (Local\).
    #      Two Windows users on one PC can point at the SAME data dir when the
    #      portable build sits on a shared folder or USB stick; with Local\ each
    #      session gets its own mutex and both can write the database at once.
    $mutexName = ''
    try {
        $bm = $siType.GetMethod('BuildName', [System.Reflection.BindingFlags]'NonPublic,Static')
        $mutexName = [string]$bm.Invoke($null, $null)
    } catch {
        L ('  diag C8b: ' + $_.Exception.Message)
    }
    L ('  diag C8b: mutex=[' + $mutexName + ']')
    Chk 'C8b-single-instance-is-global' ($mutexName.StartsWith('Global\'))

    # C10: encryption is ON but the machine-stored password is gone (changed
    #      Windows user / reinstalled OS / DPAPI broken). The backup must be
    #      REFUSED -- a silent plaintext fallback would make "encrypted" a lie.
    #      Drop the C7 plain backup first: backup names only carry minutes, so
    #      C7 and C10 inside the same minute would reuse the same file name and
    #      the count comparison below would measure an overwrite instead.
    if (Test-Path $plainPath) { Remove-Item $plainPath -Force -ErrorAction SilentlyContinue }
    $svc.SetPassword('', $pwdOk, 'hint', 'probe')
    [MemberManager.DAL.DatabaseInitializer]::SetSetting('backup_pwd_sealed', '')
    $before = @(Get-ChildItem $svcDir -File -Filter 'member-*').Count
    $refused = $false
    try { [void]$svc.BackupNow($true, 'probe') }
    catch { $refused = $true; L ('  diag C10: ' + $_.Exception.Message) }
    $after = @(Get-ChildItem $svcDir -File -Filter 'member-*').Count
    Chk 'C10a-refuse-when-no-stored-password' $refused
    Chk 'C10b-no-backup-file-left' ($after -eq $before)
    # restore a sane state (seal the password again, then turn encryption off)
    $svc.SetPassword($pwdOk, $pwdOk, 'hint', 'probe')
    $svc.DisableEncrypt($pwdOk, 'probe')
    Chk 'C10c-state-restored' ($svc.GetEncryptStatus().Enabled -eq $false)

    # ---- C11: rotation keeps only "last 2 days + newest of last month" ----
    #  The date lives in the file name, so build files with dates relative to today:
    #    today / yesterday        -> must survive (2-day window)
    #    100 days ago             -> must go
    #    last day of last month   -> must survive (cross-month fallback)
    #    third-to-last of it      -> must go (only ONE older-month file is kept)
    #  This also covers the month boundary: on the 1st, "yesterday" IS the last day of
    #  last month, and the assertions still hold (that file survives either way).
    $now = Get-Date
    $monthStart = $now.Date.AddDays(1 - $now.Day)
    $lmLast = $monthStart.AddDays(-1)
    $lmPrev = $lmLast.AddDays(-2)
    $nToday = 'member-' + $now.ToString('yyyyMMdd') + '-0300.db'
    $nYest = 'member-' + $now.AddDays(-1).ToString('yyyyMMdd') + '-0300.db'
    $nOld = 'member-' + $now.AddDays(-100).ToString('yyyyMMdd') + '-0300.db'
    $nLmLast = 'member-' + $lmLast.ToString('yyyyMMdd') + '-0300.db'
    $nLmPrev = 'member-' + $lmPrev.ToString('yyyyMMdd') + '-0300.db'
    $rotNames = @($nToday, $nYest, $nOld, $nLmLast, $nLmPrev)
    foreach ($n in $rotNames) {
        $p = Join-Path $svcDir $n
        if (-not (Test-Path $p)) { [IO.File]::WriteAllBytes($p, [byte[]]@(0)) }
    }
    L ('  diag C11: today=' + $nToday + ' yest=' + $nYest + ' old=' + $nOld + ' lmLast=' + $nLmLast + ' lmPrev=' + $nLmPrev)
    [void]$svc.Rotate('probe')
    Chk 'C11a-keep-today' (Test-Path (Join-Path $svcDir $nToday))
    Chk 'C11b-keep-yesterday' (Test-Path (Join-Path $svcDir $nYest))
    Chk 'C11c-drop-100-days-ago' (-not (Test-Path (Join-Path $svcDir $nOld)))
    Chk 'C11d-keep-last-month-newest' (Test-Path (Join-Path $svcDir $nLmLast))
    Chk 'C11e-drop-last-month-older' (-not (Test-Path (Join-Path $svcDir $nLmPrev)))
    foreach ($n in $rotNames) {
        $p = Join-Path $svcDir $n
        if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
    }

    foreach ($f in $encFiles) {
        if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }
    foreach ($f in @($srcTxt, $encTxt, $decTxt, $tampered, ($srcTxt + '.bad'), ($srcTxt + '.tamper.out'))) {
        if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }

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
    if ($tempRoot -and (Test-Path $tempRoot)) {
        Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($false)))
}
if ($fail -gt 0) { exit 1 }
exit 0

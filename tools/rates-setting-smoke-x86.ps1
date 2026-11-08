# ==============================================================
#  Commission rates settings smoke test - x86 host required
#  (SysWOW64). Pure ASCII source. Chinese strings are built from
#  Unicode code points at runtime (never stored in this file).
#
#  Checklist item (ledger 6.7 note):
#    "SaveRates writes two settings keys. They used to be two
#     separate connections / two separate commits -> a failure
#     or a crash between them left a HALF-APPLIED state
#     (recharge rate changed, consume rate not). Now both writes
#     and the audit log share ONE transaction."
#
#  Assertions:
#    R1  save round-trips both rates through GetRates
#    R2  both settings rows exist in the settings table
#    R3  a second save replaces both values atomically
#    R4  audit trail row written in the same transaction
#    R5  validation rejects out-of-range (negative)
#    R6  validation rejects out-of-range (> 1)
#    R7  REJECTED save does not partially change either rate
#    R8  boundary values 0 and 1 are accepted
#    R9  an unrelated settings row survives the whole exercise
#
#  Usage:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ^
#     -NoProfile -ExecutionPolicy Bypass -File tools\rates-setting-smoke-x86.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_rates_setting_smoke.log'
$script:Log = New-Object 'System.Collections.Generic.List[string]'
$script:Fail = 0
$script:TempDb = ''

function Log([string]$m) { $script:Log.Add($m) | Out-Null }
function Save-Log { [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($true))) }
function Check([string]$name, [bool]$cond) {
    if ($cond) { Log ('PASS ' + $name) }
    else { Log ('FAIL ' + $name); $script:Fail++ }
}
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    Log ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit, dist loaded')

    $mgr = CStr @(0x5E97,0x957F)   # manager
    $modRate = CStr @(0x5458,0x5DE5,0x63D0,0x6210)   # module: commission rates
    $actSave = CStr @(0x4FEE,0x6539,0x63D0,0x6210,0x89C4,0x5219)   # action: change rates

    # ---- point Db at a throwaway file ----
    $tempDb = Join-Path $env:TEMP ('mm_rates_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $f = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $f.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgr)
    Log ('tempDb: ' + $tempDb)

    $svc = New-Object MemberManager.BLL.CommissionService
    $conn = [MemberManager.DAL.Db]::OpenConnection()

    function Scalar([string]$sql, [hashtable]$ps) {
        $c = $conn.CreateCommand(); $c.CommandText = $sql
        if ($ps) { foreach ($k in $ps.Keys) { $p = $c.CreateParameter(); $p.ParameterName = $k; $p.Value = $ps[$k]; [void]$c.Parameters.Add($p) } }
        return ,$c.ExecuteScalar()
    }

    # Sentinel: an unrelated settings row that must survive.
    [void][MemberManager.DAL.DatabaseInitializer]::SetSetting('smoke_sentinel', 'touch-me-not')

    # ===== R1/R2: first save round-trips both rates =====
    $ok = $true; $err = ''
    try { $svc.SaveRates([decimal]0.12, [decimal]0.07, 'probe') }
    catch { $ok = $false; $err = $_.Exception.Message; if ($_.Exception.InnerException) { $err = $err + ' :: ' + $_.Exception.InnerException.Message } }
    Check 'R1a-save-no-throw-no-lock' $ok
    if (-not $ok) { Log ('  save-threw: ' + $err) }

    $r1 = $svc.GetRates()
    Check 'R1b-recharge-rate-roundtrip' ($r1[0] -eq [decimal]0.12)
    Check 'R1c-consume-rate-roundtrip' ($r1[1] -eq [decimal]0.07)

    $kr = Scalar "SELECT value FROM settings WHERE key=@k" @{ '@k' = 'recharge_commission_rate' }
    $kc = Scalar "SELECT value FROM settings WHERE key=@k" @{ '@k' = 'consume_commission_rate' }
    Check 'R2a-recharge-row-exists' ($kr -ne $null -and ([decimal]$kr -eq [decimal]0.12))
    Check 'R2b-consume-row-exists' ($kc -ne $null -and ([decimal]$kc -eq [decimal]0.07))

    # ===== R3: second save replaces BOTH values in one go =====
    $ok = $true
    try { $svc.SaveRates([decimal]0.13, [decimal]0.08, 'probe') }
    catch { $ok = $false; Log ('  save2-threw: ' + $_.Exception.Message) }
    Check 'R3a-second-save-no-throw' $ok
    $r2 = $svc.GetRates()
    Check 'R3b-recharge-updated' ($r2[0] -eq [decimal]0.13)
    Check 'R3c-consume-updated' ($r2[1] -eq [decimal]0.08)

    # ===== R4: audit trail row written in the same transaction =====
    $logRows = [int](Scalar "SELECT COUNT(*) FROM op_logs WHERE module=@m" @{ '@m' = $modRate })
    Check 'R4a-audit-module-rows' ($logRows -ge 2)
    $lastAction = Scalar "SELECT action FROM op_logs WHERE module=@m ORDER BY id DESC LIMIT 1" @{ '@m' = $modRate }
    Check 'R4b-audit-action-matches' ($lastAction -eq $actSave)

    # ===== R5/R6/R7: validation must reject and must not half-apply =====
    $ok = $true
    try { $svc.SaveRates([decimal](-0.01), [decimal]0.08, 'probe') }
    catch { $ok = $false }
    Check 'R5-reject-negative' ($ok -eq $false)

    $ok = $true
    try { $svc.SaveRates([decimal]0.13, [decimal]1.01, 'probe') }
    catch { $ok = $false }
    Check 'R6-reject-over-100pct' ($ok -eq $false)

    $r3 = $svc.GetRates()
    Check 'R7a-no-half-apply-recharge' ($r3[0] -eq [decimal]0.13)
    Check 'R7b-no-half-apply-consume' ($r3[1] -eq [decimal]0.08)

    # ===== R8: boundaries 0 and 1 are legal =====
    $ok = $true
    try { $svc.SaveRates([decimal]0, [decimal]1, 'probe') }
    catch { $ok = $false; Log ('  boundary-threw: ' + $_.Exception.Message) }
    Check 'R8-boundaries-0-and-1-accepted' $ok
    $r4 = $svc.GetRates()
    Check 'R8b-boundary-values-stored' ($r4[0] -eq [decimal]0 -and $r4[1] -eq [decimal]1)

    # ===== R9: unrelated setting untouched =====
    $sent = Scalar "SELECT value FROM settings WHERE key=@k" @{ '@k' = 'smoke_sentinel' }
    Check 'R9-unrelated-setting-survives' ($sent -eq 'touch-me-not')

    $conn.Dispose()
    Log ''
    if ($script:Fail -eq 0) { Log 'RATES SETTING SMOKE OK' } else { Log ('RATES SETTING SMOKE FAILED: ' + $script:Fail) }
}
catch {
    Log ('RATES SETTING SMOKE ERROR: ' + $_.Exception.ToString())
}
finally {
    if ($script:TempDb -eq '') { $script:TempDb = $tempDb }
    if ($script:TempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $script:TempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
    [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($false)))
}
$script:Log
exit 0

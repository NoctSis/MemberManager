# ==============================================================
#  Commission SETTLEMENT smoke (T6) - x86 host: SysWOW64 PowerShell
#
#  Owner (10-08): "settling must be per-staff, and it must be
#  reconciled first - a one-shot monthly settle is not acceptable".
#  This script locks the settlement contract:
#
#    R1  StaffSummary splits per staff: recharge/consume business,
#        unsettled count, unsettled amount, settled amount
#    R2  identity: sum(unsettledAmount) over staff == raw row sum
#    R3  SettleByStaff settles ONLY the named staff (no collateral)
#    R4  after settle: that staff unsettled -> 0, settled grows
#    R5  exactly one op_logs batch written; detail parses back
#        (count/amount) and carries the settled commission ids
#    R6  idempotent: settling again returns count=0 and writes NO log
#    R7  actual != expected without reason -> rejected (BizException);
#        with reason -> accepted and diff recorded
#    R8  SettleHistory reads batches back, newest first
#    R9  settle everything -> no staff left unsettled
#    R10 batch: settle two staff at once, amount == sum of both
#
#  THROWAWAY temp db (Db._dbPath redirected via reflection).
#  Dist dir override: set MM_SMOKE_DIST to test a scratch BLL build
#  (beta\ is locked while the app runs).
#  PURE ASCII source. CJK built from code points.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = $env:MM_SMOKE_DIST
if ([string]::IsNullOrEmpty($dist)) { $dist = Join-Path $root 'beta' }
$logPath = Join-Path $PSScriptRoot '_commission_settle_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$script:fail = 0

function L($m) { $log.Add($m) | Out-Null }
function Chk($name, [bool]$ok) {
    if ($ok) { L ('PASS ' + $name) } else { L ('FAIL ' + $name); $script:fail++ }
}
function Save-Log { [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($true))) }
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }
function Near([decimal]$a, [decimal]$b) { [math]::Abs($a - $b) -lt 0.005 }

# ---- CJK literals (code points) ----
$rechargeCn = CStr @(0x5145, 0x503C)            # "recharge" (CJK)
$consumeCn  = CStr @(0x6D88, 0x8D39)            # "consume" (CJK)
$mgrRole    = CStr @(0x5E97, 0x957F)            # "manager" role (CJK)
$modCn      = CStr @(0x5458, 0x5DE5, 0x63D0, 0x6210)   # op_logs module: staff commission
$actCn      = CStr @(0x7ED3, 0x7B97, 0x63D0, 0x6210)   # op_logs action: settle commission
$kCount     = CStr @(0x7B14, 0x6570)            # detail key: entry count
$kAmount    = CStr @(0x5E94, 0x7ED3)            # detail key: payable amount
$kStaff     = CStr @(0x4EBA, 0x6570)            # detail key: staff count
$kMonth     = CStr @(0x6708, 0x4EFD)            # detail key: month
$kDiff      = CStr @(0x5DEE, 0x5F02)            # detail key: diff
$kReason    = CStr @(0x539F, 0x56E0)            # detail key: reason
$reasonCn   = CStr @(0x62B9, 0x96F6)            # sample reason text

try {
    L ('HOST ' + [System.IntPtr]::Size * 8 + '-bit')
    L ('DIST ' + $dist)
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L 'assemblies loaded'

    $tempDb = Join-Path $env:TEMP ('mm_settle_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)
    L ('temp db: ' + $tempDb)

    $comm = New-Object MemberManager.BLL.CommissionService

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S($sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }
    function X($sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteNonQuery() }

    $month = (Get-Date).ToString('yyyy-MM')

    # ---- pick 3 staff and a real order id (commissions.order_id is NOT NULL FK) ----
    $sid = @()
    $c = $conn.CreateCommand(); $c.CommandText = 'SELECT id FROM staff ORDER BY id LIMIT 3'
    $r = $c.ExecuteReader()
    while ($r.Read()) { $sid += $r.GetInt32(0) }
    $r.Close()
    Chk 'S0-three-staff-available' ($sid.Count -eq 3)
    if ($sid.Count -lt 3) { throw 'need at least 3 staff for this smoke' }

    $oid = [int](S 'SELECT MIN(id) FROM orders')
    Chk 'S1-seed-order-available' ($oid -gt 0)
    if ($oid -le 0) { throw 'seed db has no orders' }

    L ('  staff ids = ' + ($sid -join ','))
    L ('  order id  = ' + $oid + '  month = ' + $month)

    # ---- craft: A has 2 unsettled, B has 1 unsettled, C has 1 already settled ----
    function AddRow([int]$staffId, [string]$type, [decimal]$amount, [decimal]$rate, [decimal]$fee, [int]$settled) {
        return X ("INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled)" +
                  " VALUES($oid,$staffId,'$type',$amount,$rate,$fee,'100%',$settled)")
    }
    [void](AddRow $sid[0] $rechargeCn 1000 0.03 30.00 0)
    [void](AddRow $sid[0] $consumeCn  200  0.05 10.00 0)
    [void](AddRow $sid[1] $consumeCn  300  0.05 15.00 0)
    [void](AddRow $sid[2] $consumeCn  100  0.05  5.00 1)

    $idsA = @()
    $c = $conn.CreateCommand()
    $c.CommandText = ('SELECT id FROM commissions WHERE staff_id=' + $sid[0] + ' AND settled=0 ORDER BY id')
    $r = $c.ExecuteReader(); while ($r.Read()) { $idsA += $r.GetInt32(0) }; $r.Close()

    $rawUnsettled = [decimal](S 'SELECT IFNULL(SUM(commission),0) FROM commissions WHERE settled=0')
    L ('  raw unsettled total = ' + $rawUnsettled)
    Chk 'S2-raw-unsettled-55' (Near $rawUnsettled 55.00)

    # ---------- R1: StaffSummary splits per staff ----------
    $sum = $comm.StaffSummary($month)
    Chk 'R1a-summary-has-3-staff' ($sum.Count -eq 3)
    $sA = $sum | Where-Object { $_.StaffId -eq $sid[0] }
    $sB = $sum | Where-Object { $_.StaffId -eq $sid[1] }
    $sC = $sum | Where-Object { $_.StaffId -eq $sid[2] }
    Chk 'R1b-A-unsettled-count-2' ($sA.UnsettledCount -eq 2)
    Chk 'R1c-A-unsettled-amount-40' (Near ([decimal]$sA.UnsettledAmount) 40.00)
    Chk 'R1d-A-recharge-business-1000' (Near ([decimal]$sA.RechargeAmount) 1000.00)
    Chk 'R1e-A-consume-business-200' (Near ([decimal]$sA.ConsumeAmount) 200.00)
    Chk 'R1f-C-already-settled-5' (Near ([decimal]$sC.SettledAmount) 5.00)
    Chk 'R1g-C-no-unsettled' ($sC.UnsettledCount -eq 0)

    # ---------- R2: identity ----------
    $sumUnsettled = [decimal]0
    foreach ($x in $sum) { $sumUnsettled += [decimal]$x.UnsettledAmount }
    Chk 'R2-summary-total-matches-raw' (Near $sumUnsettled $rawUnsettled)

    # ---------- R3 + R4 + R5: settle A only ----------
    $resA = $comm.SettleByStaff($month, ([string]$sid[0]), [decimal]-1, '', 'probe')
    Chk 'R3a-A-settled-count-2' ($resA.Count -eq 2)
    Chk 'R3b-A-settled-amount-40' (Near ([decimal]$resA.Amount) 40.00)
    Chk 'R3c-A-diff-zero' (Near ([decimal]$resA.Diff) 0.00)

    $leftB = [int](S ('SELECT COUNT(*) FROM commissions WHERE settled=0 AND staff_id=' + $sid[1]))
    Chk 'R3d-B-untouched' ($leftB -eq 1)

    $sum2 = $comm.StaffSummary($month)
    $sA2 = $sum2 | Where-Object { $_.StaffId -eq $sid[0] }
    Chk 'R4a-A-unsettled-now-0' ($sA2.UnsettledCount -eq 0)
    Chk 'R4b-A-settled-now-40' (Near ([decimal]$sA2.SettledAmount) 40.00)

    $logWhere = "module='" + $modCn + "' AND action='" + $actCn + "'"
    $logCnt = [int](S ('SELECT COUNT(*) FROM op_logs WHERE ' + $logWhere))
    Chk 'R5a-one-batch-logged' ($logCnt -eq 1)

    $detail = [string](S ('SELECT detail FROM op_logs WHERE ' + $logWhere + ' ORDER BY id LIMIT 1'))
    L ('  batch detail = ' + $detail)
    Chk 'R5b-detail-month'      ($detail.IndexOf($kMonth + '=' + $month) -ge 0)
    Chk 'R5c-detail-staff-1'    ($detail.IndexOf($kStaff + '=1') -ge 0)
    Chk 'R5d-detail-count-2'    ($detail.IndexOf($kCount + '=2') -ge 0)
    Chk 'R5e-detail-amount-40'  ($detail.IndexOf($kAmount + '=40.00') -ge 0)
    Chk 'R5f-detail-ids-present' (($detail.IndexOf(('ids=' + $idsA[0])) -ge 0) -and ($detail.IndexOf((',' + $idsA[1])) -ge 0))

    # ---------- R6: idempotent ----------
    $resA2 = $comm.SettleByStaff($month, ([string]$sid[0]), [decimal]-1, '', 'probe')
    Chk 'R6a-repeat-count-0' ($resA2.Count -eq 0)
    $logCnt2 = [int](S ('SELECT COUNT(*) FROM op_logs WHERE ' + $logWhere))
    Chk 'R6b-no-extra-log' ($logCnt2 -eq $logCnt)

    # ---------- R7: actual != expected ----------
    $threw = $false
    try { [void]$comm.SettleByStaff($month, ([string]$sid[1]), [decimal]10, '', 'probe') }
    catch { $threw = $true }
    Chk 'R7a-diff-without-reason-rejected' $threw

    $stillB = [int](S ('SELECT COUNT(*) FROM commissions WHERE settled=0 AND staff_id=' + $sid[1]))
    Chk 'R7b-B-still-unsettled-after-reject' ($stillB -eq 1)

    $resB = $comm.SettleByStaff($month, ([string]$sid[1]), [decimal]10, $reasonCn, 'probe')
    Chk 'R7c-B-settled-1' ($resB.Count -eq 1)
    Chk 'R7d-B-diff-minus-5' (Near ([decimal]$resB.Diff) -5.00)

    $detB = [string](S ('SELECT detail FROM op_logs WHERE ' + $logWhere + ' ORDER BY id DESC LIMIT 1'))
    L ('  B detail = ' + $detB)
    Chk 'R7e-B-detail-diff' ($detB.IndexOf($kDiff + '=-5.00') -ge 0)
    Chk 'R7f-B-detail-reason' ($detB.IndexOf($kReason + '=' + $reasonCn) -ge 0)

    # ---------- R8: history ----------
    $hist = $comm.SettleHistory(50)
    Chk 'R8a-history-2-batches' ($hist.Count -eq 2)
    Chk 'R8b-history-newest-first' ($hist[0].Count -eq 1)
    Chk 'R8c-history-newest-diff' (Near ([decimal]$hist[0].Diff) -5.00)
    Chk 'R8d-history-oldest-amount' (Near ([decimal]$hist[1].Amount) 40.00)
    Chk 'R8e-history-has-operator' ($hist[0].Operator -eq 'probe')

    # ---------- R10: batch settle two staff at once ----------
    [void](AddRow $sid[0] $consumeCn 400 0.05 20.00 0)
    [void](AddRow $sid[2] $consumeCn 100 0.05  5.00 0)
    $csv = ([string]$sid[0]) + ',' + ([string]$sid[2])
    $resB2 = $comm.SettleByStaff($month, $csv, [decimal]-1, '', 'probe')
    Chk 'R10a-batch-count-2' ($resB2.Count -eq 2)
    Chk 'R10b-batch-amount-25' (Near ([decimal]$resB2.Amount) 25.00)

    # ---------- R9: nothing left unsettled ----------
    $left = [int](S 'SELECT COUNT(*) FROM commissions WHERE settled=0')
    Chk 'R9a-nothing-unsettled' ($left -eq 0)
    $sum9 = $comm.StaffSummary($month)
    $anyLeft = $false
    foreach ($x in $sum9) { if ($x.UnsettledCount -ne 0) { $anyLeft = $true } }
    Chk 'R9b-summary-all-clear' (-not $anyLeft)

    L ''
    if ($script:fail -eq 0) { L 'COMMISSION SETTLE SMOKE OK' } else { L ('RESULT FAIL=' + $script:fail) }
}
catch {
    L ('HARNESS ERROR: ' + $_.Exception.Message)
    $script:fail++
}
Save-Log
Write-Output (Get-Content $logPath -Raw)
if ($script:fail -ne 0) { exit 1 }

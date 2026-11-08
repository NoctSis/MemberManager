# ==============================================================
#  Commission identity smoke test - x86 host required (SysWOW64).
#  Pure ASCII source. Chinese strings are built from Unicode
#  code points at runtime (never stored in this file).
#
#  End-to-end checklist item:
#    "commission detail total == monthly rank total"
#
#  Existing ReconciliationService.VerifyCommissions only aggregates
#  the commissions table twice (mathematically identical), so it
#  cannot fail. This script asserts the two PUBLIC query services
#  agree instead:
#      CommissionService.Details(month, big)  <=>  Ranks(month)
#  They use different SQL (JOIN orders + LIMIT vs GROUP BY staff_id
#  + CASE WHEN), so a divergence would show up here.
#
#  Phase A: read-only against the REAL database (services only,
#           never writes).
#  Phase B: deterministic rows on a THROWAWAY temp db (Db._dbPath
#           redirected via reflection), so results never depend on
#           what the real database happens to contain.
#
#  Usage:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ^
#     -NoProfile -ExecutionPolicy Bypass -File tools\commission-identity-smoke-x86.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_commission_identity_smoke.log'
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
function NearEq([double]$a, [double]$b) { return ([math]::Abs($a - $b) -lt 0.005) }
function SumField([object]$col, [string]$field) {
    $sum = [double]0
    foreach ($x in $col) { $sum += [double]$x.$field }
    return $sum
}
function AddParam([object]$cmd, [string]$pname, [object]$val) {
    $p = $cmd.CreateParameter()
    $p.ParameterName = $pname
    $p.Value = $val
    [void]$cmd.Parameters.Add($p)
}

try {
    # Phase A reads the REAL db without redirecting Db._dbPath, so AppPaths must
    # resolve to dist\ just like the exe does. Left to itself a PowerShell host
    # falls back to the user's %LocalAppData% and reads an empty database
    # (see AppPaths.Resolve). This also pins the smoke to the same db as the exe.
    $env:MEMBERMANAGER_ROOT = Join-Path $root 'beta'
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    Log ('Host: ' + [System.IntPtr]::Size * 8 + '-bit, dist loaded')

    # ---- runtime Chinese strings ----
    $mgrRole     = CStr @(0x5E97,0x957F)          # role: store manager
    $normal      = CStr @(0x6B63,0x5E38)          # status: normal
    $typeCharge  = CStr @(0x5145,0x503C)          # type: recharge
    $typeConsume = CStr @(0x6D88,0x8D39)          # type: consume
    $today       = Get-Date
    $month       = $today.ToString('yyyy-MM')     # rows are stamped in this month
    $day         = $today.ToString('yyyy-MM-dd')
    $otherMonth  = (Get-Date '2020-01-15T10:00:00').ToString('yyyy-MM')

    $svc = New-Object MemberManager.BLL.CommissionService

    # ============ Phase A: read-only on the real database ============
    Log '--- Phase A: identity check on the real database (read-only) ---'
    $months = New-Object 'System.Collections.Generic.List[object]'
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    $mc = $conn.CreateCommand()
    $mc.CommandText = 'SELECT substr(created_at,1,7) AS m, COUNT(*) AS n, IFNULL(SUM(commission),0) AS s FROM commissions GROUP BY substr(created_at,1,7) ORDER BY m'
    $r = $mc.ExecuteReader()
    try {
        while ($r.Read()) {
            $months.Add((New-Object psobject -Property @{ Month = [string]$r['m']; Rows = [int]$r['n']; Total = [double]$r['s'] })) | Out-Null
        }
    } finally { $r.Dispose() }
    $conn.Close()
    # 10-14: owner reset the portable DB to a fresh demo seed (no commission rows by
    # design). A0 used to hard-assert the real dev DB had commissions, which only held
    # while smoke residue was present. Identity is a contract on data shape, not on the
    # dev DB being dirty -> informational only; Phase B covers it deterministically.
    if ($months.Count -eq 0) {
        Log 'INFO real db has no commission rows (clean/reset db); cross-service identity is covered by Phase B instead'
    }

    foreach ($m in $months) {
        $mo = [string]$m.Month
        $dbSum = [double]$m.Total
        $dbCount = [int]$m.Rows
        $det = $svc.Details($mo, 100000)
        $ranks = $svc.Ranks($mo)
        $detSum = SumField $det 'Commission'
        $rkSum = SumField $ranks 'Commission'
        $rkOrders = 0
        foreach ($x in $ranks) { $rkOrders += [int]$x.Orders }
        # NOTE: parenthesize each call - PS parses "f $x 'str' + f(...)" as string concatenation
        $rkAmount = (SumField $ranks 'RechargeAmount') + (SumField $ranks 'ConsumeAmount')
        $detAmount = SumField $det 'Amount'
        Check ('A1-[' + $mo + ']-detail-commission-equals-rank-commission') (NearEq $detSum $dbSum -and NearEq $rkSum $dbSum)
        Check ('A2-[' + $mo + ']-detail-rows-equal-rank-orders') (($det.Count -eq $dbCount) -and ($rkOrders -eq $dbCount))
        Check ('A3-[' + $mo + ']-amount-split-parity') (NearEq $detAmount $rkAmount)
        if (!(NearEq $detAmount $rkAmount)) {
            # Details JOINs orders as well; orphan commission rows survive Ranks but not Details.
            $dconn = [MemberManager.DAL.Db]::OpenConnection()
            $dc1 = $dconn.CreateCommand()
            $dc1.CommandText = 'SELECT COUNT(*) FROM commissions c LEFT JOIN orders o ON o.id=c.order_id WHERE o.id IS NULL AND substr(c.created_at,1,7)=@m'
            AddParam $dc1 '@m' $mo; $orphanOrder = $dc1.ExecuteScalar()
            $dc2 = $dconn.CreateCommand()
            $dc2.CommandText = 'SELECT COUNT(*) FROM commissions c LEFT JOIN staff s ON s.id=c.staff_id WHERE s.id IS NULL AND substr(c.created_at,1,7)=@m'
            AddParam $dc2 '@m' $mo; $orphanStaff = $dc2.ExecuteScalar()
            $dconn.Close()
            Log ('INFO detail-amount=' + $detAmount + ' rank-amount=' + $rkAmount + ' rows-with-missing-order=' + $orphanOrder + ' rows-with-missing-staff=' + $orphanStaff)
        }
    }

    # ============ Phase B: deterministic temp database ============
    Log '--- Phase B: deterministic rows on a throwaway temp db ---'
    $script:TempDb = Join-Path $env:TEMP ('mm_comm_identity_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $script:TempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function Q1([string]$sql) {
        $c = $conn.CreateCommand()
        $c.CommandText = $sql
        return $c.ExecuteScalar()
    }

    # two orders for four existing seeded staff (ids 2,3,4,5), one recharge + one consume each
    $orderNos = @('PROBE-0001', 'PROBE-0002', 'PROBE-0003', 'PROBE-0004')
    $i = 0
    while ($i -lt 4) {
        $oc = $conn.CreateCommand()
        $oc.CommandText = 'INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,receivable,kind,status,created_at) VALUES(@on,@bd,NULL,1,100,100,@kd,@st,@ca)'
        AddParam $oc '@on' $orderNos[$i]; AddParam $oc '@bd' $day; AddParam $oc '@kd' $typeConsume
        AddParam $oc '@st' $normal; AddParam $oc '@ca' $day
        [void]$oc.ExecuteNonQuery()
        $cid = [int](Q1 'SELECT last_insert_rowid()')

        $cc = $conn.CreateCommand()
        $cc.CommandText = 'INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled,created_at) VALUES(@oid,@sid,@tp,@am,@ra,@cm,"100%",0,@ca)'
        AddParam $cc '@oid' $cid
        AddParam $cc '@sid' ($i + 2)               # staff 2, 3, 4, 5
        AddParam $cc '@tp' $(if ($i % 2 -eq 0) { $typeCharge } else { $typeConsume })
        AddParam $cc '@am' (500 + ($i * 100))      # 500, 600, 700, 800
        AddParam $cc '@ra' 0.1
        AddParam $cc '@cm' (50 + ($i * 10))        # 50, 60, 70, 80 -> total 260
        AddParam $cc '@ca' $day
        [void]$cc.ExecuteNonQuery()
        $i++
    }

    $dbSum = [double](Q1 'SELECT IFNULL(SUM(commission),0) FROM commissions')
    $dbCount = [int](Q1 'SELECT COUNT(*) FROM commissions')
    Check 'B1-expected-row-count' ($dbCount -eq 4)
    Check 'B2-expected-commission-total' (NearEq $dbSum 260)

    $detAll = $svc.Details($month, 100000)
    $rkAll = $svc.Ranks($month)
    $detSum = SumField $detAll 'Commission'
    $rkSum = SumField $rkAll 'Commission'
    $rkOrders = 0
    foreach ($x in $rkAll) { $rkOrders += [int]$x.Orders }
    Check 'B3-details-total-equals-db-total' (NearEq $detSum $dbSum)
    Check 'B4-ranks-total-equals-db-total' (NearEq $rkSum $dbSum)
    Check 'B5-details-equals-ranks' (NearEq $detSum $rkSum)
    Check 'B6-rank-rows-equal-distinct-staff' ($rkAll.Count -eq 4)
    Check 'B7-details-rows-equal-db-rows' ($detAll.Count -eq 4)
    Check 'B8-rank-orders-equal-db-rows' ($rkOrders -eq 4)
    Check 'B9-amount-split-parity' (NearEq (SumField $detAll 'Amount') ((SumField $rkAll 'RechargeAmount') + (SumField $rkAll 'ConsumeAmount')))

    # month filter: a row outside the month must not leak into either service
    $occ = $conn.CreateCommand()
    $occ.CommandText = 'INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled,created_at) VALUES(1,2,@tp,1000,0.2,200,"100%",0,@ca)'
    AddParam $occ '@tp' $typeConsume; AddParam $occ '@ca' '2020-01-15 10:00:00'
    [void]$occ.ExecuteNonQuery()

    $detM = $svc.Details($month, 100000)
    $rkM = $svc.Ranks($month)
    Check 'B10-month-filter-details' ($detM.Count -eq 4 -and (NearEq (SumField $detM 'Commission') $dbSum))
    Check 'B11-month-filter-ranks' ($rkM.Count -eq 4 -and (NearEq (SumField $rkM 'Commission') $dbSum))

    $detOld = $svc.Details($otherMonth, 100000)
    $rkOld = $svc.Ranks($otherMonth)
    Check 'B12-other-month-details-200' ($detOld.Count -eq 1 -and (NearEq (SumField $detOld 'Commission') 200))
    Check 'B13-other-month-ranks-200' ($rkOld.Count -eq 1 -and (NearEq (SumField $rkOld 'Commission') 200))

    # LIMIT is expected to truncate detail rows; identity only holds at full limit
    Check 'B14-details-limit-truncates' ($svc.Details($month, 2).Count -eq 2)
    Check 'B15-ranks-not-affected-by-limit' ($svc.Ranks($month).Count -eq 4)

    # settle-all, then re-assert the identity (must hold both sides)
    [void]$svc.SettleAll($month, 'probe')
    $sc = $conn.CreateCommand()
    $sc.CommandText = 'SELECT COUNT(*) FROM commissions WHERE substr(created_at,1,7)=@m AND settled=1'
    AddParam $sc '@m' $month
    $settled = [int]$sc.ExecuteScalar()
    Check 'B16-settle-all-marks-current-month' ($settled -eq 4)
    $detS = $svc.Details($month, 100000)
    $rkS = $svc.Ranks($month)
    Check 'B17-identity-holds-after-settle' (NearEq (SumField $detS 'Commission') (SumField $rkS 'Commission'))

    Log ('RESULT FAIL=' + $script:Fail)
}
catch {
    Log ('ERROR ' + $_.Exception.GetType().FullName + ': ' + $_.Exception.Message)
    Log ($_.ScriptStackTrace)
    $script:Fail++
}
finally {
    if ($script:TempDb -and (Test-Path $script:TempDb)) {
        try { Remove-Item $script:TempDb -Force -ErrorAction SilentlyContinue } catch { }
    }
    Save-Log
}

$script:Log | Select-Object -Last 40 | Out-File (Join-Path $PSScriptRoot '_commission_identity_smoke.out.txt') -Encoding utf8
if ($script:Fail -gt 0) { exit 1 } else { exit 0 }

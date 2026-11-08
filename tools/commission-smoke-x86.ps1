# ==============================================================
#  Commission accrual smoke (x86 host: SysWOW64 PowerShell)
#
#  Backfill for T0-1 (09-30): recharge accrual was entirely missing,
#  and the personal rate (staff_rate_<id>) was stored but never
#  consumed. After wiring, the rules under test are:
#
#    R1  CreateMember initial recharge accrues one RECHARGE row,
#        principal only (gift excluded), rate = global recharge
#    R2  StoredValueService.Recharge accrues one RECHARGE row,
#        principal only
#    R3  SaveRates changes global recharge rate -> next recharge
#        uses the new rate
#    R4  Consume Checkout accrues one CONSUME row at the item
#        rate when no personal rate is set
#    R5  SaveStaff rate>=0 writes personal rate -> consume uses
#        the personal rate instead of the item rate
#    R6  SaveStaff rate<0 deletes personal rate -> falls back
#    R7  ResolveRate / PersonalRate direct
#    R8  identity: commission = round(amount * rate, 2)
#    R9  Details / Ranks can read back RECHARGE rows
#    R10 SettleAll marks all unsettled rows of the month
#
#  THROWAWAY temp db (Db._dbPath redirected via reflection).
#  PURE ASCII source. CJK built from code points.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_commission_smoke.log'
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
$rechargeCn = CStr @(0x5145, 0x503C)      # recharge
$consumeCn  = CStr @(0x6D88, 0x8D39)      # consume
$mgrRole    = CStr @(0x5E97, 0x957F)      # manager role
$cashCn     = CStr @(0x73B0, 0x91D1)      # cash pay method
$liuFang    = CStr @(0x5218, 0x82B3)      # staff name 4
$jsRole     = CStr @(0x6280, 0x5E08)      # technician role
$xiJinChui  = CStr @(0x6D17, 0x526A, 0x5439)  # seed item (cut/wash/blow)

try {
    L ('HOST ' + [System.IntPtr]::Size * 8 + '-bit')
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L 'dist assemblies loaded'

    $tempDb = Join-Path $env:TEMP ('mm_comm_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)
    L ('temp db: ' + $tempDb)

    $ms   = New-Object MemberManager.BLL.MemberService
    $val  = New-Object MemberManager.BLL.StoredValueService
    $pos  = New-Object MemberManager.BLL.PosService
    $comm = New-Object MemberManager.BLL.CommissionService
    $perm = New-Object MemberManager.BLL.PermissionService

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S($sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }

    # seeded service item (name built from code points, id resolved by name). price 38, item rate 0.05
    $iid = [int](S ("SELECT id FROM service_items WHERE name = '" + $xiJinChui + "'"))
    Chk 'S0-seed-item-found' ($iid -gt 0)
    L ('  diag seed item id=' + $iid + ' price=' + (S ("SELECT price FROM service_items WHERE id=" + $iid)))

    # seed global rates: recharge 0.03 (seeded), consume NOT seeded -> fallback 0.05
    $g0 = $comm.GetRates()
    L ('  diag global rates: recharge=' + $g0[0] + ' consume=' + $g0[1])
    Chk 'S1-seed-global-recharge-3pct' ([decimal]$g0[0] -eq [decimal]'0.03')

    # ---------- R1: CreateMember initial recharge -> one RECHARGE row, principal only ----------
    $m0 = $ms.CreateMember('CommA', '13900000001', '', '', '', [decimal]200, [decimal]50, 1, 'probe')
    $mid = $m0.Id
    $row  = S ("SELECT amount FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    $rate = S ("SELECT rate FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    $cv   = S ("SELECT commission FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    $nR   = [int](S ("SELECT COUNT(*) FROM commissions WHERE type='" + $rechargeCn + "'"))
    L ('  R1 recharge rows=' + $nR + ' amount=' + $row + ' rate=' + $rate + ' commission=' + $cv)
    Chk 'R1-create-recharge-accrues-row' ($nR -ge 1)
    Chk 'R1-recharge-principal-only' ([decimal]$row -eq [decimal]200)
    Chk 'R1-recharge-rate-global-3pct' ([decimal]$rate -eq [decimal]'0.03')
    Chk 'R1-recharge-commission-6' ([decimal]$cv -eq [decimal]6.00)

    # ---------- R2: StoredValueService.Recharge -> principal only ----------
    $before = [int](S ("SELECT COUNT(*) FROM commissions WHERE type='" + $rechargeCn + "'"))
    [void]$val.Recharge($mid, [decimal]100, [decimal]20, $cashCn, 'probe')
    $row2 = S ("SELECT amount FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    $c2   = S ("SELECT commission FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    Chk 'R2-recharge-service-accrues' ([int](S ("SELECT COUNT(*) FROM commissions WHERE type='" + $rechargeCn + "'")) -eq ($before + 1))
    Chk 'R2-recharge-principal-100' ([decimal]$row2 -eq [decimal]100)
    Chk 'R2-recharge-commission-3' ([decimal]$c2 -eq [decimal]3.00)

    # ---------- R3: SaveRates changes global recharge rate ----------
    [void]$comm.SaveRates([decimal]'0.10', [decimal]'0.08', 'probe')
    [void]$val.Recharge($mid, [decimal]50, [decimal]0, $cashCn, 'probe')
    $r3 = S ("SELECT rate FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    $c3 = S ("SELECT commission FROM commissions WHERE type='" + $rechargeCn + "' ORDER BY id DESC LIMIT 1")
    L ('  R3 after SaveRates(10%): rate=' + $r3 + ' commission=' + $c3)
    Chk 'R3-rate-picks-up-10pct' ([decimal]$r3 -eq [decimal]'0.10')
    Chk 'R3-commission-5' ([decimal]$c3 -eq [decimal]5.00)

    # restore global rates for later tests
    [void]$comm.SaveRates([decimal]'0.03', [decimal]'0.05', 'probe')

    # ---------- R4: consume Checkout, no personal rate -> item rate 0.05 ----------
    function NewLine([int]$itemId, [int]$qty, [bool]$byCard) {
        $l = New-Object 'MemberManager.BLL.PosService+CartLine'
        $l.ItemId = $itemId; $l.Qty = $qty; $l.ByCard = $byCard
        return $l
    }
    function LinesOf([object[]]$arr) {
        $list = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
        foreach ($x in $arr) { $list.Add($x) }
        return ,$list
    }
    # staff 4 (technician, enabled) has no personal rate yet -> item rate 0.05
    $svcId4 = 4
    $pos.Checkout((LinesOf @((NewLine $iid 1 $false))), $null, [decimal]0, [decimal]38, $null, $svcId4) | Out-Null
    $rc4 = S ("SELECT rate FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    $cc4 = S ("SELECT commission FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    $ca4 = S ("SELECT amount FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    L ('  R4 consume: amount=' + $ca4 + ' rate=' + $rc4 + ' commission=' + $cc4)
    Chk 'R4-consume-item-rate-5pct' ([decimal]$rc4 -eq [decimal]'0.05')
    Chk 'R4-consume-commission-1.9' ([decimal]$cc4 -eq [decimal]1.90)
    Chk 'R4-consume-amount-is-subtotal' ([decimal]$ca4 -eq [decimal]38)

    # ---------- R5: SaveStaff personal rate 0.20 -> consume uses personal ----------
    [void]$perm.SaveStaff($svcId4, $liuFang, $jsRole, '', [decimal]'0.20', $true, 'probe')
    $pc5 = [MemberManager.BLL.CommissionService]::PersonalRate($conn, $null, $svcId4)
    L ('  R5 PersonalRate(4)=' + $pc5)
    Chk 'R5-personal-rate-stored-0.20' ([decimal]$pc5 -eq [decimal]'0.20')
    $pos.Checkout((LinesOf @((NewLine $iid 1 $false))), $null, [decimal]0, [decimal]38, $null, $svcId4) | Out-Null
    $rc5 = S ("SELECT rate FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    $cc5 = S ("SELECT commission FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    L ('  R5 consume w/ personal: rate=' + $rc5 + ' commission=' + $cc5)
    Chk 'R5-consume-personal-rate-20pct' ([decimal]$rc5 -eq [decimal]'0.20')
    Chk 'R5-consume-commission-7.6' ([decimal]$cc5 -eq [decimal]7.60)

    # ---------- R6: SaveStaff rate<0 deletes personal -> fallback ----------
    [void]$perm.SaveStaff($svcId4, $liuFang, $jsRole, '', [decimal](-1), $true, 'probe')
    $pc6 = [MemberManager.BLL.CommissionService]::PersonalRate($conn, $null, $svcId4)
    Chk 'R6-personal-rate-deleted' ([decimal]$pc6 -eq [decimal](-1))
    $pos.Checkout((LinesOf @((NewLine $iid 1 $false))), $null, [decimal]0, [decimal]38, $null, $svcId4) | Out-Null
    $rc6 = S ("SELECT rate FROM commissions WHERE type='" + $consumeCn + "' ORDER BY id DESC LIMIT 1")
    Chk 'R6-consume-falls-back-to-item' ([decimal]$rc6 -eq [decimal]'0.05')

    # ---------- R7: ResolveRate direct ----------
    $rr7 = [MemberManager.BLL.CommissionService]::ResolveRate($conn, $null, $svcId4, $rechargeCn)
    Chk 'R7-resolve-recharge-no-personal-3pct' ([decimal]$rr7 -eq [decimal]'0.03')

    # ---------- R8: identity commission = round(amount*rate) ----------
    $bad = [int](S "SELECT COUNT(*) FROM commissions WHERE ABS(commission - ROUND(amount*rate,2)) > 0.005")
    L ('  R8 identity violations=' + $bad)
    Chk 'R8-identity-commission-eq-round-amt-x-rate' ($bad -eq 0)

    # ---------- R9: Details / Ranks read back RECHARGE rows ----------
    $month = (Get-Date).ToString('yyyy-MM')
    $det = $comm.Details($month, 200)
    $detRe = @($det | Where-Object { $_.Type -eq $rechargeCn }).Count
    $ranks = $comm.Ranks($month)
    L ('  R9 details total=' + $det.Count + ' rechargeRows=' + $detRe + ' ranks=' + $ranks.Count)
    Chk 'R9-details-have-recharge' ($detRe -ge 2)
    $rankHasRecharge = $false
    foreach ($rk in $ranks) { if ([decimal]$rk.RechargeAmount -gt 0) { $rankHasRecharge = $true } }
    Chk 'R9-ranks-aggregate-recharge' $rankHasRecharge

    # ---------- R10: SettleAll marks all unsettled for month ----------
    $unsettledBefore = [int](S "SELECT COUNT(*) FROM commissions WHERE settled=0")
    [void]$comm.SettleAll($month, 'probe')
    $unsettledAfter = [int](S "SELECT COUNT(*) FROM commissions WHERE settled=0")
    L ('  R10 unsettled before=' + $unsettledBefore + ' after=' + $unsettledAfter)
    Chk 'R10-settle-all-cleared' ($unsettledBefore -gt 0 -and $unsettledAfter -eq 0)

    # ---------- R11: ExportCommissionExcel writes a valid .xlsx with both sheets ----------
    # (T5) returns ExportResult{Path,Name,Size,Period}; file must exist on disk and be
    #  non-empty. The export dir is GET_DIRECTORYOF(tempDb)\exports -- ExcelWriter.Save
    #  creates it, so the redirected temp DB path works.
    $ex = $comm.ExportCommissionExcel($month, 'probe')
    $exOk = ($null -ne $ex) -and ($ex.Path.Length -gt 0) -and ($ex.Name -like '*.xlsx') -and ($ex.Size -gt 0) -and (Test-Path $ex.Path)
    L ('  R11 export path=' + $ex.Path + ' name=' + $ex.Name + ' size=' + $ex.Size + ' exists=' + (Test-Path $ex.Path))
    Chk 'R11-export-returns-result' $exOk
}
catch {
    L ('EXCEPTION: ' + $_.Exception.Message)
    L ('  at ' + $_.ScriptStackTrace)
    $script:fail++
}

if ($conn) { $conn.Close() }
Save-Log
L ('RESULT fail=' + $script:fail)
if ($script:fail -gt 0) {
    foreach ($line in $log) { if ($line -match '^(FAIL|EXCEPTION)') { Write-Host $line } }
    exit 1
} else {
    foreach ($line in $log) { if ($line -match '^PASS') { Write-Host $line } }
    Write-Host 'COMMISSION SMOKE OK'
    exit 0
}

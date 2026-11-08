# ==============================================================
#  M11 card commission modes e2e (owner 10-14: all three, selectable)
#  Locks settings.card_commission_mode = sale / use / split:
#    split(default) sell card -> base payable/2 ; redeem -> unit*qty*rate/2
#    sale           sell card -> base payable   ; redeem -> 0 for card lines
#    use            sell card -> 0              ; redeem -> unit*qty*rate
#    count-mode card lines are per-occurrence: never halved, 0 only in sale
#    legacy cards (paid_amount NULL) -> unit 0, no crash, no commission
#    SaveCardCommissionMode validates + persists + logs
#  x86 host: SysWOW64 PowerShell. PURE ASCII source. CJK from code points.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_mlbatch2_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$script:fail = 0

function L($m) { $log.Add($m) | Out-Null; Write-Output $m }
function Chk($name, [bool]$ok) {
    if ($ok) { L ('PASS ' + $name) } else { L ('FAIL ' + $name); $script:fail++ }
}
function Save-Log { [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($true))) }
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }
function Rejects([scriptblock]$sb) {
    try { & $sb | Out-Null; return $false } catch {
        $e = $_.Exception
        if ($e -is [System.Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
        return ($e.GetType().Name -eq 'BizException')
    }
}

# ---- CJK literals (code points) ----
$tMgr   = CStr @(0x5E97, 0x957F)
$tCash  = CStr @(0x73B0, 0x91D1)
# NOTE: card-sale commission rows carry type=consume (commissions CHECK frozen at
# recharge/consume); the truth lives on orders.kind=sell-card. Assert by order_id.

try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_mlbatch2_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $tMgr)
    L ('temp db: ' + $tempDb)

    $svc  = New-Object MemberManager.BLL.StoredValueService
    $card = New-Object MemberManager.BLL.CardService
    $pos  = New-Object MemberManager.BLL.PosService
    $cs   = New-Object MemberManager.BLL.CommissionService
    $prj  = New-Object MemberManager.BLL.ProjectService
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    function X([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; [void]$c.ExecuteNonQuery() }

    # fixture: card item + its template, normal item, rates
    $cardItemId   = [int](S 'SELECT id FROM service_items WHERE is_card_item=1 AND enabled=1 ORDER BY id LIMIT 1')
    $cardItemName = [string](S ("SELECT name FROM service_items WHERE id=" + $cardItemId))
    $cardItemRate = [decimal](S ("SELECT commission_rate FROM service_items WHERE id=" + $cardItemId))
    $tid          = [int](S ("SELECT id FROM card_templates WHERE item_name='" + $cardItemName + "' AND enabled=1 ORDER BY id LIMIT 1"))
    $normItemId   = [int](S 'SELECT id FROM service_items WHERE card_times=0 AND enabled=1 ORDER BY id LIMIT 1')
    $normItemName = [string](S ("SELECT name FROM service_items WHERE id=" + $normItemId))
    $normPrice    = [decimal](S ("SELECT price FROM service_items WHERE id=" + $normItemId))
    $normRate     = [decimal](S ("SELECT commission_rate FROM service_items WHERE id=" + $normItemId))
    # card-line commission base (owner 10-15) = the ITEM's single-session price,
    # NOT the diluted "paid / total times" average.
    $cardItemPrice = [decimal](S ("SELECT price FROM service_items WHERE id=" + $cardItemId))
    if ($cardItemPrice -le 0) { throw 'fixture: card item has no single-session price' }
    $rechRate     = [decimal](S "SELECT CAST(value AS REAL) FROM settings WHERE key='recharge_commission_rate'")
    $mid1 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1')
    $mid2 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1 OFFSET 1')
    $mid3 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1 OFFSET 2')
    $mid4 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1 OFFSET 3')
    # fixture hygiene: demo-seeded member cards have paid_amount NULL and would be
    # redeemed first (earliest expiry) -> unit 0. Drop them so every redeem in this
    # smoke hits the freshly sold card with a known unit price.
    X 'DELETE FROM card_uses'
    X 'DELETE FROM member_cards'
    $rawPrice = S ("SELECT price FROM service_items WHERE id=" + $cardItemId)
    $cardItemPrice = [decimal]$rawPrice
    if ($cardItemPrice -le 0) { throw 'fixture: card item has no single-session price' }
    L ('  diag: cardItem=' + $cardItemId + ' tpl=' + $tid + ' norm=' + $normItemId + ' rates c/n/r=' + $cardItemRate + '/' + $normRate + '/' + $rechRate)

    # ============ default mode = use (owner 10-15: redeem-time full) ============
    Chk 'D1-default-mode-use' ($cs.GetCardCommissionMode() -eq 'use')
    Chk 'D2-mode-name' ([MemberManager.BLL.CommissionService]::CardCommissionModeName('sale') -eq (CStr @(0x552E,0x5361,0x65F6,0x5168,0x7B97)))
    X "UPDATE settings SET value='nonsense' WHERE key='card_commission_mode'" | Out-Null
    Chk 'D3-dirty-value-falls-back-to-use' ($cs.GetCardCommissionMode() -eq 'use')

    # ============ split mode ============
    [void]$cs.SaveCardCommissionMode('split', 'probe')
    Chk 'D4-switch-to-split' ($cs.GetCardCommissionMode() -eq 'split')
    [void]$card.SellCard($mid1, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $sellOid = [int](S 'SELECT MAX(id) FROM orders')
    $sellBase = [decimal](S ("SELECT IFNULL(SUM(amount),0) FROM commissions WHERE order_id=" + $sellOid))
    $sellComm = [decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $sellOid))
    Chk 'S1-sell-base-half'   ([math]::Abs($sellBase - 150) -lt 0.005)
    Chk 'S2-sell-comm-half'   ([math]::Abs($sellComm - [math]::Round(150 * $rechRate, 2)) -lt 0.005)
    # redeem 2 card times + 1 normal line
    $lines = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $c1 = New-Object MemberManager.BLL.PosService+CartLine
    $c1.ItemId = $cardItemId; $c1.Qty = 2; $c1.ByCard = $true; $c1.Name = $cardItemName
    $c1.Price = [decimal](S ("SELECT price FROM service_items WHERE id=" + $cardItemId)); $c1.Unit = (CStr @(0x6B21)); $c1.IsCardItem = $true
    $c2 = New-Object MemberManager.BLL.PosService+CartLine
    $c2.ItemId = $normItemId; $c2.Qty = 1; $c2.ByCard = $false; $c2.Name = $normItemName
    $c2.Price = $normPrice; $c2.Unit = (CStr @(0x6B21)); $c2.IsCardItem = $false
    $lines.Add($c1); $lines.Add($c2)
    [void]$pos.Checkout($lines, $mid1, [decimal]0, $normPrice, $null, 1)
    $coOid = [int](S 'SELECT MAX(id) FROM orders')
    $coComm = [decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $coOid))
    # split: card line = single-session price x rate x half;
    #        whole-card allowance = paid x (one session's money / single-session price)
    $cardPer = [math]::Round($cardItemPrice * $cardItemRate * 0.5, 2)
    $cardPool = [math]::Round(300 * $cardPer / $cardItemPrice, 2)
    $cardWant = [math]::Round($cardPer * 2, 2)
    $expCo = [math]::Round($normPrice * $normRate + [math]::Min($cardWant, $cardPool), 2)
    L ('  diag: cardPer=' + $cardPer + ' allowance=' + $cardPool + ' want=' + $cardWant)
    Chk 'S3-redeem-card-half' ([math]::Abs($coComm - $expCo) -lt 0.005)
    L ('  diag: coComm=' + $coComm + ' exp=' + $expCo)

    # ============ use mode ============
    Chk 'U0-reject-bogus-mode' (Rejects { $cs.SaveCardCommissionMode('bogus', 'probe') })
    [void]$cs.SaveCardCommissionMode('use', 'probe')
    Chk 'U1-mode-persisted' ($cs.GetCardCommissionMode() -eq 'use')
    [void]$card.SellCard($mid2, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $sell2 = [int](S 'SELECT MAX(id) FROM orders')
    Chk 'U2-sell-zero-in-use' ([int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $sell2)) -eq 0)
    $lines2 = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $d1 = New-Object MemberManager.BLL.PosService+CartLine
    $d1.ItemId = $cardItemId; $d1.Qty = 2; $d1.ByCard = $true; $d1.Name = $cardItemName
    $d1.Price = [decimal](S ("SELECT price FROM service_items WHERE id=" + $cardItemId)); $d1.Unit = (CStr @(0x6B21)); $d1.IsCardItem = $true
    $lines2.Add($d1)
    [void]$pos.Checkout($lines2, $mid2, [decimal]0, [decimal]0, $null, 1)
    $co2 = [int](S 'SELECT MAX(id) FROM orders')
    $co2Comm = [decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $co2))
    # use: card line takes the FULL "one session's money" = price x rate (capped by allowance)
    $usePer = [math]::Round($cardItemPrice * $cardItemRate, 2)
    $usePool = [math]::Round(300 * $usePer / $cardItemPrice, 2)
    $expUse = [math]::Round([math]::Min([math]::Round($usePer * 2, 2), $usePool), 2)
    Chk 'U3-redeem-full-in-use' ([math]::Abs($co2Comm - $expUse) -lt 0.005)
    L ('  diag: use coComm=' + $co2Comm + ' exp=' + $expUse)

    # ============ sale mode ============
    [void]$cs.SaveCardCommissionMode('sale', 'probe')
    Chk 'A1-mode-persisted' ($cs.GetCardCommissionMode() -eq 'sale')
    [void]$card.SellCard($mid3, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $sell3 = [int](S 'SELECT MAX(id) FROM orders')
    $sell3Base = [decimal](S ("SELECT IFNULL(SUM(amount),0) FROM commissions WHERE order_id=" + $sell3))
    Chk 'A2-sell-full-in-sale' ([math]::Abs($sell3Base - 300) -lt 0.005)
    $lines3 = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $e1 = New-Object MemberManager.BLL.PosService+CartLine
    $e1.ItemId = $cardItemId; $e1.Qty = 2; $e1.ByCard = $true; $e1.Name = $cardItemName
    $e1.Price = [decimal](S ("SELECT price FROM service_items WHERE id=" + $cardItemId)); $e1.Unit = (CStr @(0x6B21)); $e1.IsCardItem = $true
    $e2 = New-Object MemberManager.BLL.PosService+CartLine
    $e2.ItemId = $normItemId; $e2.Qty = 1; $e2.ByCard = $false; $e2.Name = $normItemName
    $e2.Price = $normPrice; $e2.Unit = (CStr @(0x6B21)); $e2.IsCardItem = $false
    $lines3.Add($e1); $lines3.Add($e2)
    [void]$pos.Checkout($lines3, $mid3, [decimal]0, $normPrice, $null, 1)
    $co3 = [int](S 'SELECT MAX(id) FROM orders')
    $co3Comm = [decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $co3))
    $expSale = [math]::Round($normPrice * $normRate, 2)
    Chk 'A3-redeem-zero-card-in-sale' ([math]::Abs($co3Comm - $expSale) -lt 0.005)

    # ============ count-mode card line: per-occurrence, never halved ============
    [void]$cs.SaveCardCommissionMode('split', 'probe')
    [void]$prj.SaveItem(0, 'MLCountCard', (CStr @(0x7F8E, 0x90ED, 0x7C7B, 0x578B)), [decimal]60, (CStr @(0x6B21)), $true, $true, 3, [decimal]0, 'count', [decimal]7)
    $cntItemId = [int](S "SELECT id FROM service_items WHERE name='MLCountCard'")
    [void]$card.SellCard($mid4, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $lines4 = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $f1 = New-Object MemberManager.BLL.PosService+CartLine
    $f1.ItemId = $cntItemId; $f1.Qty = 2; $f1.ByCard = $true; $f1.Name = 'MLCountCard'
    $f1.Price = [decimal]60; $f1.Unit = (CStr @(0x6B21)); $f1.IsCardItem = $true
    $lines4.Add($f1)
    [void]$pos.Checkout($lines4, $mid4, [decimal]0, [decimal]0, $null, 1)
    $co4 = [int](S 'SELECT MAX(id) FROM orders')
    $co4Comm = [decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $co4))
    Chk 'C1-count-line-full-in-split' ([math]::Abs($co4Comm - 14) -lt 0.005)

    # ============ legacy card (paid_amount NULL): unit 0, no crash ============
    X ("UPDATE member_cards SET paid_amount=NULL, purchase_times=NULL WHERE member_id=" + $mid1 + " AND remain>0 ORDER BY id LIMIT 1")
    $lines5 = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $g1 = New-Object MemberManager.BLL.PosService+CartLine
    $g1.ItemId = $cardItemId; $g1.Qty = 1; $g1.ByCard = $true; $g1.Name = $cardItemName
    $g1.Price = [decimal](S ("SELECT price FROM service_items WHERE id=" + $cardItemId)); $g1.Unit = (CStr @(0x6B21)); $g1.IsCardItem = $true
    $lines5.Add($g1)
    $ok5 = $true
    try { [void]$pos.Checkout($lines5, $mid1, [decimal]0, [decimal]0, $null, 1) } catch { $ok5 = $false }
    Chk 'G1-legacy-card-no-crash' $ok5
    if ($ok5) {
        $co5 = [int](S 'SELECT MAX(id) FROM orders')
        # G2 changed (owner 10-15): a legacy card has no stored paid_amount, so it falls
        # back to face value (single-session price x total) -> allowance is ample and the
        # card line earns the FULL single-session commission (no longer 0).
        $lgPer = [math]::Round($cardItemPrice * $cardItemRate * 0.5, 2)
        Chk 'G2-legacy-card-full-single-price' ([math]::Abs([decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $co5)) - $lgPer) -lt 0.005)
        # G3: same legacy card, count-mode line -> per-occurrence fee still paid
        # (service metering does not depend on the card's money record)
        $lines6 = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
        $h1 = New-Object MemberManager.BLL.PosService+CartLine
        $h1.ItemId = $cntItemId; $h1.Qty = 2; $h1.ByCard = $true; $h1.Name = 'MLCountCard'
        $h1.Price = [decimal]60; $h1.Unit = (CStr @(0x6B21)); $h1.IsCardItem = $true
        $lines6.Add($h1)
        [void]$pos.Checkout($lines6, $mid1, [decimal]0, [decimal]0, $null, 1)
        $co6 = [int](S 'SELECT MAX(id) FROM orders')
        Chk 'G3-legacy-count-line-still-paid' ([math]::Abs([decimal](S ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $co6)) - 14) -lt 0.005)
    }

    # ============ perf ledger: card-sale rows settle fine but never hit perf columns ============
    $mo = (Get-Date).ToString('yyyy-MM')
    $sumRows = $cs.StaffSummary($mo)
    $s1 = $sumRows | Where-Object { $_.StaffId -eq 1 }
    # expected = subtotals of consume orders THAT produced a commission row; a
    # zero-commission order (legacy card percent line) writes no row at all and
    # must not appear in the perf column either.
    $consumeExpected = [decimal](S ("SELECT IFNULL(SUM(o.subtotal),0) FROM orders o WHERE o.kind='" + (CStr @(0x6D88, 0x8D39)) + "' AND EXISTS (SELECT 1 FROM commissions c WHERE c.order_id=o.id)"))
    $sellBaseTotal = [decimal](S ("SELECT IFNULL(SUM(amount),0) FROM commissions c JOIN orders o ON o.id=c.order_id WHERE o.kind='" + (CStr @(0x552E, 0x5361)) + "'"))
    Chk 'P1-sell-bases-exist'  ($sellBaseTotal -gt 0)
    if ($s1) {
        Chk 'P2-recharge-perf-zero' ([math]::Abs([decimal]$s1.RechargeAmount) -lt 0.005)
        Chk 'P3-consume-perf-exact' ([math]::Abs([decimal]$s1.ConsumeAmount - $consumeExpected) -lt 0.005)
    } else {
        Chk 'P2-staff1-row-missing' $false
    }
    L ('  diag: sellBaseTotal=' + $sellBaseTotal + ' consumeExpected=' + $consumeExpected + ' sums=' + ($sumRows | ForEach-Object { $_.Name + ':' + $_.RechargeAmount + '/' + $_.ConsumeAmount }) )

    # ============ audit + final state ============
    Chk 'Z1-setting-row'   ([string](S "SELECT value FROM settings WHERE key='card_commission_mode'") -eq 'split')
    $tAct = CStr @(0x6B21, 0x5361, 0x63D0, 0x6210, 0x8BBE, 0x7F6E)
    Chk 'Z2-mode-log'      ([int](S ("SELECT COUNT(*) FROM op_logs WHERE action='" + $tAct + "'")) -ge 3)
    $conn.Close()
}
catch {
    L ('ERROR: ' + $_.Exception.Message)
    if ($_.Exception.InnerException) { L ('  inner: ' + $_.Exception.InnerException.Message) }
    $script:fail++
}
if ($script:fail -eq 0) { L 'SMOKE OK' } else { L ('FAILURES=' + $script:fail); L ('SMOKE FAIL ' + $script:fail) }
Save-Log
if ($script:fail -gt 0) { exit 1 }

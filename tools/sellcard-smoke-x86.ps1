# ==============================================================
#  Sell-card (A2) smoke (x86 host: SysWOW64 PowerShell)
#
#  A2 accounting rules under test:
#    payable    = purchase_times * unit_price - discount
#    card total = purchase_times + gift_times  (gift is free but usable)
#    orders.subtotal = gross, orders.receivable = payable
#    payments.amount = payable
#    UnitPrice fallback: legacy cards with no paid_amount use
#    (template price / TEMPLATE times) -- NOT (/card total), otherwise a
#    gift would silently dilute the per-visit price.
#
#  Runs on a THROWAWAY temp db. PURE ASCII on purpose: PS 5.1 reads
#  BOM-less UTF-8 as ANSI and silently corrupts char-built strings.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_sellcard_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
function L([string]$m) { $log.Add($m) | Out-Null }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }

$tempDb = $null
$conn = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_sellcard_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fld = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fld.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', ([string][char]0x5E97 + [string][char]0x957F))

    $cashCn   = ([string][char]0x73B0 + [string][char]0x91D1)
    $normalCn = ([string][char]0x6B63 + [string][char]0x5E38)

    $svc = New-Object MemberManager.BLL.CardService
    $pos = New-Object MemberManager.BLL.PosService
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }

    $mid = [int](S ("SELECT id FROM members WHERE status='" + $normalCn + "' LIMIT 1"))
    $tid = [int](S "SELECT id FROM card_templates WHERE enabled=1 LIMIT 1")
    $tplTimes = [int](S ("SELECT times FROM card_templates WHERE id=" + $tid))
    $tplPrice = [decimal](S ("SELECT price FROM card_templates WHERE id=" + $tid))
    L ('  diag: memberId=' + $mid + ' tplTimes=' + $tplTimes + ' tplPrice=' + $tplPrice)
    $expectedLegacyUnit = [math]::Round($tplPrice / $tplTimes, 2)
    L ('  diag: expectedLegacyUnit=' + $expectedLegacyUnit)

    # ---- S1: 10 x 30, no gift, no discount ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $sub = [decimal](S 'SELECT subtotal FROM orders ORDER BY id DESC LIMIT 1')
    $rcv = [decimal](S 'SELECT receivable FROM orders ORDER BY id DESC LIMIT 1')
    $pay = [decimal](S 'SELECT amount FROM payments WHERE order_id=(SELECT MAX(id) FROM orders)')
    $cardTotal = [int](S 'SELECT total FROM member_cards ORDER BY id DESC LIMIT 1')
    $purchase = [int](S 'SELECT purchase_times FROM member_cards ORDER BY id DESC LIMIT 1')
    $gift = [int](S 'SELECT gift_times FROM member_cards ORDER BY id DESC LIMIT 1')
    $paid = [decimal](S 'SELECT paid_amount FROM member_cards ORDER BY id DESC LIMIT 1')
    L ('  diag S1: sub=' + $sub + ' rcv=' + $rcv + ' pay=' + $pay + ' total=' + $cardTotal + ' purchase=' + $purchase + ' gift=' + $gift)
    Chk 'S1-gross-300'          ($sub -eq [decimal]300)
    Chk 'S1-receivable-300'     ($rcv -eq [decimal]300)
    Chk 'S1-payment-300'        ($pay -eq [decimal]300)
    Chk 'S1-card-total-10'      ($cardTotal -eq 10)
    Chk 'S1-purchase-10-gift-0' ($purchase -eq 10 -and $gift -eq 0)
    Chk 'S1-paid-amount-300'    ($paid -eq [decimal]300)

    # ---- S2: gift times free but counted in total (8 paid x 30 + 2 gift) ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 8, [decimal]30, 2, [decimal]0, 0)
    $rcv2 = [decimal](S 'SELECT receivable FROM orders ORDER BY id DESC LIMIT 1')
    $total2 = [int](S 'SELECT total FROM member_cards ORDER BY id DESC LIMIT 1')
    $purchase2 = [int](S 'SELECT purchase_times FROM member_cards ORDER BY id DESC LIMIT 1')
    $gift2 = [int](S 'SELECT gift_times FROM member_cards ORDER BY id DESC LIMIT 1')
    $paid2 = [decimal](S 'SELECT paid_amount FROM member_cards ORDER BY id DESC LIMIT 1')
    L ('  diag S2: rcv=' + $rcv2 + ' total=' + $total2 + ' purchase=' + $purchase2 + ' gift=' + $gift2)
    Chk 'S2-receivable-240-only-paid-times' ($rcv2 -eq [decimal]240)
    Chk 'S2-card-total-10-incl-gift'        ($total2 -eq 10)
    Chk 'S2-purchase-8-gift-2'              ($purchase2 -eq 8 -and $gift2 -eq 2)
    Chk 'S2-paid-amount-240'                ($paid2 -eq [decimal]240)
    $card2Id = [int](S 'SELECT MAX(id) FROM member_cards')
    $unit2 = -1
    foreach ($c in $pos.ListMemberCards($mid)) { if ($c.Id -eq $card2Id) { $unit2 = $c.UnitPrice } }
    L ('  diag S2: unitPrice=' + $unit2 + ' (expect 24)')
    Chk 'S2-unitprice-amortised-24' ($unit2 -eq [decimal]24)

    # ---- S3: discount reduces cash, card times unaffected ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 10, [decimal]30, 0, [decimal]50, 0)
    $sub3 = [decimal](S 'SELECT subtotal FROM orders ORDER BY id DESC LIMIT 1')
    $rcv3 = [decimal](S 'SELECT receivable FROM orders ORDER BY id DESC LIMIT 1')
    $pay3 = [decimal](S 'SELECT amount FROM payments WHERE order_id=(SELECT MAX(id) FROM orders)')
    $disc3 = [decimal](S 'SELECT discount_amount FROM member_cards ORDER BY id DESC LIMIT 1')
    $paid3 = [decimal](S 'SELECT paid_amount FROM member_cards ORDER BY id DESC LIMIT 1')
    $total3 = [int](S 'SELECT total FROM member_cards ORDER BY id DESC LIMIT 1')
    L ('  diag S3: sub=' + $sub3 + ' rcv=' + $rcv3 + ' pay=' + $pay3 + ' discount=' + $disc3 + ' total=' + $total3)
    Chk 'S3-gross-still-300'   ($sub3 -eq [decimal]300)
    Chk 'S3-receivable-250'    ($rcv3 -eq [decimal]250)
    Chk 'S3-payment-250'       ($pay3 -eq [decimal]250)
    Chk 'S3-discount-stored'   ($disc3 -eq [decimal]50)
    Chk 'S3-paid-amount-250'   ($paid3 -eq [decimal]250)
    Chk 'S3-total-unchanged-10' ($total3 -eq 10)

    # ---- S4: legacy fallback must divide by TEMPLATE times, not card total ----
    $ins = $conn.CreateCommand()
    # card total deliberately differs from template times (12 vs template's own),
    # so dividing by total vs by template times yields DIFFERENT numbers.
    $ins.CommandText = "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status)" +
        " VALUES(@m,@t,12,12,date('now','+1 year'),@st)"
    foreach ($kv in @{ '@m' = $mid; '@t' = $tid; '@st' = $normalCn }.GetEnumerator()) {
        $pp = $ins.CreateParameter(); $pp.ParameterName = $kv.Key; $pp.Value = $kv.Value; [void]$ins.Parameters.Add($pp)
    }
    [void]$ins.ExecuteNonQuery()
    $legacyId = [int](S 'SELECT MAX(id) FROM member_cards')
    $unitLegacy = -1
    foreach ($c in $pos.ListMemberCards($mid)) { if ($c.Id -eq $legacyId) { $unitLegacy = $c.UnitPrice } }
    L ('  diag S4: legacyUnit=' + $unitLegacy + ' expected(template times)=' + $expectedLegacyUnit +
       ' wrongIfDividedByCardTotal=' + [math]::Round($tplPrice / 12, 2))
    Chk 'S4-legacy-unitprice-uses-template-times' ($unitLegacy -eq $expectedLegacyUnit)

    # ---- S5..S6: server-side guard rails ----
    function Throws([scriptblock]$b) { try { [void](& $b); return $false } catch { return $true } }
    Chk 'S5-reject-discount-gt-gross' (Throws { $svc.SellCard($mid, $tid, $cashCn, 'probe', 10, [decimal]30, 0, [decimal]9999, 0) })
    Chk 'S6-reject-negative-gift'     (Throws { $svc.SellCard($mid, $tid, $cashCn, 'probe', 10, [decimal]30, -1, [decimal]0, 0) })

    # ---- S7: times<=0 must fall back to template times (never a blank card) ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 0, [decimal]30, 0, [decimal]0, 0)
    $fbTotal = [int](S 'SELECT total FROM member_cards ORDER BY id DESC LIMIT 1')
    L ('  diag S7: fallbackTotal=' + $fbTotal + ' tplTimes=' + $tplTimes)
    Chk 'S7-times-zero-falls-back-to-template' ($fbTotal -eq $tplTimes)

    # ---- S8: unit price 0 must NOT hand the card out for free ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 10, [decimal]0, 0, [decimal]0, 0)
    $zRcv = [decimal](S 'SELECT receivable FROM orders ORDER BY id DESC LIMIT 1')
    $zExpect = [decimal](10 * $expectedLegacyUnit)
    L ('  diag S8: zeroUnitRcv=' + $zRcv + ' expected=' + $zExpect)
    Chk 'S8-zero-unitprice-not-free' ($zRcv -eq $zExpect)

    # ---- S9: operator identity -- named operator (not the logged-in account) is recorded ----
    # Shop staff often share one login, so the seller must be pinnable. Pass a staff id
    # different from the signed-in one (id=1) and assert BOTH the order and the audit log.
    $opId = [int](S 'SELECT id FROM staff WHERE id<>1 ORDER BY id LIMIT 1')
    if ($opId -le 0) { $opId = 1 }
    $opName = [string](S ('SELECT name FROM staff WHERE id=' + $opId))
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 5, [decimal]30, 0, [decimal]0, $opId)
    $orderStaff = [int](S 'SELECT staff_id FROM orders ORDER BY id DESC LIMIT 1')
    $logOp     = [string](S 'SELECT operator FROM op_logs ORDER BY id DESC LIMIT 1')
    L ('  diag S9: namedOpId=' + $opId + ' orderStaff=' + $orderStaff + ' logOperator=' + $logOp + ' opName=' + $opName)
    Chk 'S9-order-staff-is-named-operator' ($orderStaff -eq $opId)
    Chk 'S9-oplog-operator-is-named'       ($logOp -eq $opName)

    # ---- S10: operator omitted (0) falls back to the logged-in account (id=1) ----
    [void]$svc.SellCard($mid, $tid, $cashCn, 'probe', 5, [decimal]30, 0, [decimal]0, 0)
    $fbStaff = [int](S 'SELECT staff_id FROM orders ORDER BY id DESC LIMIT 1')
    L ('  diag S10: fallbackStaff=' + $fbStaff + ' (expect 1)')
    Chk 'S10-operator-zero-falls-back-to-login' ($fbStaff -eq 1)

    $conn.Dispose()
    $conn = $null

    L ''
    if ($fail -eq 0) { L 'SELLCARD SMOKE OK' } else { L ('SELLCARD SMOKE FAILED: ' + $fail) }
}
catch {
    L ('SELLCARD SMOKE ERROR: ' + $_.Exception.ToString())
}
finally {
    if ($null -ne $conn) { try { $conn.Dispose() } catch { } }
    if ($tempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $tempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
    [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($false)))
}
exit 0

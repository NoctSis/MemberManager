# ==============================================================
#  M/L batch-1 e2e (owner 10-14: "just fix the medium/low ones")
#  Locks the NEW behaviours of this round on a throwaway temp db:
#    L3  birthday must be a real month/day (02-30 rejected, 02-29 ok)
#    L8  recharge/sellcard payment whitelist (member balance rejected)
#    M3  (owner 10-15) balance overpay is CHANGE: capped at receivable, surplus never debited
#    M1  held order survives a service-item rename (item_id join)
#    M2  cross-business-day held order re-dates to checkout day
#    M4  records summary counts money on status normal only
#    M7  level floor never falls back to a disabled tier
#    L6  recharge export splits normal/refund and shows a net line
#    M9  export pulls the full row set (not capped at 500)
#  x86 host: SysWOW64 PowerShell (SQLite native is 32-bit).
#  PURE ASCII source. CJK built from code points.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_mlbatch1_smoke.log'
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
$tMgr      = CStr @(0x5E97, 0x957F)                  # manager role
$tCash     = CStr @(0x73B0, 0x91D1)                  # cash
$tWechat   = CStr @(0x5FAE, 0x4FE1)                  # wechat
$tAlipay   = CStr @(0x652F, 0x4ED8, 0x5B9D)          # alipay
$tBal      = CStr @(0x4F1A, 0x5458, 0x4F59, 0x989D)  # member balance
$tNorm     = CStr @(0x6B63, 0x5E38)                  # normal status
$tAllSt    = CStr @(0x5168, 0x90E8, 0x72B6, 0x6001)  # all statuses (sentinel)
$tAllKind  = CStr @(0x5168, 0x90E8, 0x7C7B, 0x578B)  # all kinds (sentinel)
$tAllOp    = CStr @(0x5168, 0x90E8, 0x5458, 0x5DE5)  # all staff (sentinel)
$tAllLv    = CStr @(0x5168, 0x90E8, 0x7B49, 0x7EA7)  # all levels (sentinel)
$tAll      = CStr @(0x5168, 0x90E8)                  # all customers (sentinel)
$tConsume  = CStr @(0x6D88, 0x8D39)                  # consume kind
$tHeld     = CStr @(0x6302, 0x5355)                  # held status
$tRecharge = CStr @(0x5145, 0x503C)                  # recharge kind
$tCi       = CStr @(0x6B21)                          # unit: times
$tNet      = CStr @(0x5408, 0x8BA1, 0xFF08, 0x51C0, 0x989D, 0xFF09)          # total (net)
$tPartRfnd = CStr @(0x5176, 0x4E2D, 0xFF1A, 0x9000, 0x6B3E, 0xFF08, 0x7EA2, 0x51B2, 0xFF09)  # of which: refund (red reversal)

try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_mlbatch1_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $tMgr)
    L ('temp db: ' + $tempDb)

    $svc  = New-Object MemberManager.BLL.StoredValueService
    $card = New-Object MemberManager.BLL.CardService
    $pos  = New-Object MemberManager.BLL.PosService
    $mem  = New-Object MemberManager.BLL.MemberService
    $rec  = New-Object MemberManager.BLL.RecordService
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    function X([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteNonQuery() }

    $mid       = [int](S ("SELECT id FROM members WHERE status='" + $tNorm + "' ORDER BY id LIMIT 1"))
    $itemId    = [int](S "SELECT id FROM service_items WHERE card_times=0 AND enabled=1 ORDER BY id LIMIT 1")
    $itemName  = [string](S ("SELECT name FROM service_items WHERE id=" + $itemId))
    $itemPrice = [decimal](S ("SELECT price FROM service_items WHERE id=" + $itemId))
    $tid       = [int](S "SELECT id FROM card_templates WHERE enabled=1 ORDER BY id LIMIT 1")
    L ('  diag: member=' + $mid + ' item=' + $itemId + ' price=' + $itemPrice)

    $lines = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $ln = New-Object MemberManager.BLL.PosService+CartLine
    $ln.ItemId = $itemId; $ln.Qty = 1; $ln.ByCard = $false
    $ln.Name = $itemName; $ln.Price = $itemPrice; $ln.Unit = $tCi; $ln.IsCardItem = $false
    $lines.Add($ln)

    # ============ L3: birthday must be a real month/day ============
    Chk 'L3a-reject-feb-30'   (Rejects { [MemberManager.BLL.MemberService]::NormalizeBirthday('02-30') })
    Chk 'L3b-reject-apr-31'   (Rejects { [MemberManager.BLL.MemberService]::NormalizeBirthday('04-31') })
    Chk 'L3c-reject-month-13' (Rejects { [MemberManager.BLL.MemberService]::NormalizeBirthday('13-01') })
    Chk 'L3d-reject-month-00' (Rejects { [MemberManager.BLL.MemberService]::NormalizeBirthday('00-10') })
    Chk 'L3e-reject-nondigit' (Rejects { [MemberManager.BLL.MemberService]::NormalizeBirthday('1a-01') })
    Chk 'L3f-accept-feb-29'   ([string][MemberManager.BLL.MemberService]::NormalizeBirthday('02-29') -eq '02-29')
    Chk 'L3g-accept-dec-31'   ([string][MemberManager.BLL.MemberService]::NormalizeBirthday('12-31') -eq '12-31')

    # ============ L8: payment method whitelist ============
    $beforeOrders = [int](S 'SELECT COUNT(*) FROM orders')
    Chk 'L8a-recharge-rejects-balance' (Rejects { $svc.Recharge($mid, [decimal]100, [decimal]0, $tBal, 'probe') })
    Chk 'L8b-sellcard-rejects-balance' (Rejects { $card.SellCard($mid, $tid, $tBal, 'probe', 5, [decimal]30, 0, [decimal]0, 0) })
    Chk 'L8c-no-order-written-on-reject' (([int](S 'SELECT COUNT(*) FROM orders')) -eq $beforeOrders)
    [void]$svc.Recharge($mid, [decimal]100, [decimal]0, $tWechat, 'probe')
    Chk 'L8d-recharge-wechat-ok' (([string](S 'SELECT method FROM payments ORDER BY id DESC LIMIT 1')) -eq $tWechat)
    [void]$card.SellCard($mid, $tid, $tAlipay, 'probe', 5, [decimal]30, 0, [decimal]0, 0)
    Chk 'L8e-sellcard-alipay-ok' (([string](S 'SELECT method FROM payments ORDER BY id DESC LIMIT 1')) -eq $tAlipay)

    # ============ M3 (owner 10-15): balance overpay is CHANGE, not rejection ============
    [void]$svc.Recharge($mid, [decimal]500, [decimal]0, $tCash, 'probe')
    $balPre = [decimal](S ("SELECT balance FROM members WHERE id=" + $mid))
    $res0 = $pos.Checkout($lines, $mid, [decimal]150, [decimal]0, $null, 1)
    Chk 'M3a-overpay-not-rejected' ($res0.OrderNo -like 'D*')
    Chk 'M3b-balance-capped-at-receivable' ([math]::Abs([decimal]$res0.BalanceUsed - $itemPrice) -lt 0.005)
    Chk 'M3c-change-returned' ([math]::Abs([decimal]$res0.BalanceBack - (150 - $itemPrice)) -lt 0.005)
    Chk 'M3d-member-debited-once' ([math]::Abs([decimal](S ("SELECT balance FROM members WHERE id=" + $mid)) - ($balPre - $itemPrice)) -lt 0.005)
    $res1 = $pos.Checkout($lines, $mid, $itemPrice, [decimal]0, $null, 1)
    Chk 'M3e-exact-balance-no-change' (($res1.OrderNo -like 'D*') -and ([decimal]$res1.BalanceBack -eq 0))
    $paySum = [decimal](S 'SELECT IFNULL(SUM(amount),0) FROM payments WHERE order_id=(SELECT MAX(id) FROM orders)')
    Chk 'M3f-payments-equal-receivable' ([math]::Abs($paySum - $itemPrice) -lt 0.005)

    # ============ M1: held order survives a rename ============
    $heldNo = $pos.HoldOrder($lines, $mid, 1)
    $heldId = [int](S ("SELECT id FROM orders WHERE order_no='" + $heldNo + "'"))
    X ("UPDATE service_items SET name=name||'X' WHERE id=" + $itemId)
    $taken = $pos.TakeOrder($heldId)
    Chk 'M1a-take-resolves-item-id' ($taken.Lines.Count -eq 1 -and $taken.Lines[0].ItemId -eq $itemId)
    [void]$pos.Checkout($taken.Lines, $mid, [decimal]0, $itemPrice, $heldId, 1)
    Chk 'M1b-checkout-after-rename-ok' (([string](S ("SELECT status FROM orders WHERE id=" + $heldId))) -eq $tNorm)
    Chk 'M1c-item-id-snapshotted' (([int](S ("SELECT item_id FROM order_items WHERE order_id=" + $heldId))) -eq $itemId)

    # ============ M2: cross-business-day held order re-dates ============
    $todayBiz = [string](S ("SELECT biz_date FROM orders WHERE kind='" + $tRecharge + "' ORDER BY id DESC LIMIT 1"))
    $heldNo2 = $pos.HoldOrder($lines, $mid, 1)
    $heldId2 = [int](S ("SELECT id FROM orders WHERE order_no='" + $heldNo2 + "'"))
    X ("UPDATE orders SET biz_date='2026-10-01' WHERE id=" + $heldId2)
    $taken2 = $pos.TakeOrder($heldId2)
    [void]$pos.Checkout($taken2.Lines, $mid, [decimal]0, $itemPrice, $heldId2, 1)
    Chk 'M2-held-redated-to-checkout-day' (([string](S ("SELECT biz_date FROM orders WHERE id=" + $heldId2))) -eq $todayBiz)

    # ============ M4: summary money on status normal only ============
    $q1 = $rec.Query('', '', '', $tAllKind, $tAllSt, $tAllOp, $tAllLv, $tAll, 200, 0)
    $rec1 = [decimal]$q1.Total.Receivable
    $id1 = $q1.Total.Balance + $q1.Total.Cash + $q1.Total.Other
    Chk 'M4a-identity-before' ([math]::Abs($id1 - $rec1) -lt 0.005)
    X ("INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
       " VALUES('H2099-01','" + $todayBiz + "'," + $mid + ",1,999,0,999,'" + $tConsume + "','" + $tHeld + "')")
    $q2 = $rec.Query('', '', '', $tAllKind, $tAllSt, $tAllOp, $tAllLv, $tAll, 200, 0)
    Chk 'M4b-held-excluded-from-money' ([math]::Abs([decimal]$q2.Total.Receivable - $rec1) -lt 0.005)
    $id2 = $q2.Total.Balance + $q2.Total.Cash + $q2.Total.Other
    Chk 'M4c-identity-after' ([math]::Abs($id2 - [decimal]$q2.Total.Receivable) -lt 0.005)
    # 11-05: owner decision - the records LIST keeps showing held orders, but every TOTAL
    # (count / receivable / payment split) counts settled orders only, so "N rows x money"
    # stays consistent with what actually came in. Held orders remain reachable by status.
    Chk 'M4d-held-excluded-from-count' ($q2.Total.Count -eq $q1.Total.Count)
    $q3 = $rec.Query('', '', '', $tAllKind, $tHeld, $tAllOp, $tAllLv, $tAll, 200, 0)
    Chk 'M4e-held-visible-when-status-filtered' ($q3.Total.Count -ge 1)
    X "DELETE FROM orders WHERE order_no='H2099-01'"

    # ============ M7: floor never a disabled tier ============
    $lowName = [string](S 'SELECT name FROM member_levels ORDER BY min_recharge, id LIMIT 1')
    $secondName = [string](S 'SELECT name FROM member_levels WHERE enabled=1 ORDER BY min_recharge, id LIMIT 1 OFFSET 1')
    $newMemRow = $mem.CreateMember('MLProbe', '13800000009', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $newMem = [int]$newMemRow.Id
    X ("UPDATE member_levels SET enabled=0 WHERE name='" + $lowName + "'")
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    Chk 'M7a-floor-skips-disabled' (([string](S ("SELECT level FROM members WHERE id=" + $newMem))) -eq $secondName)
    X ("UPDATE member_levels SET enabled=1 WHERE name='" + $lowName + "'")
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    Chk 'M7b-floor-restored' (([string](S ("SELECT level FROM members WHERE id=" + $newMem))) -eq $lowName)

    # ============ L6/M9: export split + full row set ============
    [void]$svc.Recharge($mid, [decimal]1000, [decimal]200, $tCash, 'probe')
    [void]$svc.Refund($mid, [decimal]1000, [decimal]200, 'mlbatch-probe', 'probe')
    # M9: seed 600 bulk rows so the old 500-row cap would visibly truncate
    for ($i = 0; $i -lt 600; $i++) {
        X ("INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,operator,note)" +
           " VALUES(" + $mid + ",'" + $todayBiz + "','" + $tRecharge + "',1,0,'probe','bulk')")
    }
    $exportDir = Join-Path $env:TEMP ('mm_mlbatch1_exp_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $exportDir -Force | Out-Null
    $exp = $svc.ExportRecords('probe', $exportDir)
    Chk 'M9a-export-ok' (Test-Path $exp.Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $xml = ''
    $zip = [System.IO.Compression.ZipFile]::OpenRead($exp.Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'xl/worksheets/sheet1.xml' }
        $ms = New-Object System.IO.MemoryStream
        $entry.Open().CopyTo($ms)
        $xml = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    } finally { $zip.Dispose() }
    $rowCount = ([regex]::Matches($xml, '<row ')).Count
    L ('  diag: export rows=' + $rowCount)
    Chk 'M9b-export-not-capped' ($rowCount -ge 605)
    Chk 'L6a-net-line'     ($xml.Contains($tNet))
    Chk 'L6b-refund-split' ($xml.Contains($tPartRfnd))

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

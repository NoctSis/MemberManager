# ==============================================================
#  POS (module 03) smoke test - x86 host required (SysWOW64).
#  Pure ASCII source. Chinese strings are built from Unicode
#  code points or fetched at runtime (UiText / DB).
#
#  Phase A: read-only queries against the REAL database.
#  Phase B: full write-path tests against a THROWAWAY temp db
#           (Db._dbPath redirected via reflection) - the real
#           database is never written by this script.
#  Usage:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ^
#     -NoProfile -ExecutionPolicy Bypass -File tools\pos-smoke-x86.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_pos_smoke.log'
$script:Log = New-Object 'System.Collections.Generic.List[string]'
$script:Fail = 0

function Log([string]$m) { $script:Log.Add($m) | Out-Null }
function Save-Log { [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($true))) }
function Check([string]$name, [bool]$cond) {
    if ($cond) { Log ('PASS ' + $name) }
    else { Log ('FAIL ' + $name); $script:Fail++ }
}
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }
function NearEq([double]$a, [double]$b) { return ([math]::Abs($a - $b) -lt 0.005) }

try {
    # ---- load dist assemblies (32-bit host required) ----
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
    $normal   = CStr @(0x6B63,0x5E38)            # status: normal
    $frozen   = CStr @(0x51BB,0x7ED3)            # status: frozen
    $heldSt   = CStr @(0x6302,0x5355)            # status: held order
    $consume  = CStr @(0x6D88,0x8D39)            # flow type: consume
    $payBal   = CStr @(0x4F1A,0x5458,0x4F59,0x989D)   # method: member balance
    $payCashM = CStr @(0x73B0,0x91D1)            # method: cash
    $payCard  = CStr @(0x6B21,0x5361,0x62B5,0x6263)   # method: card deduction
    # member level is auto-derived from cumulative recharge -- not a param anymore
    $mgrRole  = CStr @(0x5E97,0x957F)            # role: manager
    $bizToday = (Get-Date).AddHours(-2).ToString('yyyy-MM-dd')   # mirror Fmt.BizDateOf cut=2

    $svc = New-Object MemberManager.BLL.PosService

    # ================= Phase A: real db, read-only =================
    Log '--- Phase A: read-only on real db ---'
    # NOTE: backend ListCategories() was removed (frontend derives the category list
    # from the loaded items itself), so A1/A3 now use the item list instead.
    $items = $svc.ListItems('')
    Check 'A2-items-nothrow' ($null -ne $items)
    Check 'A1-items-nonempty' ($items.Count -ge 1)
    if ($items.Count -gt 0) {
        $cat0 = $items[0].Category
        $byCat = $svc.ListItems($cat0)
        $ok = ($byCat.Count -gt 0)
        foreach ($it in $byCat) { if ($it.Category -ne $cat0) { $ok = $false } }
        Check 'A3-items-filtered-by-category' $ok
    } else { Check 'A3-items-filtered-by-category' $false }
    $mems = $svc.SearchBillingMembers('', 10)
    Check 'A4-search-members-nothrow' ($null -ne $mems)
    if ($mems.Count -gt 0) {
        $one = $svc.GetBillingMember($mems[0].Id)
        Check 'A5-getmember-roundtrip' ($null -ne $one -and $one.Id -eq $mems[0].Id)
    }
    $kw = $svc.SearchBillingMembers('VIP', 10)
    Check 'A6-search-by-cardno-prefix' ($kw.Count -ge 0)
    $held = $svc.ListHeldOrders()
    Check 'A7-held-list-nothrow' ($null -ne $held)
    $shift = $svc.GetShiftSnapshot($bizToday)
    Check 'A8-shift-nothrow' ($shift.Count -ge 0)

    # ================= Phase B: temp db, full write path =================
    Log '--- Phase B: write tests on throwaway temp db ---'
    $tempDb = Join-Path $env:TEMP ('mm_pos_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)

    $ms = New-Object MemberManager.BLL.MemberService
    $m = $ms.CreateMember('TestUser', '13800000001', '', '', '', [decimal]1000, [decimal]0, 1, 'probe')
    $mid = $m.Id
    Check 'B0-testmember-created' ($mid -gt 0)

    # give the test member a punch card: remain=3 (template = first card_templates row)
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    $icmd = $conn.CreateCommand()
    $icmd.CommandText = "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status) VALUES(@mid,(SELECT MIN(id) FROM card_templates),3,3,date('now','+1 year'),@st)"
    $p1 = $icmd.CreateParameter(); $p1.ParameterName = '@mid'; $p1.Value = $mid; $icmd.Parameters.Add($p1)
    $p2 = $icmd.CreateParameter(); $p2.ParameterName = '@st'; $p2.Value = $normal; $icmd.Parameters.Add($p2)
    [void]$icmd.ExecuteNonQuery()

    function Q1([string]$sql) {
        $c = $conn.CreateCommand()
        $c.CommandText = $sql
        return $c.ExecuteScalar()
    }
    function NewLine([int]$itemId, [int]$qty, [bool]$byCard) {
        $l = New-Object 'MemberManager.BLL.PosService+CartLine'
        $l.ItemId = $itemId; $l.Qty = $qty; $l.ByCard = $byCard
        return $l
    }
    function LinesOf([object[]]$arr) {
        $list = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
        foreach ($x in $arr) { $list.Add($x) }
        return ,$list   # comma prevents pipeline unrolling to object[]
    }

    # seed item ids (autoincrement 1..6, owner 09-22 "six per category"):
    #   1=cut(38,card) 2=perm(268) 3=scalp(128,card) 4=facial(168) 5=neck(198) 6=shampoo(58)
    $ids = @{}
    $c = $conn.CreateCommand(); $c.CommandText = 'SELECT id,price,is_card_item FROM service_items ORDER BY id'
    $r = $c.ExecuteReader(); $i = 0
    while ($r.Read()) { $i++; $ids[$i] = @([int]$r.GetValue(0), [double]$r.GetDouble(1), [int]$r.GetValue(2)) }
    $r.Close()
    Check 'B0b-seed-items-6' ($i -ge 6)

    # ---- checkout A: card line + cash/balance mix ----
    $res = $svc.Checkout((LinesOf @((NewLine $ids[1][0] 1 $true), (NewLine $ids[3][0] 1 $false))), $mid, [decimal]50, [decimal]78, $null, $null)
    Check 'C1-order-no-D-prefix' ($res.OrderNo -like 'D*-*')
    Check 'C2-subtotal-166' (NearEq $res.Subtotal 166)
    Check 'C3-card-covered-38' (NearEq $res.CardCovered 38)
    Check 'C4-receivable-128' (NearEq $res.Receivable 128)
    Check 'C5-balance-used-50' (NearEq $res.BalanceUsed 50)
    Check 'C6-cash-in-78' (NearEq $res.CashIn 78)
    Check 'C7-change-0' (NearEq $res.Change 0)
    $oid = [int](Q1 ("SELECT id FROM orders WHERE order_no='" + $res.OrderNo + "'"))
    Check 'C8-payments-sum-receivable' (NearEq ([double](Q1 "SELECT COALESCE(SUM(amount),0) FROM payments WHERE order_id=$oid")) 128)
    Check 'C9-pay-balance-row' (NearEq ([double](Q1 "SELECT amount FROM payments WHERE order_id=$oid AND method='$payBal'")) 50)
    Check 'C10-pay-cash-row' (NearEq ([double](Q1 "SELECT amount FROM payments WHERE order_id=$oid AND method='$payCashM'")) 78)
    Check 'C11-no-card-payment-row' ([int](Q1 "SELECT COUNT(*) FROM payments WHERE order_id=$oid AND method='$payCard'") -eq 0)
    Check 'C12-member-balance-950' (NearEq ([double](Q1 "SELECT balance FROM members WHERE id=$mid")) 950)
    $fr = $conn.CreateCommand(); $fr.CommandText = "SELECT type,amount,balance_after FROM member_flows WHERE member_id=$mid ORDER BY id DESC LIMIT 1"
    $rr = $fr.ExecuteReader(); [void]$rr.Read()
    Check 'C13-flow-type-consume' ($rr.GetString(0) -eq $consume)
    Check 'C14-flow-amount-minus50' (NearEq $rr.GetDouble(1) -50)
    Check 'C15-flow-after-950' (NearEq $rr.GetDouble(2) 950)
    $rr.Close()
    $cr = $conn.CreateCommand(); $cr.CommandText = "SELECT remain,status FROM member_cards WHERE member_id=$mid"
    $rr = $cr.ExecuteReader(); [void]$rr.Read()
    Check 'C16-card-remain-2' ([int]$rr.GetValue(0) -eq 2)
    Check 'C17-card-still-normal' ($rr.GetString(1) -eq $normal)
    $rr.Close()
    Check 'C18-carduses-1-row' ([int](Q1 "SELECT COUNT(*) FROM card_uses WHERE order_id=$oid") -eq 1)
    Check 'C19-carduses-after-2' ([int](Q1 "SELECT remain_after FROM card_uses WHERE order_id=$oid") -eq 2)
    $comm = [double](Q1 "SELECT commission FROM commissions WHERE order_id=$oid")
    # Card-redemption commission (owner 10-15): one redemption books ONE SESSION'S MONEY,
    # i.e. the item's single-session price at full rate - no longer diluted into
    # "paid / total times". This fixture's card has no paid_amount (legacy) so it falls
    # back to face value, the allowance is ample and nothing is clipped:
    #   cash line 128 x 0.05 = 6.4  +  card line 38 x 0.05 = 1.9  ->  8.3
    # (The intermediate M11 build expected 6.4 because it used "paid/times" as the base;
    #  that is not one session's money, so the owner asked for it to go back to 8.3.)
    Check 'C20-commission-8.3-cash-6.4-plus-card-1.9' (NearEq $comm 8.3)
    Check 'C21-commission-type-consume' ((Q1 "SELECT type FROM commissions WHERE order_id=$oid") -eq $consume)
    Check 'C22-order-status-normal' ((Q1 "SELECT status FROM orders WHERE id=$oid") -eq $normal)

    # ---- checkout B: walk-in, cash only ----
    $resB = $svc.Checkout((LinesOf @((NewLine $ids[2][0] 2 $false))), $null, [decimal]0, [decimal]536, $null, $null)
    $oidB = [int](Q1 ("SELECT id FROM orders WHERE order_no='" + $resB.OrderNo + "'"))
    Check 'D1-walkin-receivable-536' (NearEq $resB.Receivable 536)
    Check 'D2-walkin-member-null' ([string](Q1 "SELECT IFNULL(member_id,'NULL') FROM orders WHERE id=$oidB") -eq 'NULL')
    Check 'D3-walkin-cash-row' (NearEq ([double](Q1 "SELECT amount FROM payments WHERE order_id=$oidB AND method='$payCashM'")) 536)
    Check 'D4-walkin-no-card-row' ([int](Q1 "SELECT COUNT(*) FROM payments WHERE order_id=$oidB AND method='$payBal'") -eq 0)

    # ---- checkout C: big cash, change returned ----
    $resC = $svc.Checkout((LinesOf @((NewLine $ids[4][0] 1 $false))), $mid, [decimal]0, [decimal]400, $null, $null)
    Check 'E1-change-232' (NearEq $resC.Change 232)
    Check 'E2-cash-in-168' (NearEq $resC.CashIn 168)
    $oidC = [int](Q1 ("SELECT id FROM orders WHERE order_no='" + $resC.OrderNo + "'"))
    Check 'E3-payments-sum-168' (NearEq ([double](Q1 "SELECT COALESCE(SUM(amount),0) FROM payments WHERE order_id=$oidC")) 168)

    # ---- hold / take / complete ----
    $holdNo = $svc.HoldOrder((LinesOf @((NewLine $ids[6][0] 1 $false))), $null, $null)
    Check 'F1-hold-no-H-prefix' ($holdNo -like 'H*-*')
    Check 'F2-hold-status' ((Q1 ("SELECT status FROM orders WHERE order_no='" + $holdNo + "'")) -eq $heldSt)
    Check 'F3-hold-no-payments' ([int](Q1 "SELECT COUNT(*) FROM payments WHERE order_id=(SELECT id FROM orders WHERE order_no='$holdNo')") -eq 0)
    $held2 = $svc.ListHeldOrders()
    Check 'F4-held-listed-1' ($held2.Count -eq 1)
    Check 'F5-held-walkin-name' ($held2[0].MemberName.Length -gt 0)
    $ho = $svc.TakeOrder($held2[0].Id)
    Check 'F6-take-lines-1' ($ho.Lines.Count -eq 1)
    Check 'F7-take-item-mapped' ($ho.Lines[0].ItemId -eq $ids[6][0])
    # 10-04 owner report: checkout after take used the wrong operator -> TakeOrder must return the holder.
    # Assertion avoids raw DB (Phase B redirects Db._dbPath); cross-check with ListHeldOrders.StaffName.
    Log ('  diag F7: takeStaffId=' + $ho.StaffId + ' takeName=[' + $ho.StaffName + '] heldName=[' + $held2[0].StaffName + ']')
    Check 'F7b-take-returns-staffid' ($ho.StaffId -gt 0)
    Check 'F7c-take-staffname-matches-held' ($ho.StaffName.Length -gt 0 -and $ho.StaffName -eq $held2[0].StaffName)
    $resD = $svc.Checkout($ho.Lines, $ho.MemberId, [decimal]0, [decimal]58, $ho.OrderId, $null)
    Check 'F8-held-keep-order-no' ($resD.OrderNo -eq $ho.OrderNo)
    Check 'F9-held-done-status' ((Q1 ("SELECT status FROM orders WHERE order_no='" + $resD.OrderNo + "'")) -eq $normal)
    Check 'F10-held-payments-58' (NearEq ([double](Q1 "SELECT COALESCE(SUM(amount),0) FROM payments WHERE order_id=$($ho.OrderId)")) 58)
    Check 'F11-held-list-empty' ($svc.ListHeldOrders().Count -eq 0)

    # ---- negative tests ----
    function ExpectFail([string]$name, [scriptblock]$blk) {
        try { & $blk; Log ('FAIL ' + $name + ' (no exception)'); $script:Fail++ }
        catch { Log ('PASS ' + $name) }
    }
    ExpectFail 'N1-balance-short-rejected' { $svc.Checkout((LinesOf @((NewLine $ids[4][0] 1 $false))), $mid, [decimal]9999, [decimal]0, $null, $null) | Out-Null }
    ExpectFail 'N2-cash-short-rejected' { $svc.Checkout((LinesOf @((NewLine $ids[2][0] 1 $false))), $null, [decimal]0, [decimal]1, $null, $null) | Out-Null }
    ExpectFail 'N3-walkin-balance-rejected' { $svc.Checkout((LinesOf @((NewLine $ids[2][0] 1 $false))), $null, [decimal]10, [decimal]0, $null, $null) | Out-Null }
    ExpectFail 'N4-walkin-card-rejected' { $svc.Checkout((LinesOf @((NewLine $ids[1][0] 1 $true))), $null, [decimal]0, [decimal]38, $null, $null) | Out-Null }
    ExpectFail 'N5-card-times-short-rejected' { $svc.Checkout((LinesOf @((NewLine $ids[1][0] 4 $true))), $mid, [decimal]0, [decimal]0, $null, $null) | Out-Null }
    ExpectFail 'N6-empty-cart-rejected' { $svc.Checkout((LinesOf @()), $mid, [decimal]0, [decimal]0, $null, $null) | Out-Null }
    ExpectFail 'N7-frozen-member-rejected' {
        $fc = $conn.CreateCommand(); $fc.CommandText = "UPDATE members SET status=@s WHERE id=$mid"
        $fp = $fc.CreateParameter(); $fp.ParameterName = '@s'; $fp.Value = $frozen; $fc.Parameters.Add($fp)
        [void]$fc.ExecuteNonQuery()
        $svc.Checkout((LinesOf @((NewLine $ids[4][0] 1 $false))), $mid, [decimal]0, [decimal]168, $null, $null) | Out-Null
    }
    # restore member status after N7 (update, then verify normal again)
    $rc = $conn.CreateCommand(); $rc.CommandText = "UPDATE members SET status=@s WHERE id=$mid"
    $rp = $rc.CreateParameter(); $rp.ParameterName = '@s'; $rp.Value = $normal; $rc.Parameters.Add($rp)
    [void]$rc.ExecuteNonQuery()
    Check 'N8-member-restored' ((Q1 "SELECT status FROM members WHERE id=$mid") -eq $normal)

    # ---- shift snapshot on temp db ----
    $sh2 = $svc.GetShiftSnapshot($bizToday)
    Check 'G1-shift-revenue-890' (NearEq ([double]$sh2.Revenue) 890)
    Check 'G2-shift-count-4' ($sh2.Count -eq 4)
    Check 'G3-shift-ratio' (NearEq ([double]$sh2.BalanceRatio) ([math]::Round(50.0/890.0, 4)))

    # ---- global reconciliation ----
    # NOTE: ReconciliationService was removed as dead code (never called from the UI).
    # The balance identity itself is still covered by member-smoke's identity-check.

    # ---- M: multi-redeem must NOT write identical rows (owner 10-04: no fully-duplicate business rows) ----
    # Placed after section G: this adds one 0-yuan order, so the shift-summary checks above stay intact.
    $mcId = [int](Q1 "SELECT id FROM member_cards WHERE member_id=$mid AND status='$normal' ORDER BY id LIMIT 1")
    $remB = [int](Q1 "SELECT IFNULL(remain,0) FROM member_cards WHERE id=$mcId")
    Log ('  diag M: card=' + $mcId + ' remainBefore=' + $remB)
    Check 'M1-multi-card-remain-2' ($remB -eq 2)
    $resM = $svc.Checkout((LinesOf @((NewLine $ids[1][0] 2 $true))), $mid, [decimal]0, [decimal]0, $null, $null)
    $moid = [int](Q1 ("SELECT id FROM orders WHERE order_no='" + $resM.OrderNo + "'"))
    $mrow = [int](Q1 "SELECT COUNT(*) FROM card_uses WHERE order_id=$moid")
    $mdist = [int](Q1 "SELECT COUNT(DISTINCT remain_after) FROM card_uses WHERE order_id=$moid")
    Log ('  diag M: rows=' + $mrow + ' distinctRemainAfter=' + $mdist)
    Check 'M2-multi-redeem-rows-2' ($mrow -eq 2)
    Check 'M3-multi-redeem-rows-distinct' ($mrow -eq $mdist)
    Check 'M4-multi-redeem-final-remain-0' ([int](Q1 "SELECT remain FROM member_cards WHERE id=$mcId") -eq 0)
    # table-wide invariant: no duplicate remain_after inside one order (guards against regression)
    Check 'M5-no-duplicate-remain-in-same-order' ([int](Q1 "SELECT COUNT(*) FROM (SELECT order_id,remain_after,COUNT(*) c FROM card_uses WHERE order_id IS NOT NULL GROUP BY order_id,remain_after HAVING c>1)") -eq 0)

    $conn.Close()
    $conn.Dispose()
    Log ('RESULT: ' + $script:Fail + ' failed')
    Save-Log
    if ($script:Fail -gt 0) { exit 1 } else { exit 0 }
}
catch {
    Log ('SMOKE ERROR: ' + $_.Exception.Message)
    Log ($_ | Out-String)
    Save-Log
    exit 1
}
finally {
    # remove throwaway temp db files (keep-no-residue rule)
    if ($tempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $tempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
}

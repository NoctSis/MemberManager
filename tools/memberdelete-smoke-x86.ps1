# ==============================================================
#  S2 member write-off + delete (owner 10-15) - x86 host: SysWOW64 PowerShell
#
#  Owner decisions (locked):
#    * "write-off + delete" (soft delete), NOT physical delete. Records stay queryable.
#    * Phone / card_no are NEVER released - a deleted member still occupies them.
#    * Write-off goes through a signed 'adjust' member_flows row so the identity
#      balance = recharge + gift - consume - refund +/- adjust still holds.
#    * Manager-only, reason mandatory, both steps logged.
#    * A member with a non-zero balance CANNOT be deleted (must write off first).
#    * Deleted members vanish from the list, from billing member search, from
#      recharge/sellcard, from liability, and from birthday/sleeping reminders.
#
#  PURE ASCII source. CJK built from code points. NO BOM.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_memberdelete_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$script:fail = 0

function L($m) { $log.Add($m) | Out-Null; Write-Output $m }
function Chk($name, [bool]$ok) {
    if ($ok) { L ('PASS ' + $name) } else { L ('FAIL ' + $name); $script:fail++ }
}
function Save-Log { [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($true))) }
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }
function BizErr([scriptblock]$sb) {
    try { & $sb | Out-Null; return $null } catch {
        $e = $_.Exception
        if ($e -is [System.Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
        if ($e.GetType().Name -eq 'BizException') { return $e.Message }
        return 'NON-BIZ:' + $e.GetType().Name + ':' + $e.Message
    }
}

# ---- CJK literals (code points) ----
$tMgr    = CStr @(0x5E97, 0x957F)          # manager
$tCash   = CStr @(0x73B0, 0x91D1)          # cash
$tCashier= CStr @(0x6536, 0x94F6)          # cashier
$tAdjust = CStr @(0x8C03, 0x6574)          # flow type: adjust
$tDel    = CStr @(0x5DF2, 0x6CE8, 0x9500)  # member status filter: deleted
    $tAll    = CStr @(0x5168, 0x90E8, 0x72B6, 0x6001)  # member status filter: all
    $tLoss   = CStr @(0x6302, 0x5931)              # member status: reported lost

try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_memberdelete_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $tMgr)
    L ('temp db: ' + $tempDb)

    $svc = New-Object MemberManager.BLL.MemberService
    $pos = New-Object MemberManager.BLL.PosService

    # ---------- fixture: two fresh members, one with a balance ----------
    # NOTE: create the members BEFORE opening the long-lived read connection below.
    $mA = $svc.CreateMember('DelA', '13800003001', '', '', '', [decimal]500, [decimal]0, 1, 'probe')
    $mB = $svc.CreateMember('DelB', '13800003002', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $idA = [int]$mA.Id; $idB = [int]$mB.Id

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    function X([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; [void]$c.ExecuteNonQuery() }
    L ('  diag: idA=' + $idA + ' balance=' + (S ("SELECT balance FROM members WHERE id=" + $idA)))

    # ================= 1. reason is mandatory =================
    $e1 = BizErr { $svc.WriteOffBalance($idA, '   ', 'probe') }
    Chk 'W1-writeoff-needs-reason' ($null -ne $e1)
    $e2 = BizErr { $svc.DeleteMember($idB, '', 'probe') }
    Chk 'W2-delete-needs-reason' ($null -ne $e2)

    # ================= 2. write-off: identity still holds =================
    $before = $svc.GetBalanceCheck($idA)
    $written = $svc.WriteOffBalance($idA, 'old debt from the previous owner', 'probe')
    Chk 'W3-returns-written-off-amount' ([math]::Abs([decimal]$written - 500) -lt 0.005)
    $after = $svc.GetBalanceCheck($idA)
    Chk 'W4a-balance-zero' ([math]::Abs([decimal]$after.Actual) -lt 0.005)
    Chk 'W4b-identity-still-holds' ($after.Ok -eq $true)
    Chk 'W4c-adjust-column-is-negative-500' ([math]::Abs([decimal]$after.Adjust - (-500)) -lt 0.005)
    $flowType = [string](S ("SELECT type FROM member_flows WHERE member_id=" + $idA + " ORDER BY id DESC LIMIT 1"))
    Chk 'W4d-flow-type-is-adjust' ($flowType -eq $tAdjust)
    $flowAmt = [decimal](S ("SELECT amount FROM member_flows WHERE member_id=" + $idA + " ORDER BY id DESC LIMIT 1"))
    Chk 'W4e-flow-amount-negative' ([math]::Abs($flowAmt - (-500)) -lt 0.005)
    $flowNote = [string](S ("SELECT note FROM member_flows WHERE member_id=" + $idA + " ORDER BY id DESC LIMIT 1"))
    Chk 'W4f-reason-in-flow-note' ($flowNote.Contains('previous owner'))
    $logged = [int](S ("SELECT COUNT(*) FROM op_logs WHERE action='" + (CStr @(0x6838, 0x9500, 0x4F1A, 0x5458, 0x4F59, 0x989D)) + "'"))
    Chk 'W4g-writeoff-logged' ($logged -ge 1)
    # a second write-off on a zero balance is refused
    $e3 = BizErr { $svc.WriteOffBalance($idA, 'again', 'probe') }
    Chk 'W5-second-writeoff-refused' ($null -ne $e3)

    # ================= 3. archive with a balance writes it off in the same step =================
    # Owner 10-15: "merge the two buttons into one" - archiving clears any remaining
    # balance itself (same 'adjust' flow path), it no longer refuses.
    $mC = $svc.CreateMember('DelC', '13800003003', '', '', '', [decimal]80, [decimal]0, 0, 'probe')
    $idC = [int]$mC.Id
    [void]$svc.DeleteMember($idC, 'moved away, never coming back')
    Chk 'D1a-c-archived' ([string](S ("SELECT IFNULL(deleted_at,'') FROM members WHERE id=" + $idC)).Length -gt 0)
    Chk 'D1b-c-balance-zero' ([math]::Abs([decimal](S ("SELECT balance FROM members WHERE id=" + $idC))) -lt 0.005)
    $ckC = $svc.GetBalanceCheck($idC)
    Chk 'D1c-c-identity-still-holds' ($ckC.Ok -eq $true)
    $adjCnt = [int](S ("SELECT COUNT(*) FROM member_flows WHERE member_id=" + $idC + " AND type='" + $tAdjust + "'"))
    Chk 'D1d-c-writeoff-flow-written' ($adjCnt -eq 1)
    $adjNote = [string](S ("SELECT note FROM member_flows WHERE member_id=" + $idC + " AND type='" + $tAdjust + "'"))
    Chk 'D1e-c-reason-in-flow-note' ($adjNote.Contains('moved away'))
    $archLog = [string](S ("SELECT detail FROM op_logs WHERE action='" + (CStr @(0x5F52, 0x6863, 0x4F1A, 0x5458)) + "' ORDER BY id DESC LIMIT 1"))
    Chk 'D1f-c-log-mentions-writeoff' ($archLog.Contains('80'))

    # ================= 4. double archive is refused =================
    $eDbl = BizErr { $svc.DeleteMember($idC, 'again') }
    Chk 'D1g-double-archive-refused' ($null -ne $eDbl)
    $delAt = [string](S ("SELECT IFNULL(deleted_at,'') FROM members WHERE id=" + $idC))
    Chk 'D3-deleted-at-set' ($delAt.Length -gt 0)
    $delReason = [string](S ("SELECT IFNULL(deleted_reason,'') FROM members WHERE id=" + $idC))
    Chk 'D4-reason-stored' ($delReason.Contains('moved away'))
    $delBy = [string](S ("SELECT IFNULL(deleted_by,'') FROM members WHERE id=" + $idC))
    Chk 'D5-deleted-by-stored' ($delBy.Length -gt 0)
    # physical row still there: records stay queryable
    Chk 'D6-row-still-present' ([int](S ("SELECT COUNT(*) FROM members WHERE id=" + $idC)) -eq 1)
    Chk 'D7-flows-still-there' ([int](S ("SELECT COUNT(*) FROM member_flows WHERE member_id=" + $idC)) -ge 2)
    # phone / card_no still occupied (never released)
    Chk 'D8-phone-still-occupied' ([int](S ("SELECT COUNT(*) FROM members WHERE phone='13800003003'")) -eq 1)
    $e5 = BizErr { $svc.CreateMember('Dup', '13800003003', '', '', '', [decimal]0, [decimal]0, 0, 'probe') }
    Chk 'D9-phone-cannot-be-reused' ($null -ne $e5)
    # double delete refused
    $e6 = BizErr { $svc.DeleteMember($idC, 'again', 'probe') }
    Chk 'D10-double-delete-refused' ($null -ne $e6)
    $delLogged = [int](S ("SELECT COUNT(*) FROM op_logs WHERE action='" + (CStr @(0x5F52, 0x6863, 0x4F1A, 0x5458)) + "'"))
    Chk 'D11-delete-logged' ($delLogged -ge 1)

    # ================= 5. list / billing / liability exclude the deleted =================
    $inList = $false
    foreach ($r in $svc.SearchMembers('', $tAll, '', 500)) { if ($r.Id -eq $idC) { $inList = $true } }
    Chk 'L1-not-in-default-list' ($inList -eq $false)
    $inFilter = $false
    foreach ($r in $svc.SearchMembers('', $tDel, '', 500)) { if ($r.Id -eq $idC) { $inFilter = $true } }
    Chk 'L2-shows-under-deleted-filter' ($inFilter -eq $true)
    # keyword search must NOT resurrect them into the normal view
    $kw = $svc.SearchMembers('DelC', $tAll, '', 500)
    Chk 'L3-keyword-does-not-resurrect' (($kw | Where-Object { $_.Id -eq $idC }).Count -eq 0)
    # summary counts agree with the list
    
    $cnt = 0; $bal = [decimal]0; $abn = 0
    $svc.Summary('', $tAll, '', [ref]$cnt, [ref]$bal, [ref]$abn)
    $live = [int](S "SELECT COUNT(*) FROM members WHERE IFNULL(deleted_at,'')=''")
    Chk 'L4-summary-count-matches' ($cnt -eq $live)
    # billing member search
    $billing = $pos.SearchBillingMembers('DelC', 20)
    Chk 'L5-not-in-billing-search' (($billing | Where-Object { $_.Id -eq $idC }).Count -eq 0)
    Chk 'L6-billing-snapshot-null' ($null -eq $pos.GetBillingMember($idC))
    # liability excludes deleted (and reported-lost) members; expected value read straight from SQL
    $liab = New-Object MemberManager.BLL.StoredValueService
    $m = $liab.Metrics()
    $expLiab = [decimal](S ("SELECT IFNULL(SUM(balance),0) FROM members WHERE status<>'" + $tLoss + "' AND IFNULL(deleted_at,'')=''"))
    Chk 'L7-liability-excludes-deleted' ([math]::Abs([decimal]$m.Liability - $expLiab) -lt 0.005)
    L ('  diag L7: liability=' + $m.Liability + ' expected=' + $expLiab)

    # ================= 6. checkout refuses a deleted member =================
    $itemId = [int](S 'SELECT id FROM service_items WHERE card_times=0 AND enabled=1 ORDER BY id LIMIT 1')
    $price  = [decimal](S ("SELECT price FROM service_items WHERE id=" + $itemId))
    $lines = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $cl = New-Object MemberManager.BLL.PosService+CartLine
    $cl.ItemId = $itemId; $cl.Qty = 1; $cl.ByCard = $false; $cl.Name = 'X'; $cl.Price = $price; $cl.Unit = (CStr @(0x6B21)); $cl.IsCardItem = $false
    $lines.Add($cl)
    $e7 = BizErr { $pos.Checkout($lines, $idC, [decimal]0, $price, $null, 1) }
    Chk 'L8-checkout-refuses-deleted' ($null -ne $e7)

    # ================= 7. manager only =================
    [MemberManager.Common.Session]::SignIn(2, 'probe', $tCashier)
    $e8 = BizErr { $svc.WriteOffBalance($idA, 'cashier try', 'probe') }
    Chk 'P1-writeoff-manager-only' ($null -ne $e8)
    $e9 = BizErr { $svc.DeleteMember($idA, 'cashier try', 'probe') }
    Chk 'P2-delete-manager-only' ($null -ne $e9)
    [MemberManager.Common.Session]::SignIn(1, 'probe', $tMgr)

    # ================= 7b. a huge initial recharge must be refused with a readable message =================
    # Owner 10-15: creating a member with a huge amount threw "value too large for Int32"
    # (points math), which nobody can act on. It must be a plain Chinese message now.
    $big = [decimal]99999999999
    $eBig = BizErr { $svc.CreateMember('Huge', '13800003009', '', '', '', $big, [decimal]0, 1, 'probe') }
    # NOTE: compare against code-point-built CJK, never literal CJK in this script
    # (a BOM-less UTF-8 file is read as ANSI by PS 5.1 and the literals get mangled).
    $wTooBig = CStr @(0x8FC7, 0x5927)   # too big
    $wCap    = CStr @(0x4E0A, 0x9650)   # upper limit
    L ('  diag X2: msg=[' + $eBig + ']')
    Chk 'X1-huge-amount-refused' ($null -ne $eBig)
    Chk 'X2-huge-amount-message-readable' (($eBig -ne $null) -and (($eBig.Contains($wTooBig)) -or ($eBig.Contains($wCap))))
    # the points math itself must never overflow either (direct API call)
    $pt = New-Object MemberManager.BLL.PointService
    $connP = [MemberManager.DAL.Db]::OpenConnection()
    $txnP = $connP.BeginTransaction()
    try {
        $ptsBig = $pt.AddPoints($idB, [decimal]99999999999, 'probe', (New-Object MemberManager.BLL.PointService).GetPointConfig(), $connP, $txnP)
        Chk 'X3-addpoints-no-overflow' ($ptsBig -gt 0)
        $txnP.Rollback()
    } catch {
        Chk 'X3-addpoints-no-overflow' $false
        try { $txnP.Rollback() } catch { }
    } finally { $connP.Close() }

    # ================= 8. delete the remaining member (its balance is already 0) =================
    [void]$svc.DeleteMember($idA, 'cleaned up')
    Chk 'Z1-a-deleted' ([string](S ("SELECT IFNULL(deleted_at,'') FROM members WHERE id=" + $idA)).Length -gt 0)
    $left = [int](S "SELECT COUNT(*) FROM members WHERE IFNULL(deleted_at,'')=''")
    Chk 'Z2-live-count-sane' ($left -ge 1)

    $conn.Close()
}
catch {
    L ('ERROR: ' + $_.Exception.Message)
    if ($_.Exception.InnerException) { L ('  inner: ' + $_.Exception.InnerException.Message) }
    L ('  at: ' + $_.InvocationInfo.PositionMessage)
    L ('  stack: ' + (($_.Exception.ToString()) -replace '\r?\n',' | '))
    $script:fail++
}
if ($script:fail -eq 0) { L 'SMOKE OK' } else { L ('FAILURES=' + $script:fail); L ('SMOKE FAIL ' + $script:fail) }
Save-Log
if ($script:fail -gt 0) { exit 1 }

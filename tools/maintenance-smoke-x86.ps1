# Maintenance smoke (32-bit PowerShell, x86 SQLite interop)
# Covers the 11-05 batch:
#   A) LogService   - keep-days purge, 30-day clamp, trigger restored after purge, stats
#   B) PosService   - hold -> take round-trip (status/line count/open id unchanged)
#   C) ExportDir    - default export dir now lives under the install root (exports\)
#   D) RecordService- held orders listed in rows, but totals count settled orders only
#   E) ListStaffOptions - carries the unique login name; archived staff disappear
#   H) VoidHeldOrder   - Clear voids a taken held order, keeps the row, never touches money
#   I) Cashier perms   - recharge + selling are POS actions, never gated by the value module switch
#
# Runs against a THROWAWAY db (MEMBERMANAGER_ROOT pinned to a temp root) - never touches beta\data.
# Source must stay PURE ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI and silently breaks quotes);
# every Chinese string is built from code points at runtime.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'beta'
$tmp = Join-Path $PSScriptRoot ('_smoke_maint_tmp\' + [guid]::NewGuid().ToString('N').Substring(0, 8))

function CJK([int[]]$cp) {
    $s = ''
    foreach ($c in $cp) { $s = $s + [char]$c }
    return $s
}
$SUCCESS   = CJK @(0x6210, 0x529F)                                     # ok
$HOLD      = CJK @(0x6302, 0x5355)                                     # held
$NORMAL    = CJK @(0x6B63, 0x5E38)                                     # normal
$CONSUME   = CJK @(0x6D88, 0x8D39)                                     # consume
$NO_DELETE = CJK @(0x65E5, 0x5FD7, 0x7981, 0x6B62, 0x5220, 0x9664)     # trigger message

$pass = 0
$fail = 0
function Check($name, $cond) {
    if ($cond) { $script:pass++; Write-Output ('PASS ' + $name) }
    else { $script:fail++; Write-Output ('FAIL ' + $name) }
}
function Say($m) { Write-Output $m }

$conn = $null
try {
    New-Item -ItemType Directory -Path (Join-Path $tmp 'data') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $tmp 'portable.txt'), 'temp root for smoke')
    # Pin BEFORE the assemblies resolve AppPaths (see AppPaths.Resolve: env wins over portable probing)
    $env:MEMBERMANAGER_ROOT = $tmp
    Say ('tmp=' + $tmp)

    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))
    Say 'assemblies loaded'

    $asDal = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    $asBll = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))
    $dalInit = $asDal.GetType('MemberManager.DAL.DatabaseInitializer')
    $dbType = $asDal.GetType('MemberManager.DAL.Db')

    [void]$dalInit.InvokeMember('EnsureDatabase', 'InvokeMethod', $null, $null, @())
    Say 'EnsureDatabase OK'

    $conn = $dbType.GetMethod('OpenConnection').Invoke($null, @())

    function Exec([string]$sql) {
        $c = $script:conn.CreateCommand()
        $c.CommandText = $sql
        return $c.ExecuteNonQuery()
    }
    function Scalar([string]$sql) {
        $c = $script:conn.CreateCommand()
        $c.CommandText = $sql
        return $c.ExecuteScalar()
    }

    $sid = [int](Scalar 'SELECT IFNULL(MIN(id),0) FROM staff')
    Check 'T1 seeded staff exists' ($sid -gt 0)

    # ---------------- C) export dir lives under the install root ----------------
    $sys = New-Object MemberManager.BLL.SystemInfoService
    $dir = $sys.GetExportDir()
    Say ('exportDir=' + $dir)
    Check 'C1 default export dir under install root' ($dir -like ($tmp + '*'))
    Check 'C2 default export dir named exports' ($dir.TrimEnd('\') -like '*\exports')
    Check 'C3 export dir created on disk' (Test-Path $dir)

    # ---------------- B) held order round-trip (hold -> take) ----------------
    # 11-05: the owner dropped the "cancel held order" feature (the cart already has Clear),
    # so all this smoke needs to prove is that a held order survives hold -> take unchanged.
    $pos = New-Object MemberManager.BLL.PosService
    # HoldOrder wants IList<CartLine> - a plain List[object] will NOT bind, so build the
    # closed generic type from the nested CartLine type via reflection.
    $cartLineType = $asBll.GetType('MemberManager.BLL.PosService+CartLine')
    $listOpen = ([System.Collections.Generic.List[int]]).GetGenericTypeDefinition()
    $listType = $listOpen.MakeGenericType($cartLineType)
    $lines = [Activator]::CreateInstance($listType)
    $line = [Activator]::CreateInstance($cartLineType)
    # must point at a REAL enabled service item - PriceLines re-reads the price from the db
    # and rejects itemId=0 as 'item disabled'
    $itemRow = (Scalar 'SELECT id||''|''||name||''|''||price||''|''||unit FROM service_items WHERE enabled=1 AND IFNULL(deleted_at,'''')='''' ORDER BY id LIMIT 1').ToString().Split('|')
    $line.ItemId = [int]$itemRow[0]
    $line.Qty = 1
    $line.ByCard = $false
    $line.Name = [string]$itemRow[1]
    $line.Price = [decimal]$itemRow[2]
    $line.Unit = [string]$itemRow[3]
    $line.IsCardItem = $false
    [void]$lines.Add($line)
    $holdNo = $pos.HoldOrder($lines, $null, $sid)
    $oid = [int](Scalar ("SELECT id FROM orders WHERE order_no='" + $holdNo + "'"))
    Check 'B1 hold creates a held order' ($oid -gt 0 -and ([string](Scalar ("SELECT status FROM orders WHERE id=" + $oid))) -eq $HOLD)
    $taken = $pos.TakeOrder($oid)
    Check 'B2 take returns same order id' ([int]$taken.OrderId -eq $oid)
    Check 'B3 take restores one line' ($taken.Lines.Count -eq 1)
    Check 'B4 take does not change status' (([string](Scalar ("SELECT status FROM orders WHERE id=" + $oid))) -eq $HOLD)

    # ---------------- D) records: rows list held, totals count settled only ----------------
    $holdNo2 = 'H99999999-02'
    Exec ("INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) " +
        "VALUES('$holdNo2','2099-01-01',NULL,$sid,777,0,777,'$CONSUME','$HOLD')")
    $rec = New-Object MemberManager.BLL.RecordService
    $ALL = ''
    $q = $rec.Query($ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, 2000, 0)
    $hit = 0
    foreach ($row in $q.Rows) { if ($row.OrderNo -eq $holdNo2) { $hit++ } }
    Check 'D1 held order listed in records' ($hit -eq 1)
    $sumHeld = [double](Scalar "SELECT IFNULL(SUM(receivable),0) FROM orders WHERE status='$HOLD'")
    $normalOnly = [double](Scalar "SELECT IFNULL(SUM(receivable),0) FROM orders WHERE status<>'$HOLD'")
    Check 'D2 totals exclude held money' ([math]::Abs([double]$q.Total.Receivable - $normalOnly) -lt 0.01)
    Check 'D3 held money would have mattered' ($sumHeld -ge 777)

    $q2 = $rec.Query($ALL, $ALL, $ALL, $ALL, $HOLD, $ALL, $ALL, $ALL, 2000, 0)
    $hit2 = 0
    foreach ($row in $q2.Rows) { if ($row.OrderNo -eq $holdNo2) { $hit2++ } }
    Check 'D4 status=held filter still finds it' ($hit2 -eq 1)
    Check 'D5 status=held count matches' ([int]$q2.Total.Count -eq [int](Scalar "SELECT COUNT(*) FROM orders WHERE status='$HOLD'"))

    # ---------------- E) staff options carry the unique login name ----------------
    $opts = $pos.ListStaffOptions()
    Check 'E1 staff options not empty' ($opts.Count -gt 0)
    $allNamed = $true
    foreach ($o in $opts) { if ([string]::IsNullOrWhiteSpace($o.LoginName)) { $allNamed = $false } }
    Check 'E2 every option carries a login name' $allNamed
    $dup = [int](Scalar "SELECT COUNT(*) FROM (SELECT login_name FROM staff WHERE IFNULL(deleted_at,'')='' GROUP BY login_name HAVING COUNT(*)>1)")
    Check 'E3 login names are unique' ($dup -eq 0)

    $target = $opts[0]
    Exec ("UPDATE staff SET deleted_at='2099-01-01 00:00:00' WHERE id=" + [int]$target.Id)
    $opts2 = $pos.ListStaffOptions()
    Check 'E4 archived staff drops out of options' ($opts2.Count -eq ($opts.Count - 1))
    Exec ("UPDATE staff SET deleted_at=NULL WHERE id=" + [int]$target.Id)
    Check 'E5 restored staff comes back' ($pos.ListStaffOptions().Count -eq $opts.Count)

    # ---------------- F) backup list time column ----------------
    # File names carry yyyyMMdd-HHmm (13 chars). The list used to format only 15-char stamps,
    # so every row showed the raw '20991231-2359'. Guard the fix here.
    $bdir = Join-Path $tmp 'backups'
    New-Item -ItemType Directory -Path $bdir -Force | Out-Null
    $fakeName = 'member-20991231-2359.db'
    $fake = Join-Path $bdir $fakeName
    [System.IO.File]::WriteAllText($fake, '')
    $bs = New-Object MemberManager.BLL.BackupService
    $row = $null
    foreach ($b in $bs.List()) { if ($b.FileName -eq $fakeName) { $row = $b } }
    Check 'F1 backup row listed' ($row -ne $null)
    Check 'F2 backup time formatted' ($row -ne $null -and [string]$row.Time -eq '2099-12-31 23:59')
    Remove-Item $fake -Force -ErrorAction SilentlyContinue

    # ---------------- A) log retention ----------------
    Exec ("INSERT INTO op_logs(operator,module,action,detail,result,created_at) " +
        "VALUES('smoke','smoke','old-row','ancient','$SUCCESS','2000-01-01 00:00:00')")
    Check 'A1 ancient log row seeded' ([int](Scalar "SELECT COUNT(*) FROM op_logs WHERE created_at='2000-01-01 00:00:00'") -eq 1)

    $logSvc = New-Object MemberManager.BLL.LogService
    $st0 = $logSvc.Stats()
    Check 'A2 stats read ok' ($st0.Total -gt 0 -and $st0.KeepDays -ge 30)
    Check 'A3 stats count overdue' ($st0.Overdue -ge 1)

    $r = $logSvc.Purge(180, 'smoke')
    Say ('purge: removed=' + $r.Removed + ' remain=' + $r.Remain + ' keepDays=' + $r.KeepDays + ' cutoff=' + $r.Cutoff)
    Check 'A4 purge removed the ancient row' ([int](Scalar "SELECT COUNT(*) FROM op_logs WHERE created_at='2000-01-01 00:00:00'") -eq 0)
    Check 'A5 purge reports removals' ([int]$r.Removed -ge 1)
    Check 'A6 keep-days honoured' ([int]$r.KeepDays -eq 180)

    $r2 = $logSvc.Purge(5, 'smoke')
    Check 'A7 keep-days clamped to 30' ([int]$r2.KeepDays -eq 30)

    $st1 = $logSvc.Stats()
    Check 'A8 last-purge stamped' (-not [string]::IsNullOrWhiteSpace($st1.LastPurge))

    # trigger must be back: a plain DELETE has to abort
    Exec ("INSERT INTO op_logs(operator,module,action,detail,result) VALUES('smoke','smoke','probe','probe','$SUCCESS')")
    $pid2 = [int](Scalar "SELECT MAX(id) FROM op_logs")
    $aborted = $false
    $msg = ''
    try { Exec ("DELETE FROM op_logs WHERE id=$pid2") } catch { $aborted = $true; $msg = $_.Exception.Message }
    Check 'A9 delete trigger restored after purge' $aborted
    if ($aborted) { Say ('trigger msg=' + $msg) }
    Check 'A10 trigger message unchanged' ($msg -like ('*' + $NO_DELETE + '*'))

    # ---------------- G) run mode = what is actually installed ----------------
    # The installer ships portable.txt into {app}, so the old "portable flag" check called an
    # installed copy a green build. RunProfile must key off unins000.exe / OS version / runtime\.
    $asCommon2 = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    $rpType = $asCommon2.GetType('MemberManager.Common.RunProfile')
    Check 'G0 RunProfile type present' ($rpType -ne $null)
    $detect = $rpType.GetMethod('Detect', [System.Type[]]@([string], [bool]))
    # PowerShell wraps every literal in PSObject; reflection needs a plain Object[] with real types.
    function DetectMode([string]$dir, [bool]$portable) {
        $a = New-Object Object[] 2
        $a[0] = $dir
        $a[1] = $portable
        return $detect.Invoke($null, $a)
    }

    $plain = Join-Path $tmp 'mode-plain'
    New-Item -ItemType Directory -Path $plain -Force | Out-Null
    $d1 = DetectMode $plain $true
    Check 'G1 unzipped folder -> portable build' ($d1.Key -eq 'portable' -and $d1.Installed -eq $false)
    $d2 = DetectMode $plain $false
    Check 'G2 uninstalled without portable flag -> loose' ($d2.Key -eq 'loose')

    $inst = Join-Path $tmp 'mode-installed'
    New-Item -ItemType Directory -Path $inst -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $inst 'unins000.exe'), '')
    $d3 = DetectMode $inst $true
    $legacy = ([Environment]::OSVersion.Version.Major -lt 10)
    $want = 'normal'
    if ($legacy) { $want = 'win7' }
    Check 'G3 installer copy -> normal/win7 (never portable)' ($d3.Key -eq $want -and $d3.Installed -eq $true)
    Check 'G4 install evidence beats portable flag' ($d3.Key -ne 'portable')
    Check 'G5 kernel reported' (-not [string]::IsNullOrWhiteSpace($d3.Kernel))

    $rt = Join-Path $inst 'runtime'
    New-Item -ItemType Directory -Path $rt -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $rt 'msedgewebview2.exe'), '')
    $d4 = DetectMode $inst $true
    Check 'G6 bundled runtime detected' ($d4.BundledRuntime -eq $true)
    Check 'G7 bundled kernel named' ($d4.Kernel.Length -gt 2)

    $si = New-Object MemberManager.BLL.SystemInfoService
    $stRun = $si.GetStatus()
    Say ('status mode=' + $stRun.RunModeKey + ' text=' + $stRun.RunModeText + ' kernel=' + $stRun.WebViewKernel)
    Check 'G8 status carries run mode' (-not [string]::IsNullOrWhiteSpace($stRun.RunModeText) -and
        -not [string]::IsNullOrWhiteSpace($stRun.RunModeDesc) -and
        -not [string]::IsNullOrWhiteSpace($stRun.WebViewKernel))
    Check 'G9 mode key is a known value' (@('portable', 'win7', 'normal', 'loose') -contains $stRun.RunModeKey)

    # ---------------- H) clear = void the held order (11-05 owner decision) ----------------
    # Must run AFTER section D: D compares a raw SQL count of held orders with the
    # records-query count, and a voided row would make those two disagree.
    $ddl = [string](Scalar "SELECT sql FROM sqlite_master WHERE type='table' AND name='orders'")
    Check 'H1 orders.voided_at column added by patch' ($ddl -like '*voided_at*')

    $payBefore = [int](Scalar 'SELECT COUNT(*) FROM payments')
    $flowBefore = [int](Scalar 'SELECT COUNT(*) FROM member_flows')
    $commBefore = [int](Scalar 'SELECT COUNT(*) FROM commissions')
    $dash = New-Object MemberManager.BLL.DashboardService
    $pendingBefore = [int]$dash.CountPendingOrders()

    $voidNo = $pos.VoidHeldOrder($oid)
    Check 'H2 void returns original order no' ($voidNo -eq $holdNo)
    Check 'H3 voided_at stamped' (-not [string]::IsNullOrWhiteSpace([string](Scalar ("SELECT IFNULL(voided_at,'') FROM orders WHERE id=" + $oid))))
    Check 'H4 row kept and status unchanged' (([string](Scalar ("SELECT status FROM orders WHERE id=" + $oid))) -eq $HOLD)

    $stillListed = $false
    foreach ($h in $pos.ListHeldOrders()) { if ([int]$h.Id -eq $oid) { $stillListed = $true } }
    Check 'H5 voided order gone from held list' (-not $stillListed)
    Check 'H6 dashboard badge drops by one' ([int]$dash.CountPendingOrders() -eq ($pendingBefore - 1))

    $takeFailed = $false
    try { [void]$pos.TakeOrder($oid) } catch { $takeFailed = $true }
    Check 'H7 voided order cannot be taken' $takeFailed

    $recH = New-Object MemberManager.BLL.RecordService
    $qh = $recH.Query($ALL, $ALL, $holdNo, $ALL, $ALL, $ALL, $ALL, $ALL, 2000, 0)
    $recHit = 0
    foreach ($row in $qh.Rows) { if ($row.OrderNo -eq $holdNo) { $recHit++ } }
    Check 'H8 voided order gone from records' ($recHit -eq 0)

    $againFailed = $false
    try { [void]$pos.VoidHeldOrder($oid) } catch { $againFailed = $true }
    Check 'H9 second void rejected' $againFailed

    Check 'H10 void leaves audit log' ([int](Scalar ("SELECT COUNT(*) FROM op_logs WHERE detail LIKE '%" + $holdNo + "%'")) -ge 1)
    Check 'H11 payments untouched' ([int](Scalar 'SELECT COUNT(*) FROM payments') -eq $payBefore)
    Check 'H12 flows untouched' ([int](Scalar 'SELECT COUNT(*) FROM member_flows') -eq $flowBefore)
    Check 'H13 commissions untouched' ([int](Scalar 'SELECT COUNT(*) FROM commissions') -eq $commBefore)

    Exec ("INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) " +
        "VALUES('D99999999-01','2099-01-01',NULL,$sid,10,0,10,'$CONSUME','$NORMAL')")
    $normalId = [int](Scalar "SELECT id FROM orders WHERE order_no='D99999999-01'")
    $normalFailed = $false
    try { [void]$pos.VoidHeldOrder($normalId) } catch { $normalFailed = $true }
    Check 'H14 settled order cannot be voided' $normalFailed
    Check 'H15 settled order still normal' (([string](Scalar ("SELECT status FROM orders WHERE id=" + $normalId))) -eq $NORMAL)

    # ---------------- I) cashier default: recharge and sell cards (11-05 owner decision) ----------------
    # Owner asked: cashiers MUST be able to recharge members and sell cards.
    # Guards: manager-only maintenance pages/APIs must NOT leak out with this module.
    $M_VALUE = CJK @(0x50A8, 0x503C, 0x4E0E, 0x6B21, 0x5361)   # stored value & cards
    $R_CASH  = CJK @(0x6536, 0x94F6, 0x5458)                     # cashier
    $R_BOSS  = CJK @(0x5E97, 0x957F)                             # manager
    $R_TECH  = CJK @(0x6280, 0x5E08)                             # technician
    $M_SYS   = CJK @(0x7CFB, 0x7EDF, 0x8BBE, 0x7F6E)            # system settings
    $qValue  = "SELECT allowed FROM role_perms WHERE role='" + $R_CASH + "' AND module='" + $M_VALUE + "'"

    Check 'I1 new db cashier has value module' ([int](Scalar $qValue) -eq 1)
    Check 'I2 boss has value module' ([int](Scalar ("SELECT allowed FROM role_perms WHERE role='" + $R_BOSS + "' AND module='" + $M_VALUE + "'")) -eq 1)
    Check 'I3 technician has no value module' ([int](Scalar ("SELECT allowed FROM role_perms WHERE role='" + $R_TECH + "' AND module='" + $M_VALUE + "'")) -eq 0)
    Check 'I4 cashier still has no system settings' ([int](Scalar ("SELECT allowed FROM role_perms WHERE role='" + $R_CASH + "' AND module='" + $M_SYS + "'")) -eq 0)

    # Old db migration: historical 0 must be lifted on upgrade, and stay lifted.
    [void](Exec ("UPDATE role_perms SET allowed=0 WHERE role='" + $R_CASH + "' AND module='" + $M_VALUE + "'"))
    [void]$dalInit.InvokeMember('EnsureDatabase', 'InvokeMethod', $null, $null, @())
    Check 'I5 old db role default repaired on upgrade' ([int](Scalar $qValue) -eq 1)

    # A per-staff configured account: missing row filled from role default, explicit 0 respected.
    [void](Exec ("INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES('SMOKE-CASH','" + $R_CASH + "','0','',1)"))
    $csId = [int](Scalar "SELECT id FROM staff WHERE name='SMOKE-CASH'")
    [void](Exec ("INSERT INTO staff_perms(staff_id,module,allowed) VALUES(" + $csId + ",'" + $M_SYS + "',0)"))
    [void]$dalInit.InvokeMember('EnsureDatabase', 'InvokeMethod', $null, $null, @())
    Check 'I6 per-staff account gets value module row' ([int](Scalar ("SELECT COUNT(*) FROM staff_perms WHERE staff_id=" + $csId + " AND module='" + $M_VALUE + "'")) -eq 1)
    Check 'I7 per-staff row inherits role default 1' ([int](Scalar ("SELECT allowed FROM staff_perms WHERE staff_id=" + $csId + " AND module='" + $M_VALUE + "'")) -eq 1)
    [void](Exec ("UPDATE staff_perms SET allowed=0 WHERE staff_id=" + $csId + " AND module='" + $M_VALUE + "'"))
    [void]$dalInit.InvokeMember('EnsureDatabase', 'InvokeMethod', $null, $null, @())
    Check 'I8 explicit per-staff OFF is not overwritten' ([int](Scalar ("SELECT allowed FROM staff_perms WHERE staff_id=" + $csId + " AND module='" + $M_VALUE + "'")) -eq 0)

    # New-account default list (BLL private static field) must carry the module.
    $permType = $asBll.GetType('MemberManager.BLL.PermissionService')
    $cdf = $permType.GetField('CashierDefault', [System.Reflection.BindingFlags]'NonPublic,Static')
    $cdfVal = [string[]]$cdf.GetValue($null)
    Check 'I9 new account default includes value module' ($cdfVal -contains $M_VALUE)
    Check 'I10 new account default keeps system settings out' (-not ($cdfVal -contains $M_SYS))

    # Server-side second gate with a REAL logged-in cashier session:
    # recharge/sell is allowed, plan/template maintenance is still rejected.
    $U_CASH = CJK @(0x6536, 0x94F6)   # default cashier login name
    $authType = $asBll.GetType('MemberManager.BLL.AuthService')
    $authObj = [Activator]::CreateInstance($authType)
    $logged = $true
    try { [void]$authType.GetMethod('Login').Invoke($authObj, @($U_CASH, '123456')) } catch { $logged = $false }
    Check 'I11 cashier can sign in' $logged
    $psObj = [Activator]::CreateInstance($permType)
    $allow = $true
    try { [void]$permType.GetMethod('RequireModule').Invoke($psObj, @($M_VALUE)) } catch { $allow = $false }
    Check 'I12 server gate allows cashier recharge' $allow
    $blocked = $false
    try { [void]$permType.GetMethod('RequireModule').Invoke($psObj, @($M_SYS)) } catch { $blocked = $true }
    Check 'I13 server gate blocks cashier plan maintenance' $blocked

    # ---- I14..I20: selling a card is a POS action, NOT a stored-value action (owner 11-05) ----
    # The compiled exe (not source text) is the source of truth for the bridge permission map.
    $M_POS   = CJK @(0x6536, 0x94F6, 0x5F00, 0x5355)     # pos module
    $M_CASHM = CJK @(0x73B0, 0x91D1)                      # cash payment method
    $K_SELL  = CJK @(0x552E, 0x5361)                      # order kind: sell card
    $mapSell = ''; $mapTpl = ''; $mapRecharge = ''; $mapRecharge2 = ''
    try {
        $asShell = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.exe'))
        $bdType  = $asShell.GetType('MemberManager.Shell.BridgeDispatcher')
        $swFld   = $bdType.GetField('SensitiveWrites', [System.Reflection.BindingFlags]'NonPublic,Static')
        $swMap   = $swFld.GetValue($null)
        $mapSell      = [string]$swMap['cards.SellCard']
        $mapTpl       = [string]$swMap['cards.SaveTemplate']
        $mapRecharge  = [string]$swMap['storedvalue.RechargeByStaff']
        $mapRecharge2 = [string]$swMap['storedvalue.Recharge']
    } catch {
        Say ('I-bridge-map: ' + $_.Exception.Message)
    }
    Check 'I14 bridge maps SellCard to POS module' ($mapSell -eq $M_POS)
    Check 'I15 bridge keeps template edit manager-only' ($mapTpl -eq $M_SYS)
    Check 'I16 bridge maps RechargeByStaff to POS module' ($mapRecharge -eq $M_POS)
    Check 'I17 bridge maps Recharge to POS module' ($mapRecharge2 -eq $M_POS)

    # Per-person switch: turning the value module OFF must NOT disable recharge OR selling.
    # staff_perms has rows => authoritative, so the POS row must be present too.
    $cashId = [int](Scalar ("SELECT id FROM staff WHERE login_name='" + $U_CASH + "'"))
    [void](Exec ("INSERT OR REPLACE INTO staff_perms(staff_id,module,allowed) VALUES(" + $cashId + ",'" + $M_VALUE + "',0)"))
    [void](Exec ("INSERT OR REPLACE INTO staff_perms(staff_id,module,allowed) VALUES(" + $cashId + ",'" + $M_POS + "',1)"))
    $valOff = $false
    try { [void]$permType.GetMethod('RequireModule').Invoke($psObj, @($M_VALUE)) } catch { $valOff = $true }
    Check 'I18 value module OFF is really OFF' $valOff
    $posOn = $true
    try { [void]$permType.GetMethod('RequireModule').Invoke($psObj, @($M_POS)) } catch { $posOn = $false }
    Check 'I19 value OFF still allows POS (recharge+sell)' $posOn

    # End-to-end: a REAL cashier session sells a card (BLL has no manager-only hard gate).
    $sellMid = [int](Scalar ("SELECT id FROM members WHERE status='" + $NORMAL + "' LIMIT 1"))
    $sellTid = [int](Scalar "SELECT id FROM card_templates WHERE enabled=1 LIMIT 1")
    if ($sellMid -eq 0) {
        [void](Exec ("INSERT INTO members(card_no,name,phone,status) VALUES('SMOKE-CARD','SMOKE-MEMBER','SMOKE-PHONE','" + $NORMAL + "')"))
        $sellMid = [int](Scalar "SELECT id FROM members WHERE card_no='SMOKE-CARD'")
    }
    if ($sellTid -eq 0) {
        [void](Exec "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('SMOKE-TPL','SMOKE-ITEM',5,150,12,1)")
        $sellTid = [int](Scalar "SELECT id FROM card_templates WHERE name='SMOKE-TPL'")
    }
    $sellBefore = [int](Scalar ("SELECT COUNT(*) FROM orders WHERE kind='" + $K_SELL + "'"))
    $sold = $false
    try {
        $cardType = $asBll.GetType('MemberManager.BLL.CardService')
        $cardObj  = [Activator]::CreateInstance($cardType)
        [void]$cardType.GetMethod('SellCard').Invoke($cardObj, @([int]$sellMid, [int]$sellTid, $M_CASHM, 'SMOKE-CASH', [int]5, [decimal]30, [int]0, [decimal]0, [int]0))
        $sold = $true
    } catch {
        Say ('I19 sell failed: ' + $_.Exception.Message)
    }
    Check 'I20 cashier sells a card end-to-end' $sold
    Check 'I21 sell-card order written' ([int](Scalar ("SELECT COUNT(*) FROM orders WHERE kind='" + $K_SELL + "'")) -eq ($sellBefore + 1))

    # Recharge must survive the very same per-person OFF switch (owner: cashiers MUST recharge).
    $balBefore = [decimal](Scalar ("SELECT balance FROM members WHERE id=" + $sellMid))
    $recharged = $false
    try {
        $valType = $asBll.GetType('MemberManager.BLL.StoredValueService')
        $valObj  = [Activator]::CreateInstance($valType)
        [void]$valType.GetMethod('RechargeByStaff').Invoke($valObj, @([int]$sellMid, [decimal]100, [decimal]0, $M_CASHM, 'SMOKE-CASH', [int]0))
        $recharged = $true
    } catch {
        Say ('I22 recharge failed: ' + $_.Exception.Message)
    }
    Check 'I22 cashier recharges end-to-end while value OFF' $recharged
    $balAfter = [decimal](Scalar ("SELECT balance FROM members WHERE id=" + $sellMid))
    Check 'I23 balance increased by 100' ($balAfter -eq ($balBefore + [decimal]100))
}
catch {
    Say ('MAINT FAILED: ' + $_.Exception.Message)
    if ($_.Exception.InnerException) { Say ('inner: ' + $_.Exception.InnerException.Message) }
    $fail = $fail + 1
}
finally {
    if ($conn -ne $null) { try { $conn.Dispose() } catch { } }
    Say '---'
    Say ('PASS=' + $pass + ' FAIL=' + $fail)
    try { Remove-Item $tmp -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue } catch { }
}

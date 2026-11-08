# ==============================================================
#  staff write-path smoke (x86 host required: SysWOW64 PowerShell)
#
#  Regression test for the "database is locked" bug:
#  PermissionService.SaveStaff / DeleteStaff used to call
#  DatabaseInitializer.SetSetting/DelSetting WHILE a transaction
#  was open. That opened a SECOND connection to the same file and
#  dead-locked under WAL -> SQLiteException: database is locked.
#  Now they use TxnSetSetting/TxnDelSetting on the same conn/txn.
#
#  Everything here runs against a THROWAWAY temp db (Db._dbPath
#  redirected via reflection). The real database is never touched.
#  Source must stay PURE ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI).
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_staff_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
function L([string]$m) { $log.Add($m) | Out-Null }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

$tempDb = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $mgr  = CStr @(0x5E97,0x957F)                 # manager
    $cash = CStr @(0x6536,0x94F6,0x5458)          # cashier
    $tech = CStr @(0x6280,0x5E08)                 # technician
    $tMei = CStr @(0x7F8E,0x53D1)                 # category: hair
    $tCi  = CStr @(0x6B21)                        # unit: time(s)
    $sOK  = CStr @(0x6B63,0x5E38)                 # status: normal

    # ---- point Db at a throwaway file ----
    $tempDb = Join-Path $env:TEMP ('mm_staff_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $f = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $f.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgr)
    L ('tempDb: ' + $tempDb)

    $perm = New-Object MemberManager.BLL.PermissionService

    # ===== 1. SaveStaff (INSERT + per-staff rate write) must not dead-lock =====
    $ok = $true
    try {
        $perm.SaveStaff(0, 'TestTechA', $tech, '13900000001', [decimal]0.10, $true, 'probe')
    } catch { $ok = $false; L ('  SaveStaff-threw: ' + $_.Exception.Message) }
    Chk 'W1-savestaff-insert-no-lock' $ok

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function Scalar([string]$sql, [hashtable]$ps) {
        $c = $conn.CreateCommand(); $c.CommandText = $sql
        if ($ps) { foreach ($k in $ps.Keys) { $p = $c.CreateParameter(); $p.ParameterName = $k; $p.Value = $ps[$k]; [void]$c.Parameters.Add($p) } }
        return ,$c.ExecuteScalar()
    }
    $newId = [int](Scalar "SELECT id FROM staff WHERE name='TestTechA'" $null)
    Chk 'W2-staff-row-exists' ($newId -gt 0)
    $rate = Scalar "SELECT value FROM settings WHERE key=@k" @{ '@k' = ('staff_rate_' + $newId) }
    Chk 'W3-rate-setting-written' ($rate -ne $null -and ([decimal]$rate -eq [decimal]0.10))

    # ===== 2. SaveStaff UPDATE + rate change to "inherit global" (rate<0 -> DELETE key) =====
    $ok = $true
    try {
        $perm.SaveStaff($newId, 'TestTechA2', $cash, '13900000002', [decimal](-1), $true, 'probe')
    } catch { $ok = $false; L ('  SaveStaff-update-threw: ' + $_.Exception.Message) }
    Chk 'W4-savestaff-update-no-lock' $ok
    $rate2 = Scalar "SELECT value FROM settings WHERE key=@k" @{ '@k' = ('staff_rate_' + $newId) }
    Chk 'W5-rate-key-deleted-on-inherit' ($rate2 -eq $null)
    $nm = Scalar "SELECT name FROM staff WHERE id=@id" @{ '@id' = $newId }
    Chk 'W6-name-updated' ($nm -eq 'TestTechA2')

    # ===== 3. DeleteStaff with NO business records must succeed (the reported bug) =====
    $ok = $true
    try {
        $perm.DeleteStaff($newId, 'probe')
    } catch { $ok = $false; L ('  DeleteStaff-threw: ' + $_.Exception.Message) }
    Chk 'W7-deletestaff-no-records' $ok
    # soft delete: row stays (history must keep the name) and deleted_at is stamped.
    # 11-04 (shop owner): deleting from the staff roster must NOT kill the login account -
    # that was the bug ("delete a cashier and they can no longer log in"). So enabled is
    # deliberately left untouched; only deleted_at is written. See WebServices.Ops.DeleteStaff.
    $gone = Scalar "SELECT COUNT(*) FROM staff WHERE id=@id AND deleted_at IS NULL" @{ '@id' = $newId }
    Chk 'W8-staff-hidden-from-list' ([int]$gone -eq 0)
    $stamp = Scalar "SELECT COUNT(*) FROM staff WHERE id=@id AND deleted_at IS NOT NULL" @{ '@id' = $newId }
    Chk 'W8b-staff-soft-deleted' ([int]$stamp -eq 1)
    # new contract: roster removal must not touch the login flag at all.
    # (these test rows have no password on purpose - SaveStaff never sets one - so only
    #  assert on enabled, which is the field DeleteStaff used to clobber)
    $alive = Scalar "SELECT COUNT(*) FROM staff WHERE id=@id AND enabled=1" @{ '@id' = $newId }
    Chk 'W8c-login-survives-roster-delete' ([int]$alive -eq 1)

    # ===== 4. DeleteStaff WITH business records must now SUCCEED; records survive =====
    $perm.SaveStaff(0, 'TestTechB', $tech, '13900000003', [decimal](-1), $true, 'probe')
    $bId = [int](Scalar "SELECT id FROM staff WHERE name='TestTechB'" $null)
    # create a member, then a real order attributed to TestTechB
    $ms = New-Object MemberManager.BLL.MemberService
    $mem = $ms.CreateMember('SmokeGuy', '13800000009', '', '', '', [decimal]500, [decimal]0, 1, 'probe')
    $conn2 = [MemberManager.DAL.Db]::OpenConnection()
    $ins = $conn2.CreateCommand()
    $ins.CommandText = "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES('D20990101-99',date('now'),@m,@s,100,0,100,'" + (CStr @(0x6D88,0x8D39)) + "',@st)"
    foreach ($kv in @{ '@m' = $mem.Id; '@s' = $bId; '@st' = $sOK }.GetEnumerator()) {
        $p = $ins.CreateParameter(); $p.ParameterName = $kv.Key; $p.Value = $kv.Value; $ins.Parameters.Add($p)
    }
    [void]$ins.ExecuteNonQuery()
    $conn2.Dispose()

    $ok = $true
    try { $perm.DeleteStaff($bId, 'probe') } catch { $ok = $false; L ('  DeleteStaff-with-records-threw: ' + $_.Exception.Message) }
    Chk 'W9-deletestaff-with-records-allowed' $ok
    $still = Scalar "SELECT COUNT(*) FROM staff WHERE id=@id AND deleted_at IS NULL" @{ '@id' = $bId }
    Chk 'W10-row-hidden-after-delete' ([int]$still -eq 0)
    # the order rows MUST survive untouched
    $orders = Scalar "SELECT COUNT(*) FROM orders WHERE staff_id=@id" @{ '@id' = $bId }
    Chk 'W10b-history-orders-survive' ([int]$orders -eq 1)
    $oname = Scalar ("SELECT s.name FROM orders o JOIN staff s ON s.id=o.staff_id WHERE o.staff_id=@id") @{ '@id' = $bId }
    Chk 'W10c-history-keeps-name' ($oname -eq 'TestTechB')

    # ===== 5. manager account must never be deletable =====
    $mgrId = [int](Scalar "SELECT MIN(id) FROM staff WHERE role=@r" @{ '@r' = $mgr })
    $refusedMgr = $false
    try { $perm.DeleteStaff($mgrId, 'probe') } catch { $refusedMgr = $true }
    Chk 'W11-manager-not-deletable' $refusedMgr

    # ===== 5b. StaffList must hide deleted rows =====
    $rows = $perm.StaffList()
    $hiddenB = $true; $hiddenA = $true
    $hasPwdOk = $true
    foreach ($r in $rows) {
        if ($r.Id -eq $bId) { $hiddenB = $false }
        if ($r.Id -eq $newId) { $hiddenA = $false }
        if ($null -eq $r.HasPwd) { $hasPwdOk = $false }
    }
    Chk 'W11b-stafflist-hides-deleted' ($hiddenA -and $hiddenB)
    Chk 'W11c-stafflist-haspwd-marker' $hasPwdOk

    # ===== 6. ListMemberCards must join card_templates (no "no such column: card_name") =====
    $pos = New-Object MemberManager.BLL.PosService
    $cardOk = $true
    try {
        $cards = $pos.ListMemberCards($mem.Id)
        L ('  ListMemberCards(no cards) -> ' + $cards.Count)
    } catch { $cardOk = $false; L ('  ListMemberCards-threw: ' + $_.Exception.Message) }
    Chk 'W12-listmembercards-no-sql-error' $cardOk

    # attach a card to the member and re-read (exercises the JOIN path)
    $conn3 = [MemberManager.DAL.Db]::OpenConnection()
    $ci = $conn3.CreateCommand()
    $ci.CommandText = "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status) VALUES(@m,(SELECT MIN(id) FROM card_templates),10,7,date('now','+1 year'),@st)"
    foreach ($kv in @{ '@m' = $mem.Id; '@st' = $sOK }.GetEnumerator()) {
        $p = $ci.CreateParameter(); $p.ParameterName = $kv.Key; $p.Value = $kv.Value; $ci.Parameters.Add($p)
    }
    [void]$ci.ExecuteNonQuery()
    $conn3.Dispose()

    $cards2 = $pos.ListMemberCards($mem.Id)
    Chk 'W13-listmembercards-returns-row' ($cards2.Count -eq 1)
    if ($cards2.Count -eq 1) {
        $c = $cards2[0]
        L ('  card: name=[' + $c.CardName + '] remain=' + $c.Remain + '/' + $c.Total + ' item=[' + $c.ItemName + '] unitPrice=' + $c.UnitPrice + ' expire=' + $c.ExpireDate)
        Chk 'W14-card-name-not-empty' ($c.CardName -ne '')
        Chk 'W15-card-remain-correct' ($c.Remain -eq 7 -and $c.Total -eq 10)
        Chk 'W16-card-unitprice-derived' ($c.UnitPrice -ge 0)
    }

    # ===== 7. SaveAccount (login account): new writes pwd hash + default perms;
    #          update with blank pwd must KEEP the old hash; non-blank must change it =====
    $acctId = 0
    $acctOk = $true
    try { $acctId = $perm.SaveAccount(0, 'TestAcctA', $cash, '13900000011', 'abc123', $true, 'probe') }
    catch { $acctOk = $false; L ('  SaveAccount-new-threw: ' + $_.Exception.Message) }
    Chk 'W17-saveaccount-insert-ok' ($acctOk -and $acctId -gt 0)

    $hasPwd = Scalar "SELECT COUNT(*) FROM staff WHERE id=@id AND pwd_hash IS NOT NULL AND pwd_hash<>'!nologin!'" @{ '@id' = $acctId }
    Chk 'W18-saveaccount-wrote-pwd-hash' ([int]$hasPwd -eq 1)

    $permRows = Scalar "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id" @{ '@id' = $acctId }
    Chk 'W19-saveaccount-default-perms-written' ([int]$permRows -gt 0)

    $hashBefore = Scalar "SELECT pwd_hash FROM staff WHERE id=@id" @{ '@id' = $acctId }
    $ok2 = $true
    try { [void]$perm.SaveAccount($acctId, 'TestAcctA2', $cash, '13900000012', '', $true, 'probe') }
    catch { $ok2 = $false; L ('  SaveAccount-update-threw: ' + $_.Exception.Message) }
    Chk 'W20-saveaccount-update-ok' $ok2
    $hashAfter = Scalar "SELECT pwd_hash FROM staff WHERE id=@id" @{ '@id' = $acctId }
    Chk 'W20b-blank-pwd-keeps-hash' ($hashBefore -eq $hashAfter)

    try { [void]$perm.SaveAccount($acctId, 'TestAcctA2', $cash, '13900000012', 'xyz789', $true, 'probe') } catch { }
    $hashChg = Scalar "SELECT pwd_hash FROM staff WHERE id=@id" @{ '@id' = $acctId }
    Chk 'W21-nonblank-pwd-changes-hash' ($hashChg -ne $hashAfter)

    $badRole = $false
    try { [void]$perm.SaveAccount(0, 'BadRoleGuy', 'Boss', '', 'abc123', $true, 'probe') } catch { $badRole = $true }
    Chk 'W22-bad-role-rejected' $badRole

    $needPwd = $false
    try { [void]$perm.SaveAccount(0, 'NoPwdGuy', $cash, '', '', $true, 'probe') } catch { $needPwd = $true }
    Chk 'W23-new-account-needs-pwd' $needPwd

    $shortPwd = $false
    try { [void]$perm.SaveAccount(0, 'ShortPwdGuy', $cash, '', '123', $true, 'probe') } catch { $shortPwd = $true }
    Chk 'W24-short-pwd-rejected' $shortPwd

    # new staff (SaveStaff path) must have an EMPTY pwd hash -> no login account (10-18: was '!nologin!' placeholder)
    $perm.SaveStaff(0, 'TestNoLogin', $tech, '13900000013', [decimal](-1), $true, 'probe')
    $nid = [int](Scalar "SELECT id FROM staff WHERE name='TestNoLogin'" $null)
    $ph = Scalar "SELECT pwd_hash FROM staff WHERE id=@id" @{ '@id' = $nid }
    Chk 'W25-savestaff-no-login-empty-pwd' ([string]$ph -eq '')

    # ===== login_name must be unique even when display names collide =====
    # 11-04 (shop owner): two staff named the same broke login - Login() looked up by
    # name and always hit the first row (often a disabled one), so the new account
    # could never get in. UpsertStaffRow now assigns a unique login_name.
    [void]$perm.SaveStaff(0, 'DupName', $tech, '13900000021', [decimal](-1), $true, 'probe')
    [void]$perm.SaveStaff(0, 'DupName', $tech, '13900000022', [decimal](-1), $true, 'probe')
    $dupRows = Scalar "SELECT COUNT(*) FROM staff WHERE name='DupName'" $null
    Chk 'W25b-dup-name-two-rows' ([int]$dupRows -eq 2)
    $distinct = Scalar "SELECT COUNT(DISTINCT IFNULL(login_name,'')) FROM staff WHERE name='DupName'" $null
    Chk 'W25c-dup-name-unique-login' ([int]$distinct -eq 2)
    # and the FIRST one keeps the plain name (so the account the owner already uses
    # does not silently change its login); the later one gets a suffix
    $firstLogin = Scalar "SELECT IFNULL(login_name,'') FROM staff WHERE name='DupName' ORDER BY id LIMIT 1" $null
    Chk 'W25d-dup-name-first-keeps-plain' ([string]$firstLogin -eq 'DupName')

    # LoadStaffNameRole uniformity: missing id must throw the SAME message from both entry points
    $msgA = ''; $msgB = ''
    try { [void]$perm.AccountPerms(999999) } catch { if ($_.Exception.InnerException) { $msgA = $_.Exception.InnerException.Message } }
    $emptyMods = [string[]]@()
    try { [void]$perm.SetAccountPerms(999999, $emptyMods, 'probe') } catch { if ($_.Exception.InnerException) { $msgB = $_.Exception.InnerException.Message } }
    L ('  msgA=[' + $msgA + ']')
    L ('  msgB=[' + $msgB + ']')
    Chk 'W26-accountperms-missing-throws' ($msgA -ne '')
    Chk 'W27-setaccountperms-missing-throws' ($msgB -ne '' -and $msgB -eq $msgA)

    $conn.Dispose()
    L ''
    if ($fail -eq 0) { L 'STAFF SMOKE OK' } else { L ('STAFF SMOKE FAILED: ' + $fail) }
}
catch {
    L ('STAFF SMOKE ERROR: ' + $_.Exception.ToString())
}
finally {
    if ($tempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $tempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
    [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($false)))
}
$log
exit 0

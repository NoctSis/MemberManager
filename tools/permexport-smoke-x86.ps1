# ==============================================================
#  Per-person permissions (staff_perms) + Excel export (xlsx)
#  smoke test - x86 host required (SysWOW64).
#  Pure ASCII source. Chinese strings are built from code points.
#
#  Phase A: read-only checks against the REAL database, plus a
#           reflection check that the new bridge methods really
#           exist in the shipped dist DLL (that is what caused the
#           earlier 401 - method missing on the deployed DLL).
#  Phase B: full write-path tests against a THROWAWAY temp db.
#           AppPaths._rootDir and Db._dbPath are both redirected,
#           so the real database and the real data folder are never
#           touched. The xlsx is validated structurally (ZipArchive +
#           XmlDocument) - no Excel process is launched.
#  Usage:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ^
#     -NoProfile -ExecutionPolicy Bypass -File tools\permexport-smoke-x86.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_permexport_smoke.log'
$script:Log = New-Object 'System.Collections.Generic.List[string]'
$script:Fail = 0

function Log([string]$m) { $script:Log.Add($m) | Out-Null }
function Save-Log { [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($true))) }
function Check([string]$name, [bool]$cond) {
    if ($cond) { Log ('PASS ' + $name) } else { Log ('FAIL ' + $name); $script:Fail++ }
}
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

try {
    # ---- load dist assemblies (32-bit host required) ----
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    [void][System.Reflection.Assembly]::Load('System.IO.Compression, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
    [void][System.Reflection.Assembly]::Load('System.IO.Compression.FileSystem, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
    Log ('Host: ' + [System.IntPtr]::Size * 8 + '-bit, dist loaded')

    # ---- runtime Chinese strings ----
    $mMember  = CStr @(0x4F1A,0x5458,0x7BA1,0x7406)   # members
    $mCash    = CStr @(0x6536,0x94F6,0x5F00,0x5355)   # pos / checkout
    $mRefund  = CStr @(0x9000,0x6B3E,0x002F,0x6539,0x4EF7) # refund / repricing
    $mCard    = CStr @(0x50A8,0x503C,0x4E0E,0x6B21,0x5361) # stored value + times cards
    $mShift   = CStr @(0x4EA4,0x63A5,0x73ED,0x65E5,0x7ED3) # shift / daily close
    $mReport  = CStr @(0x62A5,0x8868,0x5BFC,0x51FA)   # report export
    $mComm    = CStr @(0x5458,0x5DE5,0x63D0,0x6210)   # commission
    $mPerms   = CStr @(0x6743,0x9650,0x8BBE,0x7F6E)   # permission setup
    $mBackup  = CStr @(0x6570,0x636E,0x5907,0x4EFD)   # backup
    $mSys     = CStr @(0x7CFB,0x7EDF,0x8BBE,0x7F6E)   # system settings
    $mMall    = CStr @(0x79EF,0x5206,0x5546,0x57CE)   # points mall (10-24: 11th module)
    $rMgr     = CStr @(0x5E97,0x957F)                 # role: manager
    $rCash    = CStr @(0x6536,0x94F6,0x5458)          # role: cashier
    $rTech    = CStr @(0x6280,0x5E08)                 # role: technician
    $sSum     = CStr @(0x8425,0x4E1A,0x6C47,0x603B)   # sheet: summary
    $sDaily   = CStr @(0x6BCF,0x65E5,0x8425,0x6536)   # sheet: daily revenue
    $sPay     = CStr @(0x652F,0x4ED8,0x65B9,0x5F0F)   # sheet: pay methods
    $sRank    = CStr @(0x63D0,0x6210,0x6392,0x540D)   # sheet: commission rank
    $rgWeek   = CStr @(0x672C,0x5468)                 # range: this week
    $rgMonth  = CStr @(0x672C,0x6708)                 # range: this month
    $rgCustom = CStr @(0x81EA,0x5B9A,0x4E49)          # range: custom
    $rgToday  = CStr @(0x4ECA,0x5929)                 # range: today
    $cashCn   = CStr @(0x73B0,0x91D1)                 # pay method: cash
    $stNormal = CStr @(0x6B63,0x5E38)                 # order status: normal
    $tRecharge= CStr @(0x5145,0x503C)                 # order kind: recharge
    $allModules = @($mMember,$mCash,$mRefund,$mCard,$mShift,$mReport,$mComm,$mPerms,$mBackup,$mSys,$mMall)
    # Module count is read from the DLL, not hardcoded, so adding a module never
    # breaks these assertions again (10-24: points mall became the 11th).
    # NOTE: ModuleNames is a static readonly field - PS reflection needs GetValue,
    # direct .Count on the FieldInfo returns 0.
    $modCount = [int]([MemberManager.BLL.PermissionService].GetField('ModuleNames').GetValue($null)).Count
    if ($modCount -ne $allModules.Count) {
        throw ('ModuleNames has ' + $modCount + ' entries but script knows ' + $allModules.Count)
    }

    # Expect a BizException to be thrown by the scriptblock; returns true on success.
    # PowerShell wraps native throws in MethodInvocationException; the real BizException
    # lives on .InnerException, so we check both frames.
    function Expect-Biz([scriptblock]$sb, [string]$what) {
        try { & $sb; Log ('  no throw: ' + $what); return $false }
        catch {
            $e = $_.Exception
            if ($e -is [MemberManager.Common.BizException]) { return $true }
            if ($null -ne $e.InnerException -and $e.InnerException -is [MemberManager.Common.BizException]) { return $true }
            $inner = ''
            if ($null -ne $e.InnerException) { $inner = ' | inner: ' + $e.InnerException.Message }
            Log ('  wrong exception: ' + $what + ' -> ' + $e.Message + $inner)
            return $false
        }
    }
    function Scalar([string]$sql, [hashtable]$ps) {
        $conn = [MemberManager.DAL.Db]::OpenConnection()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = $sql
            if ($ps) {
                foreach ($k in $ps.Keys) {
                    $p = $cmd.CreateParameter(); $p.ParameterName = $k; $p.Value = $ps[$k]; $cmd.Parameters.Add($p) | Out-Null
                }
            }
            return ,$cmd.ExecuteScalar()
        } finally { $conn.Dispose() }
    }
    function Has-Method([type]$t, [string]$m, [string[]]$params) {
        if ($null -eq $t) { return $false }
        if ($params -eq $null) { $params = @() }
        foreach ($mi in $t.GetMethods([System.Reflection.BindingFlags]'Public,Instance')) {
            if ($mi.Name -ne $m) { continue }
            $ps = $mi.GetParameters()
            # tolerate trailing optional parameters (e.g. the default style argument)
            if ($ps.Length -lt $params.Length) { continue }
            $ok = $true
            for ($i = 0; $i -lt $params.Length; $i++) {
                if ([string]$ps[$i].ParameterType.Name -ne $params[$i]) { $ok = $false }
            }
            if (-not $ok) { continue }
            for ($i = $params.Length; $i -lt $ps.Length; $i++) {
                if ($ps[$i].HasDefaultValue -eq $false) { $ok = $false }
            }
            if ($ok) { return $true }
        }
        return $false
    }

    # ================= Phase A: real db, read-only =================
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $realDb = Join-Path $dist 'data\member.db'
    $fPath.SetValue($null, $realDb)
    Log ('--- Phase A: read-only on real db (' + $realDb + ') ---')

    # A1: the new table is there and keyed by (staff_id, module)
    $colCount = [int](Scalar "SELECT COUNT(*) FROM pragma_table_info('staff_perms')" $null)
    Check 'A1-staff_perms-exists' ($colCount -ge 4)
    $tableSql = [string](Scalar "SELECT sql FROM sqlite_master WHERE type='table' AND name='staff_perms'" $null)
    Check 'A1b-staff_perms-pk' ([bool]($tableSql -match 'PRIMARY KEY\(staff_id,module\)'))

    # A2: every module value in the real table is one of the ten known modules
    $permSvc = New-Object MemberManager.BLL.PermissionService
    $badModule = 0
    $connA = [MemberManager.DAL.Db]::OpenConnection()
    try {
        $cmd = $connA.CreateCommand()
        $cmd.CommandText = "SELECT module FROM staff_perms"
        $r = $cmd.ExecuteReader()
        while ($r.Read()) {
            $mv = [string]$r.GetValue(0)
            if ($allModules -notcontains $mv) { $badModule++; Log ('  unknown module: ' + $mv) }
        }
        $r.Close()
    } finally { $connA.Dispose() }
    Check 'A2-all-modules-known' ($badModule -eq 0)

    # A3: the shipped DLL really exposes the new bridge surface (the 401 lesson)
    $tPerm = [MemberManager.BLL.PermissionService]
    $tRep  = [MemberManager.BLL.ReportService]
    $tSys  = [MemberManager.BLL.SystemInfoService]
    $tComm = [MemberManager.BLL.CommissionService]
    $tXw   = [MemberManager.BLL.ExcelWriter]
    Check 'A3-ExcelWriter-type' ($null -ne $tXw)
    Check 'A3a-ExcelWriter-Save'     (Has-Method $tXw 'Save' @('String'))
    Check 'A3b-ExcelWriter-Sheet'    (Has-Method $tXw 'Sheet' @('String'))
    Check 'A3c-ExcelWriter-Text'     (Has-Method $tXw 'Text' @('Int32','Int32','String'))
    Check 'A3d-ExcelWriter-Num'      (Has-Method $tXw 'Num' @('Int32','Int32','Decimal'))
    Check 'A3e-ExcelWriter-Widths'   (Has-Method $tXw 'Widths' @('Double[]'))
    Check 'A3f-ExcelWriter-FreezeHeaders' (Has-Method $tXw 'FreezeHeaders' @('Boolean'))
    Check 'A3g-Report-ExportStaffDetailExcel' (Has-Method $tRep 'ExportStaffDetailExcel' @('String','String','String','String'))
    Check 'A3g2-Comm-ExportCommissionExcel'   (Has-Method $tComm 'ExportCommissionExcel' @('String','String'))
    Check 'A3h-Sys-OpenPath'         (Has-Method $tSys 'OpenPath' @('String'))
    Check 'A3i-Perm-AccountPerms'    (Has-Method $tPerm 'AccountPerms' @('Int32'))
    Check 'A3j-Perm-SetAccountPerms' (Has-Method $tPerm 'SetAccountPerms' @('Int32','String[]','String'))
    Check 'A3k-Perm-MyPerms'         (Has-Method $tPerm 'MyPerms' @())
    Check 'A3l-Perm-SaveAccount-int' (Has-Method $tPerm 'SaveAccount' @('Int32','String','String','String','String','Boolean','String'))
    $enumS = $tXw.GetNestedType('S', [System.Reflection.BindingFlags]'Public,NonPublic')
    Check 'A3m-ExcelWriter-style-enum' ($null -ne $enumS -and $enumS.GetEnumNames().Length -eq 6)

    # A4: StaffList surfaces the Custom flag and matches staff_perms on disk
    $rows = $permSvc.StaffList()
    Check 'A4-stafflist-nonempty' ($rows.Count -ge 1)
    $connA = [MemberManager.DAL.Db]::OpenConnection()
    try {
        $customRows = 0; $wrongCustom = 0
        foreach ($row in $rows) {
            $cmd = $connA.CreateCommand()
            $cmd.CommandText = "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id"
            $p = $cmd.CreateParameter(); $p.ParameterName = '@id'; $p.Value = [int]$row.Id; $cmd.Parameters.Add($p) | Out-Null
            $n = [int]$cmd.ExecuteScalar()
            if ($n -gt 0) { $customRows++ }
            $expect = ($n -gt 0)
            if ([bool]$row.Custom -ne $expect) { $wrongCustom++; Log ('  custom mismatch id=' + $row.Id + ' flagged=' + $row.Custom + ' rows=' + $n) }
        }
        Check ('A4b-custom-flag-matches-' + $rows.Count) ($wrongCustom -eq 0)
        Log ('  accounts with per-person perms on the real db: ' + $customRows)
    } finally { $connA.Dispose() }

    # A5: AccountPerms on the real db answers for every staff row, 10 modules each
    $badShape = 0
    foreach ($row in $rows) {
        try {
            $d = $permSvc.AccountPerms([int]$row.Id)
            if ($d.Modules.Count -ne $modCount -or $d.Allowed.Count -ne $modCount) { $badShape++; Log ('  shape wrong id=' + $row.Id) }
            foreach ($m in $allModules) {
                if (-not $d.Allowed.ContainsKey($m)) { $badShape++; Log ('  missing key: ' + $m) }
            }
            if ($mRefund -notin $d.Allowed.Keys) { $badShape++ }
        } catch { $badShape++; Log ('  AccountPerms threw id=' + $row.Id + ': ' + $_.Exception.Message) }
    }
    Check ('A5-accounperms-shape-' + $rows.Count) ($badShape -eq 0)

    # A6: manager always resolves to all ten modules (never lockable out)
    $mgrRow = $null
    foreach ($row in $rows) { if ([string]$row.Role -eq $rMgr) { $mgrRow = $row } }
    if ($null -ne $mgrRow) {
        $md = $permSvc.AccountPerms([int]$mgrRow.Id)
        $allTrue = $true
        foreach ($m in $allModules) { if (-not $md.Allowed[$m]) { $allTrue = $false } }
        Check 'A6-manager-all-modules' $allTrue
        Check 'A6b-manager-not-custom' ($md.Custom -eq $false)
    } else { Check 'A6-manager-all-modules' $false; Log '  no manager row found' }

    # A7: nobody but the manager holds the refund module (real-data invariant)
    $viol = 0
    foreach ($row in $rows) {
        if ([string]$row.Role -eq $rMgr) { continue }
        $cmd = [MemberManager.DAL.Db]::OpenConnection().CreateCommand()
        $cmd.CommandText = "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id AND module=@m AND allowed=1"
        $p1 = $cmd.CreateParameter(); $p1.ParameterName = '@id'; $p1.Value = [int]$row.Id; $cmd.Parameters.Add($p1) | Out-Null
        $p2 = $cmd.CreateParameter(); $p2.ParameterName = '@m'; $p2.Value = $mRefund; $cmd.Parameters.Add($p2) | Out-Null
        if ([int]$cmd.ExecuteScalar() -gt 0) { $viol++ }
    }
    Check 'A7-no-nonmanager-refund' ($viol -eq 0)

    # ================= Phase B: temp db, full write path =================
    Log '--- Phase B: write tests on throwaway temp db ---'
    $tempRoot = Join-Path $env:TEMP ('mm_permexp_' + [Guid]::NewGuid().ToString('N'))
    $tempData = Join-Path $tempRoot 'data'
    New-Item -ItemType Directory -Path $tempData -Force | Out-Null
    $fRoot = [MemberManager.Common.AppPaths].GetField('_rootDir', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fRoot.SetValue($null, $tempRoot)
    $fPath.SetValue($null, (Join-Path $tempData 'member.db'))
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $rMgr)
    Log ('  temp root: ' + $tempRoot + ' | portable=' + [MemberManager.DAL.Db]::Portable)

    # B1: table created on a fresh db
    $t1 = [int](Scalar "SELECT COUNT(*) FROM pragma_table_info('staff_perms')" $null)
    Check 'B1-staff_perms-created' ($t1 -ge 4)

    # B2: brand new cashier account gets the minimal default grant
    $staffSvc = New-Object MemberManager.BLL.PermissionService
    $newId = [int]($staffSvc.SaveAccount(0, 'probeCash', $rCash, '', '123456', $true, 'probe'))
    Check 'B2-saveaccount-returns-id' ($newId -gt 0)
    $nRows = [int](Scalar "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id" @{ '@id' = $newId })
    Check 'B3a-default-module-rows' ($nRows -eq $modCount)
    $d = $permSvc.AccountPerms($newId)
    Check 'B3b-default-custom-true' ($d.Custom -eq $true)
    Check 'B3c-default-has-members' ($d.Allowed[$mMember] -eq $true)
    Check 'B3d-default-has-pos'     ($d.Allowed[$mCash] -eq $true)
    Check 'B3e-default-no-refund'   ($d.Allowed[$mRefund] -eq $false)
    Check 'B3f-default-no-perms'    ($d.Allowed[$mPerms] -eq $false)
    Check 'B3g-default-no-backup'   ($d.Allowed[$mBackup] -eq $false)

    # B3: SetAccountPerms is a full overwrite (see what you tick)
    $keep = @($mMember, $mCash, $mCard, $mReport)
    $permSvc.SetAccountPerms($newId, $keep, 'probe')
    $d2 = $permSvc.AccountPerms($newId)
    $onCount = 0
    foreach ($m in $allModules) { if ($d2.Allowed[$m]) { $onCount++ } }
    Check 'B4a-overwrite-count-4' ($onCount -eq 4)
    $keptOk = $true
    foreach ($m in $keep) { if (-not $d2.Allowed[$m]) { $keptOk = $false } }
    Check 'B4b-overwrite-kept' $keptOk
    $nRows2 = [int](Scalar "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id" @{ '@id' = $newId })
    Check 'B4c-overwrite-still-module-rows' ($nRows2 -eq $modCount)
    Check 'B4d-overwrite-dropped-backup' (-not $d2.Allowed[$mBackup])

    # B4: un-tick everything - a cashier with zero modules is legal
    $permSvc.SetAccountPerms($newId, @(), 'probe')
    $d3 = $permSvc.AccountPerms($newId)
    $zeroOk = $true
    foreach ($m in $allModules) { if ($d3.Allowed[$m]) { $zeroOk = $false } }
    Check 'B5a-empty-grant-all-off' $zeroOk
    Check 'B5b-empty-grant-still-custom' ($d3.Custom -eq $true)

    # B5: the refund module can never be granted to a non-manager
    $withRefund = @($mMember, $mRefund)
    Check 'B6a-refund-grant-rejected' (Expect-Biz { $permSvc.SetAccountPerms($newId, $withRefund, 'probe') } 'refund-grant')
    Check 'B6b-refund-roleperm-rejected' (Expect-Biz { $permSvc.SetPerm($rCash, $mRefund, $true) } 'refund-roleperm')
    Check 'B6c-manager-roleperm-locked'  (Expect-Biz { $permSvc.SetPerm($rMgr, $mPerms, $false) } 'manager-roleperm')

    # B6: SetPerm on a role still works for an ordinary module
    $permSvc.SetPerm($rCash, $mReport, $true)
    $roleOn = [int](Scalar "SELECT allowed FROM role_perms WHERE role=@r AND module=@m" @{ '@r' = $rCash; '@m' = $mReport })
    Check 'B7-roleperm-writes' ($roleOn -eq 1)

    # B7: MyPerms reflects the per-person grant, not the role default
    [MemberManager.Common.Session]::SignIn($newId, 'probeCash', $rCash)
    $mine = $permSvc.MyPerms()
    Check 'B8a-myperms-empty-after-ungrant' ($mine.Count -eq 0)
    [MemberManager.Common.Session]::SignIn($newId, 'probeCash', $rCash)
    $permSvc.SetAccountPerms($newId, $keep, 'probe')
    $mine2 = $permSvc.MyPerms()
    Check 'B8b-myperms-4-modules' ($mine2.Count -eq 4)
    $mineOk = $true
    foreach ($m in $keep) { if ($mine2 -notcontains $m) { $mineOk = $false } }
    Check 'B8c-myperms-exact-set' $mineOk
    Check 'B8d-myperms-no-refund' ($mine2 -notcontains $mRefund)

    # B8: manager session returns all ten; signed-out returns nothing
    [MemberManager.Common.Session]::SignIn(1, 'probe', $rMgr)
    $mine3 = $permSvc.MyPerms()
    Check 'B9-manager-myperms-all' ($mine3.Count -eq $modCount)
    [MemberManager.Common.Session]::SignOut()
    Check 'B10-signedout-myperms-empty' ($permSvc.MyPerms().Count -eq 0)

    # B9: a manager account has no staff_perms rows -> Custom=false, role default wins
    [MemberManager.Common.Session]::SignIn(1, 'probe', $rMgr)
    $dMgr = $permSvc.AccountPerms(1)
    Check 'B11a-manager-not-custom' ($dMgr.Custom -eq $false)
    $allTrue = $true
    foreach ($m in $allModules) { if (-not $dMgr.Allowed[$m]) { $allTrue = $false } }
    Check 'B11b-manager-falls-back-to-all' $allTrue

    # B10: an OLD account with no staff_perms rows falls back to its role_perms
    $oldId = [int]($staffSvc.SaveAccount(0, 'probeLegacy', $rCash, '', '123456', $true, 'probe'))
    $cmdDel = [MemberManager.DAL.Db]::OpenConnection().CreateCommand()
    $cmdDel.CommandText = "DELETE FROM staff_perms WHERE staff_id=@id"
    $p = $cmdDel.CreateParameter(); $p.ParameterName = '@id'; $p.Value = [int]$oldId; $cmdDel.Parameters.Add($p) | Out-Null
    $cmdDel.ExecuteNonQuery()
    [MemberManager.DAL.Db]::OpenConnection().Dispose()
    $permSvc.SetPerm($rCash, $mBackup, $true)
    [MemberManager.Common.Session]::SignIn($oldId, 'probeLegacy', $rCash)
    $mineOld = $permSvc.MyPerms()
    $dl = $permSvc.AccountPerms($oldId)
    Check 'B12a-legacy-not-custom' ($dl.Custom -eq $false)
    Check 'B12b-legacy-inherits-roleperm' ($mineOld -contains $mBackup)

    # B11: SaveAccount rejects bad input (guardrails still intact after the int change)
    Check 'B13a-saveaccount-empty-name' (Expect-Biz { $staffSvc.SaveAccount(0, '  ', $rCash, '', '123456', $true, 'probe') } 'empty-name')
    Check 'B13b-saveaccount-no-password' (Expect-Biz { $staffSvc.SaveAccount(0, 'probeX', $rCash, '', '', $true, 'probe') } 'no-password')
    Check 'B13c-saveaccount-bad-role' (Expect-Biz { $staffSvc.SaveAccount(0, 'probeY', $rTech + 'X', '', '123456', $true, 'probe') } 'bad-role')
    Check 'B13d-accounperms-no-such-id' (Expect-Biz { $permSvc.AccountPerms(99999) } 'no-such-id')
    Check 'B13e-setaccounperms-no-such-id' (Expect-Biz { $permSvc.SetAccountPerms(99999, @($mMember), 'probe') } 'no-such-id-set')

    # B12: seed a recharge + a real sale so every export sheet carries data
    [MemberManager.Common.Session]::SignIn(1, 'probe', $rMgr)
    $ms2 = New-Object MemberManager.BLL.MemberService
    $sv2 = New-Object MemberManager.BLL.StoredValueService
    $pos2 = New-Object MemberManager.BLL.PosService
    $mem = $ms2.CreateMember('ExpMember', '13800009001', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $memId = [int]$mem.Id
    Check 'B13f-seed-member' ($memId -gt 0)
    $ci = [MemberManager.DAL.Db]::OpenConnection()
    $cmd = $ci.CreateCommand(); $cmd.CommandText = 'SELECT price FROM service_items ORDER BY id'
    $rd = $cmd.ExecuteReader(); $pr = @()
    while ($rd.Read()) { $pr += [double]$rd.GetDouble(0) }
    $rd.Close(); $ci.Dispose()
    Check 'B13g-seed-items-6' ($pr.Count -ge 6)
    $sv2.Recharge($memId, [decimal]300, [decimal]50, $cashCn, 'probe')
    Check 'B13h-seed-recharge-2-flows' ([int](Scalar 'SELECT COUNT(*) FROM member_flows WHERE member_id=@id' @{ '@id' = $memId }) -eq 2)
    $payable = [decimal]([double]$pr[0] + 2 * [double]$pr[3])
    $cl = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $l1 = New-Object 'MemberManager.BLL.PosService+CartLine'; $l1.ItemId = 1; $l1.Qty = 1; $l1.ByCard = $false
    $l2 = New-Object 'MemberManager.BLL.PosService+CartLine'; $l2.ItemId = 4; $l2.Qty = 2; $l2.ByCard = $false
    $null = $cl.Add($l1); $null = $cl.Add($l2)
    $co = $pos2.Checkout($cl, [System.Nullable[int]]$memId, [decimal]0, $payable, $null, [System.Nullable[int]]1)
    Check 'B13i-seed-sale-order' ($null -ne $co -and [string]$co.OrderNo -like 'D*')
    Check 'B13j-seed-sale-receivable' ([double]$co.Receivable -eq [double]$payable)
    $normalCount = [int](Scalar ("SELECT COUNT(*) FROM orders WHERE status='" + $stNormal + "' AND kind<>'" + $tRecharge + "'") $null)
    Check 'B13k-sale-landed-on-db' ($normalCount -ge 1)
    Check 'B13l-payment-landed-on-db' ([int](Scalar 'SELECT COUNT(*) FROM payments' $null) -ge 2)

    # B14: Excel export - both LIVE methods, then structural validation.
    # (The old 4-sheet ReportService.ExportExcel was removed in Task D; the live
    #  surface is ReportService.ExportStaffDetailExcel + CommissionService.ExportCommissionExcel.)
    $rep  = New-Object MemberManager.BLL.ReportService
    $comm = New-Object MemberManager.BLL.CommissionService
    $expDir = Join-Path $tempData 'exports'
    $mm = (Get-Date).ToString('yyyy-MM')
    # staff detail pivot export (single sheet)
    $resStaff = $rep.ExportStaffDetailExcel(($mm + '-01'), ($mm + '-28'), 'probe', $expDir)
    $sPath = [string]$resStaff.Path
    Check 'B14a-staffdetail-exists' ([System.IO.File]::Exists($sPath))
    Check 'B14b-staffdetail-size' ([int64](Get-Item $sPath).Length -gt 0)
    Check 'B14c-staffdetail-under-datadir' ([string]$resStaff.Path -like ($expDir + '*'))
    Check 'B14d-staffdetail-name' ([string]$resStaff.Name -match '\.xlsx$')
    Check 'B14e-staffdetail-period' (-not [string]::IsNullOrWhiteSpace([string]$resStaff.Period))
    # commission export (two sheets: detail + rank)
    $resComm = $comm.ExportCommissionExcel($mm, 'probe', $expDir)
    $cPath = [string]$resComm.Path
    Check 'B14f-comm-exists' ([System.IO.File]::Exists($cPath))
    Check 'B14g-comm-size' ([int64](Get-Item $cPath).Length -gt 0)
    Check 'B14h-comm-under-datadir' ([string]$resComm.Path -like ($expDir + '*'))
    Check 'B14i-comm-name' ([string]$resComm.Name -match '\.xlsx$')
    Check 'B14j-comm-period' ([string]$resComm.Period -eq $mm)

    # ---- structural validation of the commission workbook (2 sheets) ----
    $wb = $cPath
    Log ('  validating workbook: ' + $wb)
    $entryNames = New-Object 'System.Collections.Generic.List[string]'
    $xmls = @{}
    $zip = [System.IO.Compression.ZipFile]::OpenRead($wb)
    try {
        foreach ($en in $zip.Entries) {
            $entryNames.Add($en.FullName) | Out-Null
            $sr = New-Object System.IO.StreamReader($en.Open(), [System.Text.Encoding]::UTF8)
            $xmls[$en.FullName] = $sr.ReadToEnd()
            $sr.Close()
        }
    } finally { $zip.Dispose() }
    $need = @('[Content_Types].xml','_rels/.rels','xl/workbook.xml','xl/_rels/workbook.xml.rels','xl/styles.xml',
              'xl/worksheets/sheet1.xml','xl/worksheets/sheet2.xml')
    $missing = 0
    foreach ($n in $need) { if (-not $entryNames.Contains($n)) { $missing++; Log ('  missing entry: ' + $n) } }
    Check 'B15a-xlsx-7-entries' ($missing -eq 0)
    Check 'B15b-no-sharedstrings' (-not $entryNames.Contains('xl/sharedStrings.xml'))

    # every xml part must be well formed
    $badXml = 0
    foreach ($k in $xmls.Keys) {
        try { $xd = New-Object System.Xml.XmlDocument; $xd.LoadXml($xmls[$k]) }
        catch { $badXml++; Log ('  bad xml: ' + $k + ' -> ' + $_.Exception.Message) }
    }
    Check ('B15c-all-xml-wellformed-' + $xmls.Count) ($badXml -eq 0)

    # workbook declares the two commission sheets, in the right order
    $xwb = New-Object System.Xml.XmlDocument; $xwb.LoadXml($xmls['xl/workbook.xml'])
    $sheets = $xwb.GetElementsByTagName('sheet')
    Check 'B16a-workbook-2-sheets' ($sheets.Count -eq 2)
    $names = @()
    foreach ($s in $sheets) { $names += $s.GetAttribute('name') }
    $sComm1 = CStr @(0x63D0,0x6210,0x660E,0x7EC6)   # sheet1: commission detail
    $sComm2 = CStr @(0x63D0,0x6210,0x6392,0x540D)   # sheet2: commission rank
    Check 'B16b-sheet-order' (($names -join '|') -eq ($sComm1 + '|' + $sComm2))

    # content types declare both sheet parts
    $ct = $xmls['[Content_Types].xml']
    $ctOk = $true
    for ($i = 1; $i -le 2; $i++) { if ($ct -notlike ('*xl/worksheets/sheet' + $i + '.xml*')) { $ctOk = $false } }
    Check 'B16c-content-types-all-sheets' $ctOk

    # styles: exactly 6 cellXfs, what the S enum promises
    $xst = New-Object System.Xml.XmlDocument; $xst.LoadXml($xmls['xl/styles.xml'])
    $cellXfs = $xst.GetElementsByTagName('cellXfs')
    $nXf = 0
    if ($cellXfs.Count -eq 1) { $nXf = [int]$cellXfs.Item(0).ChildNodes.Count }
    Check 'B17-styles-6-cellxfs' ($nXf -eq 6)

    # freeze pane: on for both commission sheets (FreezeHeaders(true))
    $frozen = $true
    foreach ($i in 1..2) { if ($xmls['xl/worksheets/sheet' + $i + '.xml'] -notlike '*ySplit="1"*') { $frozen = $false } }
    Check 'B18a-freeze-commission-sheets' $frozen

    # text is inline (no sharedStrings dependency) and money cells carry the money style.
    # union both sheets so the check holds regardless of which sheet holds the data.
    $allXml = ($xmls['xl/worksheets/sheet1.xml'] + $xmls['xl/worksheets/sheet2.xml'])
    Check 'B19a-inlinestr-used' ($allXml -like '*<is><t*')
    Check 'B19b-money-style-applied' ($allXml -match 's="2"')
    Check 'B19c-number-as-v' ($allXml -match '<v>')
    Check 'B19d-header-style-applied' ($allXml -match 's="1"')
    $cols = ($xmls['xl/worksheets/sheet1.xml'] -match '<cols>') -and ($xmls['xl/worksheets/sheet1.xml'] -match '<col ')
    Check 'B19e-col-widths-written' $cols

    # B13: OpenPath guard - accept only inside the data dir (no app is launched)
    $sysi = New-Object MemberManager.BLL.SystemInfoService
    $outside = Join-Path $tempRoot 'outside.xlsx'
    Check 'B20a-openpath-empty' (Expect-Biz { $sysi.OpenPath('') } 'empty')
    Check 'B20b-openpath-outside-datadir' (Expect-Biz { $sysi.OpenPath($outside) } 'outside')
    Check 'B20c-openpath-dotdot' (Expect-Biz { $sysi.OpenPath((Join-Path $tempRoot '..\outside2.xlsx')) } 'dotdot')
    # a missing file INSIDE the data dir gets past the prefix guard and fails on existence ->
    # proves the guard allows data-dir paths without launching anything.
    $insideMissing = Join-Path $tempData 'never-created.xlsx'
    try { $sysi.OpenPath($insideMissing); $passed = $false; Log '  openpath-inside-missing: no throw' }
    catch {
        $msg = $_.Exception.Message
        if ($null -ne $_.Exception.InnerException) { $msg = $_.Exception.InnerException.Message }
        Check 'B20d-openpath-inside-past-guard' ($msg -notlike ('*' + (CStr @(0x6570,0x636E)) + '*'))
    }

    # B15: audit trail
    $sqlPerm = "SELECT COUNT(*) FROM op_logs WHERE module='" + $mPerms + "'"
    $sqlRep  = "SELECT COUNT(*) FROM op_logs WHERE module='" + $mReport + "'"
    $audPerm = [int](Scalar $sqlPerm $null)
    $audRep  = [int](Scalar $sqlRep $null)
    Check 'B21a-perm-actions-audited' ($audPerm -ge 6)
    Check 'B21b-export-audited-2' ($audRep -eq 2)

    # keep one workbook for an independent (openpyxl) cross-check
    $keepCopy = Join-Path $PSScriptRoot '_verify.xlsx'
    if (Test-Path $keepCopy) { Remove-Item $keepCopy -Confirm:$false }
    Copy-Item $wb $keepCopy
    Log ('  kept for external validation: ' + $keepCopy + ' (' + (Get-Item $keepCopy).Length + ' bytes)')

    # ---- cleanup ----
    try { Remove-Item -Recurse -Force $tempRoot -Confirm:$false } catch { }
    Log '  temp dir cleaned'
}
catch {
    Log ('ERROR: ' + $_.Exception.Message)
    if ($null -ne $_.InvocationInfo) { Log ('  at: ' + $_.InvocationInfo.PositionMessage) }
    if ($null -ne $_.ScriptStackTrace) { Log ('  trace: ' + ($_.ScriptStackTrace -replace '\r\n', ' | ')) }
    $script:Fail++
}
finally {
    Log ('FAILURES=' + $script:Fail)
    Save-Log
    if ($script:Fail -eq 0) { Write-Output 'SMOKE PASS' } else { Write-Output ('SMOKE FAIL ' + $script:Fail) }
    exit $script:Fail
}

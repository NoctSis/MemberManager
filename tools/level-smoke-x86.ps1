# ==============================================================
#  Member level policy (member_levels) smoke test - x86 host required (SysWOW64).
#  Pure ASCII source. Chinese strings are built from Unicode code points.
#
#  Phase A: read-only queries against the REAL database.
#  Phase B: full write-path tests against a THROWAWAY temp db
#           (Db._dbPath redirected via reflection) - the real
#           database is never written by this script.
#  Usage:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ^
#     -NoProfile -ExecutionPolicy Bypass -File tools\level-smoke-x86.ps1
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$script:LogPath = Join-Path $PSScriptRoot '_level_smoke.log'
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
    Log ('Host: ' + [System.IntPtr]::Size * 8 + '-bit, dist loaded')

    # ---- runtime Chinese strings ----
    $lVk = CStr @(0x666E,0x5361)             # seeded level: plain card
    $lSl = CStr @(0x94F6,0x5361)             # seeded level: silver card
    $lGd = CStr @(0x91D1,0x5361)             # seeded level: gold card
    $lDm = CStr @(0x94BB,0x77F3,0x5361)      # seeded level: diamond card
    $lNew  = CStr @(0x9496,0x94F6,0x5361)    # test rename target: platinum card
    $lExtra = CStr @(0x81F3,0x5003,0x5361)   # test extra level: supreme
    $lDup2  = CStr @(0x6D4B,0x9A8C,0x5361)   # throwaway name for the validation checks
    $tRecharge = CStr @(0x5145,0x503C)       # flow type: recharge
    $cashCn = CStr @(0x73B0,0x91D1)          # pay method: cash
    $tLevel = CStr @(0x7B49,0x7EA7)          # level (substring of op_logs actions)
    $mgrRole = CStr @(0x5E97,0x957F)         # role: manager
    $sRefunded = CStr @(0x5DF2,0x9000,0x6B3E) # order status: refunded

    $lv = New-Object MemberManager.BLL.LevelService

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

    # Mirror of LevelRules.Resolve (backend is authoritative; this is a cross-check).
    # Hand-rolled sort: PS 5.1 Sort-Object rejects two positional hashtable sort keys
    # with "position form parameter" Hashtable mismatch.
    function Resolve-Level([object[]]$defs, [double]$total) {
        $sorted = New-Object 'System.Collections.ArrayList'
        foreach ($d in $defs) { $null = $sorted.Add($d) }
        for ($i = 0; $i -lt $sorted.Count; $i++) {
            for ($j = 0; $j -lt ($sorted.Count - $i - 1); $j++) {
                $a = [double]$sorted[$j].MinRecharge
                $b = [double]$sorted[$j + 1].MinRecharge
                if ($a -gt $b -or ($a -eq $b -and [int]$sorted[$j].Id -gt [int]$sorted[$j + 1].Id)) {
                    $t = $sorted[$j]; $sorted[$j] = $sorted[$j + 1]; $sorted[$j + 1] = $t
                }
            }
        }
        $floor = $null; $best = $null
        foreach ($d in $sorted) {
            if ($null -eq $floor) { $floor = $d }
            if ($d.Enabled -eq $true -and $total -ge [double]$d.MinRecharge) { $best = $d }
        }
        $r = $best
        if ($null -eq $r) { $r = $floor }
        if ($null -eq $r) { return '' }
        return [string]$r.Name
    }

    # ================= Phase A: real db, read-only =================
    # Pin the DAL to the app's own database (portable mode: dist\data\member.db).
    # AppPaths resolves relative to the EXE directory; inside a PowerShell host that
    # falls back to the user's %LocalAppData% and would read the wrong file.
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $realDb = Join-Path $dist 'data\member.db'
    $fPath.SetValue($null, $realDb)
    Log ('--- Phase A: read-only on real db (' + $realDb + ') ---')
    $list = $lv.List()
    Check 'A1-levels-ge1' ($list.Count -ge 1)
    $namesOk = $true; $thrOk = $true; $dupThr = $false
    $seenThr = @{}
    foreach ($x in $list) {
        if ([string]::IsNullOrWhiteSpace($x.Name)) { $namesOk = $false }
        if ([double]$x.MinRecharge -lt 0) { $thrOk = $false }
        $k = [double]$x.MinRecharge
        if ($seenThr.ContainsKey($k)) { $dupThr = $true }
        $seenThr[$k] = $true
    }
    Check 'A2-names-nonempty' $namesOk
    Check 'A3-thresholds-nonnegative' $thrOk
    Check 'A3b-thresholds-unique' ($dupThr -eq $false)

    # A rename or delete must never leave a stale level name behind on any member.
    $orphan = [int](Scalar "SELECT COUNT(*) FROM members m WHERE m.level NOT IN (SELECT name FROM member_levels)" $null)
    Check 'A4-no-stale-levels-on-members' ($orphan -eq 0)

    # Every member's stored level must equal resolve(cumulative recharge).
    $mism = 0; $checked = 0
    $connA = [MemberManager.DAL.Db]::OpenConnection()
    try {
        $cmd = $connA.CreateCommand()
        $cmd.CommandText = "SELECT m.id, IFNULL(m.level,''), COALESCE((SELECT SUM(f.amount) FROM member_flows f WHERE f.member_id=m.id AND f.type='" + $tRecharge + "'),0) FROM members m"
        $r = $cmd.ExecuteReader()
        while ($r.Read()) {
            $checked++
            $got = [string]$r.GetValue(1)
            $tot = [double]$r.GetValue(2)
            $exp = Resolve-Level $list $tot
            if ($got -ne $exp) {
                $mism++
                if ($mism -le 3) { Log ('  mismatch id=' + $r.GetValue(0) + ' got=' + $got + ' exp=' + $exp + ' total=' + $tot) }
            }
        }
        $r.Close()
    } finally { $connA.Dispose() }
    Check ('A5-levels-match-cumulative-' + $checked) ($mism -eq 0)

    # ================= Phase B: temp db, full write path =================
    Log '--- Phase B: write tests on throwaway temp db ---'
    $tempDb = Join-Path $env:TEMP ('mm_level_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)

    function LevelOf([int]$id) { return [string](Scalar 'SELECT level FROM members WHERE id=@id' @{ '@id' = $id }) }
    function CumOf([int]$id) {
        # 10-14: cumulative is now NET of refunds (recharge sum minus refunded recharge orders),
        # mirroring LevelRules.CumulativeRecharge. Keep ASCII-only: status via $sRefunded.
        return [double](Scalar ("SELECT" +
            " (SELECT COALESCE(SUM(amount),0) FROM member_flows WHERE member_id=@id AND type='" + $tRecharge + "')" +
            " - (SELECT COALESCE(SUM(receivable),0) FROM orders WHERE member_id=@id AND kind='" + $tRecharge + "' AND status='" + $sRefunded + "')") @{ '@id' = $id })
    }

    $seeded = $lv.List()
    Check 'B0-seeded-4-levels' ($seeded.Count -eq 4)
    $thr = @{}
    $idByName = @{}
    foreach ($x in $seeded) { $thr[[string]$x.Name] = [double]$x.MinRecharge; $idByName[[string]$x.Name] = [int]$x.Id }
    Check 'B0b-plain-floor-0' ($thr[$lVk] -eq 0)
    Check 'B0c-silver-1000' ($thr[$lSl] -eq 1000)
    Check 'B0d-gold-5000' ($thr[$lGd] -eq 5000)
    Check 'B0e-diamond-10000' ($thr[$lDm] -eq 10000)

    $ms = New-Object MemberManager.BLL.MemberService
    $sv = New-Object MemberManager.BLL.StoredValueService

    # --- auto determination on create ---
    $m1 = $ms.CreateMember('LvlZero', '13800001001', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $dbg = LevelOf $m1.Id
    Log ('  diag m1.level raw type=' + $dbg.GetType().FullName + ' len=' + $dbg.Length + ' cp=' + (($dbg.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ','))
    Check 'B1-init0-falls-to-floor' ($dbg -eq $lVk)

    $m2 = $ms.CreateMember('LvlSilver', '13800001002', '', '', '', [decimal]1000, [decimal]0, 1, 'probe')
    $dbg2 = LevelOf $m2.Id
    Log ('  diag m2.level raw cp=' + (($dbg2.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ','))
    Check 'B2-init1000-exactly-silver' ($dbg2 -eq $lSl)

    $m3 = $ms.CreateMember('LvlDiamond', '13800001003', '', '', '', [decimal]25000, [decimal]0, 1, 'probe')
    $dbg3 = LevelOf $m3.Id
    Log ('  diag m3.level raw cp=' + (($dbg3.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ','))
    Check 'B3-init25000-diamond' ($dbg3 -eq $lDm)
    Check 'B3b-row-exposes-totalrecharge' ([double]$m3.TotalRecharge -eq 25000)

    # --- policy change: rename propagates to every member sitting on that level ---
    $lv.Save($idByName[$lSl], $lNew, [decimal]1000, $true)
    Check 'B4a-rename-propagated' ((LevelOf $m2.Id) -eq $lNew)
    $onOld = [int](Scalar "SELECT COUNT(*) FROM members WHERE level=@n" @{ '@n' = $lSl })
    Check 'B4b-no-member-keeps-old-name' ($onOld -eq 0)

    # --- policy change: raising the threshold pushes members back down ---
    $lv.Save($idByName[$lSl], $lNew, [decimal]2000, $true)
    Check 'B5a-threshold-raise-demotes' ((LevelOf $m2.Id) -eq $lVk)
    $m5 = $ms.CreateMember('LvlMid', '13800001005', '', '', '', [decimal]1500, [decimal]0, 1, 'probe')
    Check 'B5b-below-raise-stays-floor' ((LevelOf $m5.Id) -eq $lVk)

    # --- recharge upgrades by crossing a threshold ---
    $sv.Recharge($m2.Id, [decimal]5000, [decimal]0, $cashCn, 'probe')
    Check 'B6a-recharge-crosses-to-gold' ((LevelOf $m2.Id) -eq $lGd)
    Check 'B6b-cumulative-6000' ([double](CumOf $m2.Id) -eq 6000)

    # --- gifts never count toward the threshold ---
    $m4 = $ms.CreateMember('LvlGift', '13800001004', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $sv.Recharge($m4.Id, [decimal]500, [decimal]9000, $cashCn, 'probe')
    Check 'B7a-cumulative-excludes-gift' ([double](CumOf $m4.Id) -eq 500)
    Check 'B7b-gift-alone-no-upgrade' ((LevelOf $m4.Id) -eq $lVk)

    # --- 10-14: refund now claws back cumulative recharge and CAN demote the level (owner: "refund must reduce") ---
    $sv.Recharge($m4.Id, [decimal]5000, [decimal]0, $cashCn, 'probe')
    Check 'B8-pre-cumulative-5500' ([double](CumOf $m4.Id) -eq 5500)
    Check 'B8-pre-level-gold' ((LevelOf $m4.Id) -eq $lGd)
    $sv.Refund($m4.Id, [decimal]5000, [decimal]0, 'smoke', 'probe')
    Check 'B8a-refund-nets-cumulative' ([double](CumOf $m4.Id) -eq 500)
    Check 'B8b-refund-demotes-level' ((LevelOf $m4.Id) -eq $lVk)

    # --- policy change: disabling a level stops it being used ---
    $lv.Save($idByName[$lDm], $lDm, [decimal]10000, $false)
    Check 'B9-disable-diamond-demotes' ((LevelOf $m3.Id) -eq $lGd)

    # --- add a brand new top level ---
    $lv.Save(0, $lExtra, [decimal]20000, $true)
    Check 'B10a-add-high-level' ((LevelOf $m3.Id) -eq $lExtra)

    # --- validation: duplicate name / duplicate threshold / bad input rejected ---
    Check 'B11a-duplicate-name-rejected' (Expect-Biz { $lv.Save(0, $lExtra, [decimal]40000, $true) } 'dup-name')
    Check 'B11b-duplicate-threshold-rejected' (Expect-Biz { $lv.Save(0, $lDup2, [decimal]10000, $true) } 'dup-threshold')
    Check 'B11c-negative-threshold-rejected' (Expect-Biz { $lv.Save(0, $lDup2, [decimal](-1), $true) } 'negative')
    Check 'B11d-empty-name-rejected' (Expect-Biz { $lv.Save(0, '   ', [decimal]41000, $true) } 'empty-name')

    # --- delete a level: the remaining policy decides who stays where ---
    $extraId = 0
    foreach ($x in $lv.List()) { if ([string]$x.Name -eq $lExtra) { $extraId = [int]$x.Id } }
    Check 'B12a-extra-level-found' ($extraId -gt 0)
    $lv.Delete($extraId)
    Check 'B12b-delete-demotes-to-gold' ((LevelOf $m3.Id) -eq $lGd)

    # --- can never delete the last remaining level ---
    $ids = @()
    foreach ($x in $lv.List()) { $ids += [int]$x.Id }
    for ($i = 0; $i -lt $ids.Count - 1; $i++) { $lv.Delete($ids[$i]) }
    Check 'B13a-only-one-left' (($lv.List()).Count -eq 1)
    $lastId = [int]((($lv.List())[0]).Id)
    Check 'B13b-cannot-delete-last' (Expect-Biz { $lv.Delete($lastId) } 'delete-last')

    # --- startup alignment is idempotent: re-initialising must not re-seed ---
    $cntBefore = ($lv.List()).Count
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    $cntAfter = ($lv.List()).Count
    Check 'B14-idempotent-no-reseed' ($cntBefore -eq $cntAfter)

    # --- policy changes are auditable ---
    $audited = [int](Scalar ("SELECT COUNT(*) FROM op_logs WHERE action LIKE '%" + $tLevel + "%'") $null)
    Check 'B15-policy-changes-audited' ($audited -ge 5)

    Log ('  diag survivors: ' + ((($lv.List()) | ForEach-Object { [string]$_.Name + '/' + $_.MinRecharge + '/' + $_.Enabled }) -join ','))
    $staleRows = @()
    $cmdSt = [MemberManager.DAL.Db]::OpenConnection().CreateCommand()
    try {
        $cmdSt.CommandText = "SELECT IFNULL(m.level,''), COUNT(*) FROM members m WHERE IFNULL(m.level,'')<>'' AND m.level NOT IN (SELECT name FROM member_levels) GROUP BY m.level"
        $rSt = $cmdSt.ExecuteReader()
        while ($rSt.Read()) { $staleRows += ('[' + $rSt.GetString(0) + ']x' + $rSt.GetInt32(1)) }
        $rSt.Close()
    } finally { $cmdSt.Connection.Dispose() }
    Log ('  diag stale: ' + ($staleRows -join ' '))
    # Blank level is legitimate: when no tier is usable the member simply has none (owner 10-15).
    # What must never happen is a member carrying a level NAME that no longer exists.
    $left = [int](Scalar "SELECT COUNT(*) FROM members WHERE IFNULL(level,'')<>'' AND level NOT IN (SELECT name FROM member_levels)" $null)
    Check 'B16-no-stale-levels-after-mutations' ($left -eq 0)
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

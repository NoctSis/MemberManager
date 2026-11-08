# ==============================================================
#  member-create-recharge smoke (x86 host: SysWOW64 PowerShell)
#
#  Regression: when a member is created WITH an initial recharge, it must
#  produce a recharge order + cash payment, so the transactions screen
#  (which reads orders) can see it and the cash counts into daily revenue.
#  The old implementation only wrote member_flows, so it was invisible and
#  never counted as revenue.
#
#  Runs on a THROWAWAY temp db (Db._dbPath redirected by reflection), never
#  touches the real shop database.
#  Source stays PURE ASCII: PS 5.1 reads BOM-less UTF-8 as ANSI, and any
#  stray multibyte char shifts parsing and silently corrupts the checked
#  strings. Chinese literals are built from char codes only.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_member_recharge_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
function L([string]$m) { $log.Add($m) | Out-Null }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }

$tempDb = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    # ---- point Db at a throwaway file ----
    $tempDb = Join-Path $env:TEMP ('mm_mrecharge_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $f = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $f.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', ([string][char]0x5E97 + [string][char]0x957F))   # shop-manager role
    L ('tempDb: ' + $tempDb)

    $svc = New-Object MemberManager.BLL.MemberService

    # --- M1: create a member WITH initial recharge (500) -> must make a recharge order + cash payment ---
    # signature: (name, phone, birthday, tags, remark, initAmount, createdByStaffId, createdByNameHint) -- level is auto-derived, never passed
    $mem = $svc.CreateMember('SmokeRecharge', '13800000100', '', '', '', [decimal]500, [decimal]0, 1, 'probe')
    Chk 'M1-create-member-returns-row' ($mem -ne $null -and $mem.Id -gt 0)

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function Scalar([string]$sql, [hashtable]$ps) {
        $c = $conn.CreateCommand(); $c.CommandText = $sql
        if ($ps) { foreach ($k in $ps.Keys) { $p = $c.CreateParameter(); $p.ParameterName = $k; $p.Value = $ps[$k]; [void]$c.Parameters.Add($p) } }
        return ,$c.ExecuteScalar()
    }

    $rechargeCn = ([string][char]0x5145 + [string][char]0x503C)   # recharge kind
    # M2: a recharge order row exists for this member (proven shape, mirrors M3)
    $rcv = [decimal](Scalar ("SELECT receivable FROM orders WHERE member_id=@m AND kind='" + $rechargeCn + "'") @{ '@m' = $mem.Id })
    Chk 'M2-recharge-order-created' ($rcv -eq [decimal]500)

    # diagnostics: what did we actually store?
    $anyOrd = [int](Scalar "SELECT COUNT(*) FROM orders WHERE member_id=@m" @{ '@m' = $mem.Id })
    L ('  diag: orders-for-member(no kind filter)=' + $anyOrd)
    $storedKind = [string](Scalar "SELECT kind FROM orders WHERE member_id=@m" @{ '@m' = $mem.Id })
    $storedCodepoints = ($storedKind.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ','
    L ('  diag: stored-kind-len=' + $storedKind.Length + ' codepoints=[' + $storedCodepoints + ']')
    $flowType = [int](Scalar "SELECT COUNT(*) FROM member_flows WHERE member_id=@m" @{ '@m' = $mem.Id })
    L ('  diag: member_flows(no type filter)=' + $flowType)

    # the order must carry the cash into receivable (already asserted as M2 above)
    Chk 'M3-recharge-receivable-500' ($rcv -eq [decimal]500)

    # a cash payment row linked to that order (cash = 0x73B0 0x91D1)
    $cashCn = ([string][char]0x73B0 + [string][char]0x91D1)
    $cashCnt = [int](Scalar ("SELECT COUNT(*) FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.member_id=@m AND p.method=@cash") @{ '@m' = $mem.Id; '@cash' = $cashCn })
    Chk 'M4-cash-payment-500' ($cashCnt -eq 1)

    # member balance must equal the recharge (identity)
    $bal = [decimal](Scalar 'SELECT balance FROM members WHERE id=@id' @{ '@id' = $mem.Id })
    Chk 'M5-balance-equals-recharge' ($bal -eq [decimal]500)

    # a member flow row exists for the recharge
    $flowCnt = [int](Scalar ("SELECT COUNT(*) FROM member_flows WHERE member_id=@m AND type='" + $rechargeCn + "'") @{ '@m' = $mem.Id })
    Chk 'M6-member-flow-written' ($flowCnt -eq 1)

    # --- M7: create a member with ZERO initial recharge -> no recharge order ---
    $mem0 = $svc.CreateMember('SmokeNoCharge', '13800000101', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $noOrd = [int](Scalar ("SELECT COUNT(*) FROM orders WHERE member_id=@m") @{ '@m' = $mem0.Id })
    Chk 'M7-no-charge-no-order' ($noOrd -eq 0)

    $conn.Dispose()

    L ''
    if ($fail -eq 0) { L 'MEMBER-RECHARGE SMOKE OK' } else { L ('MEMBER-RECHARGE SMOKE FAILED: ' + $fail) }
}
catch {
    L ('MEMBER-RECHARGE SMOKE ERROR: ' + $_.Exception.ToString())
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
exit 0

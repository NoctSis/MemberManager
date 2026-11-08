# ==============================================================
#  Historical-backfill smoke for new-member recharge orders (x86 host: SysWOW64)
#
#  Regression: backfill_newmember_recharge_orders (in DatabaseInitializer.EnsureDatabase)
#  must, for legacy "new-member" recharge flows that have NO order yet (order_id IS NULL),
#  create a recharge order + cash payment so they show up in the transactions
#  screen (which reads orders) and count into daily revenue. It must be IDEMPOTENT
#  (recorded in settings, re-runs create nothing new).
#
#  Runs on a THROWAWAY temp db (Db._dbPath redirected). Pure ASCII source (PS 5.1).
#  Chinese literals are built from char codes, never raw UTF-8.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_backfill_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
function L([string]$m) { $log.Add($m) | Out-Null }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }

$tempDb = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_backfill_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $f = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $f.SetValue($null, $tempDb)

    $rechargeCn = ([string][char]0x5145 + [string][char]0x503C)   # recharge kind
    $cashCn     = ([string][char]0x73B0 + [string][char]0x91D1)   # cash method

    # ---- 1st run: DDL + seed + backfill ----
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S1([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }

    # B1: backfill flag is recorded
    $flag = [string](S1 "SELECT value FROM settings WHERE key='backfilled_newmember_flows'")
    Chk 'B1-backfill-flag-set' (-not [string]::IsNullOrEmpty($flag))
    $flagCount = 0
    if (-not [string]::IsNullOrEmpty($flag)) { $flagCount = (@($flag.Split(',') | Where-Object { $_.Trim().Length -gt 0 })).Count }
    L ('  diag: flagIds=' + $flagCount)

    # B2: number of recharge orders == number of distinct backfilled flow ids
    $orderCnt = [int](S1 ("SELECT COUNT(*) FROM orders o WHERE o.kind='" + $rechargeCn + "'"))
    Chk 'B2-recharge-orders-match-flag' ($orderCnt -eq $flagCount -and $flagCount -gt 0)
    L ('  diag: rechargeOrders=' + $orderCnt + ' flagIds=' + $flagCount)

    # B3: every backfilled recharge order has a matching cash payment.
    # Diagnostics wrapped in try/catch so they can never mask the main assert.
    # IFNULL at SQL level guards against NULL method rows.
    $totalRecharge = [int](S1 ("SELECT COUNT(*) FROM orders o WHERE o.kind='" + $rechargeCn + "'"))
    try {
        $dc = $conn.CreateCommand()
        $dc.CommandText = "SELECT IFNULL(p.method,'') FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.kind=@k"
        $dk = $dc.CreateParameter(); $dk.ParameterName = '@k'; $dk.Value = $rechargeCn; [void]$dc.Parameters.Add($dk)
        $dr = $dc.ExecuteReader()
        $parts = New-Object 'System.Collections.Generic.List[string]'
        while ($dr.Read()) {
            $mm = [string]$dr.GetValue(0)
            $cpc = ($mm.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ','
            $parts.Add('[' + $cpc + ']')
        }
        $dr.Dispose(); $dc.Dispose()
        $cashCp = (($cashCn.ToCharArray() | ForEach-Object { '0x{0:X4}' -f [int][char]$_ }) -join ',')
        L ('  diag: cashCn=[' + $cashCp + '] rechargePayMethods=' + ($parts -join ','))
    } catch { L ('  diag: dump-failed: ' + $_.Exception.Message) }

    $cc = $conn.CreateCommand()
    $cc.CommandText = "SELECT COUNT(*) FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.kind=@k AND p.method=@m"
    $pk = $cc.CreateParameter(); $pk.ParameterName = '@k'; $pk.Value = $rechargeCn; [void]$cc.Parameters.Add($pk)
    $pm = $cc.CreateParameter(); $pm.ParameterName = '@m'; $pm.Value = $cashCn; [void]$cc.Parameters.Add($pm)
    $withCash = [int]$cc.ExecuteScalar()
    $cc.Dispose()
    L ('  diag: totalRecharge=' + $totalRecharge + ' withCash=' + $withCash)
    Chk 'B3-backfilled-orders-have-cash' ($totalRecharge -gt 0 -and $withCash -eq $totalRecharge)

    $orderCntBefore = $orderCnt
    $conn.Dispose()

    # ---- 2nd run: idempotency, no duplicate orders ----
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    $conn2 = [MemberManager.DAL.Db]::OpenConnection()
    function S2([string]$sql) { $c = $conn2.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    $orderCnt2 = [int](S2 ("SELECT COUNT(*) FROM orders o WHERE o.kind='" + $rechargeCn + "'"))
    Chk 'B4-idempotent-second-run' ($orderCnt2 -eq $orderCntBefore)
    L ('  diag: orders-before=' + $orderCntBefore + ' after-2nd=' + $orderCnt2)
    $conn2.Dispose()

    L ''
    if ($fail -eq 0) { L 'BACKFILL SMOKE OK' } else { L ('BACKFILL SMOKE FAILED: ' + $fail) }
}
catch {
    L ('BACKFILL SMOKE ERROR: ' + $_.Exception.ToString())
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

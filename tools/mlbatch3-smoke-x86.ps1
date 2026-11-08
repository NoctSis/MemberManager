# ==============================================================
#  Round-2 owner corrections (10-15) e2e - x86 host required (SysWOW64).
#    R1  switching card-commission mode re-computes UNSETTLED commission rows
#        (original row never deleted: mirror reversal + new row)
#    R2  SETTLED rows are never touched by a mode switch
#    M3  balance overfill is CHANGE not rejection: BalanceUsed=receivable,
#        BalanceBack=overage, member balance debited by receivable only
#    M7  no usable tier left -> brand new member gets a BLANK level (no stale fallback)
#    M9  ExportRecordsRange honours the date range and reports Count / Truncated
#  PURE ASCII source - CJK literals are built from code points.
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_mlbatch3_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$script:fail = 0

function L($m) { $log.Add($m) | Out-Null; Write-Output $m }
function Chk($name, [bool]$ok) {
    if ($ok) { L ('PASS ' + $name) } else { L ('FAIL ' + $name); $script:fail++ }
}
function Save-Log { [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($true))) }
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

# ---- CJK literals (code points) ----
$tMgr  = CStr @(0x5E97, 0x957F)          # shop manager
$tCash = CStr @(0x73B0, 0x91D1)          # cash
$tSell = CStr @(0x552E, 0x5361)          # sell-card (orders.kind)
$tCsm  = CStr @(0x6D88, 0x8D39)          # consume (orders.kind)

try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_mlbatch3_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $tMgr)
    L ('temp db: ' + $tempDb)

    $svc  = New-Object MemberManager.BLL.StoredValueService
    $card = New-Object MemberManager.BLL.CardService
    $pos  = New-Object MemberManager.BLL.PosService
    $cs   = New-Object MemberManager.BLL.CommissionService
    $ms   = New-Object MemberManager.BLL.MemberService
    $lv   = New-Object MemberManager.BLL.LevelService
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    function X([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; [void]$c.ExecuteNonQuery() }
    function Sum([string]$sql) { $v = S $sql; if ($null -eq $v -or $v -is [System.DBNull]) { return [decimal]0 } return [decimal]$v }

    $cardItemId   = [int](S 'SELECT id FROM service_items WHERE is_card_item=1 AND enabled=1 ORDER BY id LIMIT 1')
    $cardItemName = [string](S ("SELECT name FROM service_items WHERE id=" + $cardItemId))
    $tid          = [int](S ("SELECT id FROM card_templates WHERE item_name='" + $cardItemName + "' AND enabled=1 ORDER BY id LIMIT 1"))
    $normItemId   = [int](S 'SELECT id FROM service_items WHERE card_times=0 AND enabled=1 ORDER BY id LIMIT 1')
    $normPrice    = [decimal](S ("SELECT price FROM service_items WHERE id=" + $normItemId))
    $rechRate     = [decimal](S "SELECT CAST(value AS REAL) FROM settings WHERE key='recharge_commission_rate'")
    $mid1 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1')
    $mid2 = [int](S 'SELECT id FROM members ORDER BY id LIMIT 1 OFFSET 1')
    X 'DELETE FROM card_uses'
    X 'DELETE FROM member_cards'
    L ('  diag: cardItem=' + $cardItemId + ' tpl=' + $tid + ' norm=' + $normItemId + ' rechRate=' + $rechRate)

    # ================= R1: switch re-computes UNSETTLED rows =================
    Chk 'R1a-default-is-use' ($cs.GetCardCommissionMode() -eq 'use')
    # sell a card under "use" -> sell side earns nothing
    [void]$card.SellCard($mid1, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $oidSell = [int](S ("SELECT MAX(id) FROM orders WHERE kind='" + $tSell + "'"))
    Chk 'R1b-sell-earns-nothing-in-use' ([int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $oidSell)) -eq 0)
    $sellRecv = [decimal](S ("SELECT receivable FROM orders WHERE id=" + $oidSell))

    # switch to "sale" -> the UNSETTLED sell order is re-computed immediately
    [void]$cs.SaveCardCommissionMode('sale', 'probe')
    Chk 'R1c-mode-persisted' ($cs.GetCardCommissionMode() -eq 'sale')
    $nSell = [int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $oidSell))
    $sumSell = Sum ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $oidSell)
    $expSell = [math]::Round($sellRecv * $rechRate, 2)
    Chk 'R1d-unsettled-recomputed' ($nSell -eq 1)
    Chk 'R1e-recomputed-value' ([math]::Abs($sumSell - $expSell) -lt 0.005)
    L ('  diag: oidSell=' + $oidSell + ' recv=' + $sellRecv + ' comm=' + $sumSell + ' exp=' + $expSell)
    # and switching back reverses it again (never deleted, always mirrored)
    [void]$cs.SaveCardCommissionMode('use', 'probe')
    $sumBack = Sum ("SELECT IFNULL(SUM(commission),0) FROM commissions WHERE order_id=" + $oidSell)
    $nBack = [int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $oidSell))
    Chk 'R1f-switch-back-nets-to-zero' ([math]::Abs($sumBack) -lt 0.005)
    # original row kept, reversal added, nothing deleted (1 original + 1 reversal)
    Chk 'R1g-rows-only-added' ($nBack -eq 2)

    # ================= R2: SETTLED rows are never touched =================
    [void]$cs.SaveCardCommissionMode('sale', 'probe')
    $oidSell2 = 0
    [void]$card.SellCard($mid2, $tid, $tCash, 'probe', 10, [decimal]30, 0, [decimal]0, 0)
    $oidSell2 = [int](S ("SELECT MAX(id) FROM orders WHERE kind='" + $tSell + "'"))
    $before = [int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $oidSell2))
    X "UPDATE commissions SET settled=1 WHERE order_id=$oidSell2"
    [void]$cs.SaveCardCommissionMode('split', 'probe')
    $after = [int](S ("SELECT COUNT(*) FROM commissions WHERE order_id=" + $oidSell2))
    Chk 'R2a-settled-row-untouched' ($before -eq $after -and $before -eq 1)
    Chk 'R2b-mode-now-split' ($cs.GetCardCommissionMode() -eq 'split')

    # ================= M3: balance overfill is CHANGE, not rejection =================
    $bal0 = [decimal](S ("SELECT balance FROM members WHERE id=" + $mid1))
    X ("UPDATE members SET balance=500 WHERE id=" + $mid1)
    $lines = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
    $c1 = New-Object MemberManager.BLL.PosService+CartLine
    $c1.ItemId = $normItemId; $c1.Qty = 1; $c1.ByCard = $false; $c1.Name = 'ML3Item'
    $c1.Price = $normPrice; $c1.Unit = (CStr @(0x6B21)); $c1.IsCardItem = $false
    $lines.Add($c1)
    # payBalance 200 against a much smaller receivable -> the surplus is change, not an error
    $okM3 = $true
    try { $r3 = $pos.Checkout($lines, $mid1, [decimal]200, [decimal]0, $null, 1) } catch { $okM3 = $false; L ('  M3 threw: ' + $_.Exception.Message) }
    Chk 'M3a-overfill-not-rejected' $okM3
    if ($okM3) {
        Chk 'M3b-balance-capped-at-receivable' ([math]::Abs([decimal]$r3.BalanceUsed - [decimal]$r3.Receivable) -lt 0.005)
        Chk 'M3c-change-returned' ([math]::Abs([decimal]$r3.BalanceBack - (200 - [decimal]$r3.Receivable)) -lt 0.005)
        Chk 'M3d-member-debited-once' ([math]::Abs([decimal](S ("SELECT balance FROM members WHERE id=" + $mid1)) - (500 - [decimal]$r3.Receivable)) -lt 0.005)
        L ('  diag: recv=' + $r3.Receivable + ' used=' + $r3.BalanceUsed + ' back=' + $r3.BalanceBack)
    }
    X ("UPDATE members SET balance=" + [string]$bal0 + " WHERE id=" + $mid1)

    # ================= M7: no usable tier -> blank level =================
    foreach ($d in $lv.List()) { $lv.Save([int]$d.Id, [string]$d.Name, [decimal]$d.MinRecharge, $false) }
    Chk 'M7a-all-tiers-disabled' ([int](S 'SELECT COUNT(*) FROM member_levels WHERE enabled=1') -eq 0)
    $mn = $ms.CreateMember('ML3NoTier', '13800001999', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $lvNew = [string](S ("SELECT IFNULL(level,'') FROM members WHERE id=" + [int]$mn.Id))
    Chk 'M7b-new-member-level-blank' ($lvNew -eq '')
    Chk 'M7c-no-crash-on-resolve' ([int](S ("SELECT COUNT(*) FROM members WHERE id=" + [int]$mn.Id)) -eq 1)
    # restore one tier: a brand-new member must land on it again
    $first = ($lv.List())[0]
    $lv.Save([int]$first.Id, [string]$first.Name, [decimal]$first.MinRecharge, $true)
    $mn2 = $ms.CreateMember('ML3Tier2', '13800001998', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    Chk 'M7d-restored-tier-applied' ([string](S ("SELECT IFNULL(level,'') FROM members WHERE id=" + [int]$mn2.Id)) -eq [string]$first.Name)
    L ('  diag: newLevel=[' + $lvNew + '] restored=' + $first.Name + ' -> ' + (S ("SELECT IFNULL(level,'') FROM members WHERE id=" + [int]$mn2.Id)))

    # ================= M9: segmented export =================
    $outDir = Join-Path $env:TEMP ('mm_mlbatch3_exp_' + [Guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($outDir)
    $full = $svc.ExportRecords('probe', $outDir)
    Chk 'M9a-full-export-has-rows' ([int]$full.Count -gt 0)
    Chk 'M9b-full-export-not-truncated' (-not $full.Truncated)
    # a range far in the past must contain fewer rows than the full export
    $far = $svc.ExportRecordsRange('probe', $outDir, '1999-01-01', '1999-12-31')
    Chk 'M9c-empty-range-zero-rows' ([int]$far.Count -eq 0)
    Chk 'M9d-empty-range-not-truncated' (-not $far.Truncated)
    # a range covering today must contain the rows we just created
    $today = (Get-Date).ToString('yyyy-MM-dd')
    $now = $svc.ExportRecordsRange('probe', $outDir, $today, $today)
    Chk 'M9e-today-range-nonzero' ([int]$now.Count -gt 0)
    Chk 'M9f-today-range-file-written' ([System.IO.File]::Exists($now.Path))
    Chk 'M9g-range-narrower-than-full' ([int]$now.Count -le [int]$full.Count)
    L ('  diag: full=' + $full.Count + ' today=' + $now.Count + ' far=' + $far.Count)
    # reversed range is nonsense but must not crash
    $bad = $svc.ExportRecordsRange('probe', $outDir, '2030-01-01', '2000-01-01')
    Chk 'M9h-reversed-range-no-crash' ([int]$bad.Count -ge 0)

    $conn.Close()
}
catch {
    L ('ERROR: ' + $_.Exception.Message)
    if ($_.Exception.InnerException) { L ('  inner: ' + $_.Exception.InnerException.Message) }
    L ('  at: ' + $_.InvocationInfo.PositionMessage)
    $script:fail++
}
if ($script:fail -eq 0) { L 'SMOKE OK' } else { L ('FAILURES=' + $script:fail); L ('SMOKE FAIL ' + $script:fail) }
Save-Log
if ($script:fail -gt 0) { exit 1 }
